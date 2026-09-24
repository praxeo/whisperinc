// MicCapture.cs
// Warm microphone with a pre-roll ring buffer.
//
// The old path built a WaveInEvent, opened the device and called
// StartRecording() inside the hotkey handler. Measured on the desktop:
// StartRecording() returns in ~7 ms but the first audio callback lands
// 131-159 ms later, and StopRecording()+Dispose() cost another 112-134 ms in
// the real pipeline log. Everything the user said between pressing the key and
// the capture stream actually engaging was simply gone — that is the clipped
// first syllable.
//
// The fix is to stop opening the device per dictation. The mic is held open
// ("warm") and every buffer flows into a small circular buffer regardless of
// whether we are recording. Pressing the hotkey no longer starts a device: it
// creates a writer, seeds it with the last few hundred milliseconds ALREADY
// captured, and flips a flag. So the recording now begins slightly BEFORE the
// keypress and it is structurally impossible to clip the onset.
//
// Because the device is never stopped mid-dictation, the 112-134 ms teardown
// disappears from the stop path too.
//
// Cost of keeping it warm: Windows shows the microphone-in-use indicator for
// as long as the device is held. Hence Release() and the caller's idle timer —
// after a quiet spell the device is genuinely closed, and the next press pays
// the old cold-open latency once.

using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace WhisperInk
{
    public sealed class MicCapture : IDisposable
    {
        // 16 kHz mono 16-bit: what every ASR backend here wants, and what the
        // WAVs have always been written at.
        private static readonly WaveFormat Format = new WaveFormat(16000, 1);
        private const int BytesPerMs = 16000 * 2 / 1000; // 32

        // Smaller than NAudio's 100 ms default so the pre-roll ring tracks the
        // present more closely and the drain at stop is shorter.
        private const int BufferMilliseconds = 50;

        private readonly Func<int> _deviceNumber;
        private readonly Func<int> _preRollMs;
        private readonly Action<string> _log;
        // Raised on the capture thread when the device dies MID-dictation, so
        // the user hears about it while still talking, not after release.
        private readonly Action? _onCaptureLost;

        // Guards the ring, the writer and _capturing together. Taken by the
        // NAudio callback thread and by the UI thread; never held across a
        // wait, so the two can't deadlock.
        private readonly object _gate = new();

        private WaveInEvent? _waveIn;
        private PreRollRing _ring = new(0);

        private MemoryStream? _memStream;
        private WaveFileWriter? _writer;
        // Gets every byte written to the take's WAV, in the same order — the
        // pre-roll seed, then each buffer — for a take streamed to its
        // provider while it is still being recorded. Called under _gate, so it
        // must not block (StreamedTranscription.Append copies and queues). A
        // sink that throws is dropped; the WAV never depends on it.
        private Action<byte[], int, int>? _sink;
        private bool _capturing;
        private bool _captureInterrupted;
        private ManualResetEventSlim? _drainSignal;

        private bool _disposed;

        public MicCapture(Func<int> deviceNumber, Func<int> preRollMs, Action<string>? log = null, Action? onCaptureLost = null)
        {
            _deviceNumber = deviceNumber;
            _preRollMs = preRollMs;
            _log = log ?? (_ => { });
            _onCaptureLost = onCaptureLost;
        }

        public bool IsWarm { get { lock (_gate) return _waveIn != null; } }

        /// <summary>True when the device failed during the capture the most
        /// recent <see cref="EndCapture"/> returned: that WAV stops where the
        /// mic did. It used to be handed on as if complete, so everything said
        /// after an unplug or driver reset simply wasn't in the note.</summary>
        public bool LastCaptureInterrupted { get; private set; }

        /// <summary>Opens the capture device if it isn't already streaming.
        /// Returns false if the device could not be opened.</summary>
        public bool EnsureOpen()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (_waveIn != null) return true;

                try
                {
                    int device = _deviceNumber();
                    var waveIn = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = BufferMilliseconds };
                    if (device >= 0 && device < WaveIn.DeviceCount) waveIn.DeviceNumber = device;

                    _ring = new PreRollRing(Math.Max(BufferMilliseconds, _preRollMs()) * BytesPerMs);

                    waveIn.DataAvailable += OnDataAvailable;
                    waveIn.RecordingStopped += OnRecordingStopped;
                    waveIn.StartRecording();
                    _waveIn = waveIn;
                    return true;
                }
                catch (Exception ex)
                {
                    _log($"[mic] open failed: {ex.GetType().Name}: {ex.Message}");
                    _waveIn = null;
                    return false;
                }
            }
        }

        /// <summary>Closes the device and drops the pre-roll. Refused while a
        /// dictation is in flight.</summary>
        public void Release()
        {
            WaveInEvent? toDispose;
            lock (_gate)
            {
                if (_capturing || _waveIn == null) return;
                toDispose = _waveIn;
                _waveIn = null;
                _ring.Clear();
            }
            try { toDispose.StopRecording(); } catch { }
            try { toDispose.Dispose(); } catch { }
        }

        /// <summary>Re-opens the device on the currently selected index, if it
        /// was warm. Called when the user picks a different microphone.</summary>
        public void DeviceChanged()
        {
            bool wasWarm = IsWarm;
            Release();
            if (wasWarm) EnsureOpen();
        }

        /// <summary>Starts writing to a fresh WAV, seeded with the pre-roll
        /// already sitting in the ring. Returns the milliseconds of pre-press
        /// audio recovered (0 on a cold device). The device open, the writer
        /// creation and the pre-roll copy all happen under one lock, so a
        /// buffer arriving mid-setup cannot slip through the gap between
        /// "seeded the ring" and "started capturing". <paramref name="sink"/>,
        /// if given, gets the same bytes as the WAV until EndCapture.</summary>
        public int BeginCapture(Action<byte[], int, int>? sink = null)
        {
            if (!EnsureOpen()) return -1;

            lock (_gate)
            {
                if (_capturing) return 0;

                DisposeWriterLocked();
                _memStream = new MemoryStream();
                _writer = new WaveFileWriter(new IgnoreDisposeStream(_memStream), Format);
                _sink = sink;

                var writer = _writer;
                int preRollBytes = _ring.CopyNewest(
                    _preRollMs() * BytesPerMs,
                    (buf, offset, count) =>
                    {
                        writer.Write(buf, offset, count);
                        FeedSinkLocked(buf, offset, count);
                    });

                _captureInterrupted = false;
                _capturing = true;
                return preRollBytes / BytesPerMs;
            }
        }

        /// <summary>Stops capturing and returns the finished WAV bytes.
        ///
        /// Waits (bounded by <paramref name="postRollMs"/>) for one more buffer
        /// to land first. Without it the up-to-50 ms still in flight at the key
        /// release would be dropped — we'd have fixed the clipped start by
        /// introducing a clipped end. Blocks up to postRollMs, so call it off
        /// the UI thread.</summary>
        public byte[]? EndCapture(int postRollMs)
        {
            ManualResetEventSlim? signal = null;
            lock (_gate)
            {
                if (!_capturing) return null;
                if (postRollMs > 0 && _waveIn != null)
                {
                    signal = new ManualResetEventSlim(false);
                    _drainSignal = signal;
                }
            }

            // Outside the lock — the callback needs it to deliver that buffer.
            signal?.Wait(postRollMs);

            lock (_gate)
            {
                _drainSignal = null;
                _capturing = false;
                _sink = null;
                LastCaptureInterrupted = _captureInterrupted;
                _captureInterrupted = false;
                try
                {
                    _writer?.Dispose();
                    byte[]? bytes = _memStream?.ToArray();
                    return bytes is { Length: > 0 } ? bytes : null;
                }
                catch (Exception ex)
                {
                    _log($"[mic] WAV flush failed: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
                finally
                {
                    _writer = null;
                    try { _memStream?.Dispose(); } catch { }
                    _memStream = null;
                }
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs a)
        {
            if (a.BytesRecorded <= 0) return;
            lock (_gate)
            {
                if (_capturing)
                {
                    try { _writer?.Write(a.Buffer, 0, a.BytesRecorded); } catch { }
                    FeedSinkLocked(a.Buffer, 0, a.BytesRecorded);
                }
                _ring.Write(a.Buffer, a.BytesRecorded);
                // Signals EndCapture that the in-flight tail has landed.
                _drainSignal?.Set();
            }
        }

        private void FeedSinkLocked(byte[] buffer, int offset, int count)
        {
            if (_sink == null) return;
            try { _sink(buffer, offset, count); }
            catch (Exception ex)
            {
                // The stream then comes up short at release and the take goes
                // out as its WAV instead.
                _sink = null;
                _log($"[mic] the take's upload stream failed and was detached ({ex.GetType().Name}: {ex.Message}); the recording is unaffected");
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception == null) return;
            // Unplugged USB mic, driver reset, device disabled. Drop to cold so
            // the next BeginCapture re-opens rather than silently recording
            // nothing forever.
            _log($"[mic] capture stopped unexpectedly: {e.Exception.GetType().Name}: {e.Exception.Message}");
            bool midDictation;
            lock (_gate)
            {
                _waveIn = null;
                _ring.Clear();
                // Mid-dictation, the writer keeps what arrived before the
                // failure; flag the take so it can't pass for a complete one.
                midDictation = _capturing;
                if (midDictation) _captureInterrupted = true;
            }
            if (midDictation)
            {
                try { _onCaptureLost?.Invoke(); } catch { }
            }
        }

        private void DisposeWriterLocked()
        {
            _sink = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            try { _memStream?.Dispose(); } catch { }
            _memStream = null;
        }

        // Measuring a finished take (peak, RMS, sustained speech) lives in
        // SpeechDetector, where the harness can test it without a microphone.

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            WaveInEvent? toDispose;
            lock (_gate)
            {
                _capturing = false;
                DisposeWriterLocked();
                toDispose = _waveIn;
                _waveIn = null;
            }
            try { toDispose?.StopRecording(); } catch { }
            try { toDispose?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Fixed-size circular byte buffer holding the most recent audio, so a
    /// recording can be seeded with sound captured before the user pressed
    /// anything.
    ///
    /// Split out of <see cref="MicCapture"/> purely so the wrap arithmetic is
    /// testable without a microphone: an off-by-one here would not crash, it
    /// would quietly corrupt or mis-order the first few hundred milliseconds of
    /// every dictation — the hardest possible failure to notice by ear.
    ///
    /// Not thread-safe; MicCapture calls it under its own lock.
    /// </summary>
    public sealed class PreRollRing
    {
        private readonly byte[] _buf;
        private int _pos;     // next write index
        private int _filled;  // valid bytes, <= _buf.Length

        public PreRollRing(int capacityBytes)
        {
            _buf = new byte[Math.Max(0, capacityBytes)];
        }

        public int Capacity => _buf.Length;
        public int Filled => _filled;

        public void Clear()
        {
            _pos = 0;
            _filled = 0;
        }

        public void Write(byte[] source, int count)
        {
            if (_buf.Length == 0 || count <= 0) return;
            if (count > source.Length) count = source.Length;

            int offset = 0;
            // A write larger than the whole ring can only leave its tail.
            if (count > _buf.Length)
            {
                offset = count - _buf.Length;
                count = _buf.Length;
            }

            int firstChunk = Math.Min(count, _buf.Length - _pos);
            Buffer.BlockCopy(source, offset, _buf, _pos, firstChunk);
            if (count > firstChunk)
                Buffer.BlockCopy(source, offset + firstChunk, _buf, 0, count - firstChunk);

            _pos = (_pos + count) % _buf.Length;
            _filled = Math.Min(_buf.Length, _filled + count);
        }

        /// <summary>Hands the newest <paramref name="maxBytes"/> to the sink in
        /// chronological order, as one or two contiguous spans. Returns how many
        /// bytes were emitted.</summary>
        public int CopyNewest(int maxBytes, Action<byte[], int, int> sink)
        {
            if (_buf.Length == 0 || _filled == 0 || maxBytes <= 0) return 0;

            int wanted = Math.Min(_filled, maxBytes);
            // Oldest retained byte, walking back from the write head.
            int start = ((_pos - wanted) % _buf.Length + _buf.Length) % _buf.Length;
            int firstChunk = Math.Min(wanted, _buf.Length - start);
            sink(_buf, start, firstChunk);
            if (wanted > firstChunk) sink(_buf, 0, wanted - firstChunk);
            return wanted;
        }
    }
}
