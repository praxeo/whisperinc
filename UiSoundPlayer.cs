// UiSoundPlayer.cs
// Low-latency UI chirps.
//
// The old path was System.Media.SoundPlayer.PlaySync() on a freshly
// synthesised MemoryStream. Measured on the desktop: a *30 ms* tone took
// 190-222 ms end to end, because winmm's PlaySound opens and tears down the
// render endpoint on every single call. That is the "lag on the beep" — the
// user pressed the hotkey and heard confirmation a fifth of a second later,
// which reads as "the app is slow to start recording" even when it isn't.
//
// This class keeps ONE WaveOutEvent open and feeds pre-synthesised tones into
// a BufferedWaveProvider. Measured on the same machine: enqueue is
// 0.02-0.46 ms, so the only remaining delay is DesiredLatency (60 ms of
// output buffering) — a ~3x improvement, and below the threshold where a
// confirmation tone reads as "delayed" rather than "immediate".
//
// Holding the device open has one failure mode: WAVE_MAPPER binds to whatever
// the default render endpoint was at open time, so plugging in headphones
// mid-session would keep chirping at the speakers. Querying the current
// default endpoint costs ~3 ms (measured), which is cheap enough to check
// before every tone, so we do — and reopen when it changed.

using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WhisperInk
{
    public enum UiSound
    {
        Start,      // hotkey pressed, capture is live
        Stop,       // hotkey released, audio is being transcribed
        Success,    // text pasted
        Error,      // transcription genuinely failed
        Dismissed,  // input deliberately discarded (too short / silent) — not an error
    }

    public sealed class UiSoundPlayer : IDisposable
    {
        private static readonly WaveFormat Format = new WaveFormat(44100, 16, 1);

        // 60 ms of output buffering. Lower starts glitching on the shared
        // WASAPI mix; higher re-introduces audible lag.
        private const int DesiredLatencyMs = 60;

        private readonly Action<string> _log;
        private readonly Dictionary<UiSound, byte[]> _tones = new();

        // Serialises open/reopen/enqueue. Held only on the thread-pool thread
        // that Play() dispatches to, never on the UI thread.
        private readonly object _gate = new();

        private WaveOutEvent? _out;
        private BufferedWaveProvider? _buffer;
        private MMDeviceEnumerator? _enumerator;
        private string _endpointId = "";
        private bool _endpointQueryBroken;
        private bool _disposed;

        public UiSoundPlayer(Action<string>? log = null)
        {
            _log = log ?? (_ => { });

            // Synthesised once at construction — the tones never change, and
            // regenerating ~1 kB of sine on every keypress was pointless work
            // on the latency-critical path.
            _tones[UiSound.Start]     = Synth(1200, 30, 0.30);
            _tones[UiSound.Stop]      = Synth(800, 30, 0.30);
            _tones[UiSound.Success]   = Synth(1600, 50, 0.30);
            _tones[UiSound.Error]     = Synth(300, 120, 0.30);
            // Deliberately quieter and lower than Error: "I saw the press and
            // threw it away" should not sound like "something broke".
            _tones[UiSound.Dismissed] = Synth(520, 28, 0.14);
        }

        /// <summary>Fire-and-forget. Returns immediately; the tone is opened,
        /// enqueued and played on a thread-pool thread so no caller — least of
        /// all the UI thread mid-hotkey — ever blocks on audio.</summary>
        public void Play(UiSound sound)
        {
            if (_disposed) return;
            ThreadPool.QueueUserWorkItem(_ => PlayCore(sound));
        }

        private void PlayCore(UiSound sound)
        {
            try
            {
                if (!_tones.TryGetValue(sound, out var tone)) return;
                lock (_gate)
                {
                    if (_disposed) return;
                    if (!EnsureOpen()) return;
                    // Drop anything still queued: chirps are status signals, and
                    // a backlog of stale ones is worse than a missed one.
                    _buffer!.ClearBuffer();
                    _buffer.AddSamples(tone, 0, tone.Length);
                }
            }
            catch (Exception ex) { _log($"[sound] {sound} failed: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Opens the render device if needed, or reopens it when the
        /// default endpoint has changed underneath us (headphones plugged in).
        /// Caller must hold <see cref="_gate"/>.</summary>
        private bool EnsureOpen()
        {
            string current = CurrentEndpointId();
            if (_out != null && current == _endpointId) return true;

            if (_out != null)
            {
                _log($"[sound] default output device changed — reopening");
                CloseDevice();
            }

            try
            {
                _buffer = new BufferedWaveProvider(Format)
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                    // Keeps Read() returning zero-filled buffers when idle, so
                    // the device stays in Playing state between chirps instead
                    // of stopping and needing a ~110 ms reopen.
                    ReadFully = true,
                };
                _out = new WaveOutEvent { DesiredLatency = DesiredLatencyMs, NumberOfBuffers = 3 };
                _out.Init(_buffer);
                _out.Play();
                _endpointId = current;
                return true;
            }
            catch (Exception ex)
            {
                _log($"[sound] output device open failed: {ex.GetType().Name}: {ex.Message}");
                CloseDevice();
                return false;
            }
        }

        /// <summary>~3 ms COM query (measured). Empty string when the endpoint
        /// can't be determined — in which case we stop asking and keep whatever
        /// device we opened, rather than reopening on every chirp.</summary>
        private string CurrentEndpointId()
        {
            if (_endpointQueryBroken) return _endpointId;
            try
            {
                _enumerator ??= new MMDeviceEnumerator();
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            }
            catch (Exception ex)
            {
                _endpointQueryBroken = true;
                _log($"[sound] endpoint query unavailable ({ex.GetType().Name}) — pinning to the opened device");
                return _endpointId;
            }
        }

        private void CloseDevice()
        {
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            _buffer = null;
            _endpointId = "";
        }

        /// <summary>16-bit mono PCM sine with a short attack ramp and a linear
        /// decay. The ramp matters: the previous synth started the envelope at
        /// full amplitude, which put a click in front of every chirp.</summary>
        private static byte[] Synth(double freq, int durMs, double amplitude)
        {
            int sampleRate = Format.SampleRate;
            int samples = sampleRate * durMs / 1000;
            var bytes = new byte[samples * 2];
            double durSec = durMs / 1000.0;
            const double attackSec = 0.004;

            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / sampleRate;
                double attack = attackSec > 0 ? Math.Min(1.0, t / attackSec) : 1.0;
                double decay = Math.Max(0, 1.0 - t / durSec);
                short s = (short)(Math.Sin(2 * Math.PI * freq * t) * attack * decay * amplitude * short.MaxValue);
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return bytes;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_gate)
            {
                CloseDevice();
                try { _enumerator?.Dispose(); } catch { }
                _enumerator = null;
            }
        }
    }
}
