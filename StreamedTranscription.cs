// StreamedTranscription.cs
// A take whose upload starts at the key-PRESS instead of the release.
//
// Measured 2026-09-23 against ElevenLabs (see CLAUDE.md, "Cloud connections &
// latency"): at release a take still had to open a connection, when it had
// been idle, and upload the whole WAV before the service could start. A 4 s
// take on a fresh connection spent ~166 ms on the connection alone, and every
// minute of audio added ~0.7 s of upload at the desktop's ~2.5 MB/s. Streaming
// the audio up while it is being spoken leaves only the tail, and the
// transcription itself, for the release to wait on.
//
// The request is the ordinary one, field for field (HttpTranscriber builds
// it), except that the file part is raw PCM sent chunked with
// file_format=pcm_s16le_16. Raw PCM is why this works at all: a WAV header
// states the data length up front, and that isn't known until the key comes
// up.
//
// It can only ever make a take faster, never lose or change one:
//   - The WAV is still recorded, journaled and kept exactly as before.
//   - The streamed result is used only if the stream carried byte-for-byte
//     as much audio as the WAV holds. Any mismatch, and any failure other than
//     the take's deadline, sends the WAV the ordinary way instead (FallBack).
//   - A discarded take (a tap, silence) cancels the upload mid-body, so the
//     service never receives a complete request to transcribe or bill.
//   - Nothing here runs on the capture thread except a copy into a queue, and
//     nothing touches the network on the UI thread: HttpClient's first request
//     can block on proxy auto-detection, which on the key-press path would eat
//     audio past the pre-roll.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class StreamedTranscription : IDisposable
    {
        /// <summary>Past this much audio the stream is given up and the take
        /// goes out the ordinary way at release. A 104 s take streamed at
        /// real-time pace worked in testing; how long ElevenLabs' front end
        /// tolerates a slow upload beyond that is unknown, and a long hold
        /// shouldn't be the way to find out.</summary>
        public static readonly TimeSpan MaxStreamedAudio = TimeSpan.FromMinutes(5);

        private const int BytesPerSecond = 16000 * 2;   // 16 kHz mono 16-bit, what MicCapture records

        private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource _cts = new();
        private readonly HttpRequestMessage _request;
        private readonly Task<HttpResponseMessage> _send;
        private readonly Action<string> _log;
        private readonly long _maxBytes = (long)(MaxStreamedAudio.TotalSeconds * BytesPerSecond);

        private long _bytes;
        private volatile string? _abandoned;   // why streaming was given up, once it was
        private int _closed;                   // 1 once finished or cancelled
        private int _disposed;

        /// <summary>The transcriber that opened this stream. A take is only
        /// finished through it if it is still the active provider's
        /// transcriber at release.</summary>
        public HttpTranscriber Transcriber { get; }
        public string ProviderId { get; }

        internal StreamedTranscription(HttpTranscriber transcriber, string providerId, HttpClient http,
                                       HttpRequestMessage request, MultipartFormDataContent fields, Action<string> log)
        {
            Transcriber = transcriber;
            ProviderId = providerId;
            _log = log;
            _request = request;

            fields.Add(new StringContent("pcm_s16le_16"), "file_format");
            var audio = new StreamContent(new QueueReadStream(_audio.Reader));
            audio.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            // Still the last part, like every multipart request here.
            fields.Add(audio, "file", "audio.pcm");
            request.Content = fields;

            // Cancelling fails the audio part's pending read as well as the
            // request, so the body can never be finished off by accident.
            _cts.Token.Register(() => _audio.Writer.TryComplete(new OperationCanceledException(_cts.Token)));

            _send = Task.Run(() => http.SendAsync(request, _cts.Token));
            // Observed even when nobody awaits it (a discarded take).
            _send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _log($"[stream] {ProviderId}: upload opened at the key-press");
        }

        public long BytesStreamed => Interlocked.Read(ref _bytes);

        /// <summary>Takes each buffer as it is written to the take's WAV — the
        /// pre-roll first, then the capture — in order. Called under
        /// MicCapture's lock on the capture thread: copies and queues, never
        /// blocks.</summary>
        public void Append(byte[] buffer, int offset, int count)
        {
            if (count <= 0 || _abandoned != null || Volatile.Read(ref _closed) != 0) return;
            if (Interlocked.Read(ref _bytes) + count > _maxBytes)
            {
                Abandon($"the take passed {MaxStreamedAudio.TotalMinutes:F0} min");
                return;
            }
            var copy = new byte[count];
            Buffer.BlockCopy(buffer, offset, copy, 0, count);
            if (_audio.Writer.TryWrite(copy)) Interlocked.Add(ref _bytes, count);
        }

        private void Abandon(string reason)
        {
            if (_abandoned != null) return;
            _abandoned = reason;
            // Off the capture thread: cancellation runs the HTTP stack's
            // callbacks synchronously, and this is called under MicCapture's lock.
            Task.Run(CancelQuietly);
            _log($"[stream] {ProviderId}: streaming stopped ({reason}); the take goes out as an ordinary upload at release");
        }

        public readonly record struct Outcome(string? Text, bool FallBack, string Reason);

        /// <summary>Ends the upload once the key is up and the capture is
        /// complete, then waits for the transcript under the take's deadline.
        /// FallBack means the stream can't be trusted or failed: send the WAV
        /// the ordinary way. A deadline is final, not a fallback: there is no
        /// time left for a second attempt, and the take is kept for a retry.</summary>
        public async Task<Outcome> FinishAsync(long wavPcmBytes, CancellationToken deadline)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return new(null, true, "already finished");
            if (_abandoned is { } why)
            {
                CancelQuietly();
                return new(null, true, why);
            }

            // Checked BEFORE the body is ended, so a stream missing even one
            // buffer is cancelled rather than transcribed and billed.
            long streamed = Interlocked.Read(ref _bytes);
            if (streamed != wavPcmBytes)
            {
                CancelQuietly();
                return new(null, true, $"the stream carried {streamed} bytes of audio but the take has {wavPcmBytes}");
            }

            _audio.Writer.TryComplete();
            _log($"[stream] {ProviderId}: {streamed} bytes streamed ({streamed / (double)BytesPerSecond:F1} s); waiting for the transcript");
            using var onDeadline = deadline.Register(CancelQuietly);
            try
            {
                using var response = await _send.ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(_cts.Token).ConfigureAwait(false);
                string? text = Transcriber.ReadResponse(response, body);
                return text != null
                    ? new(text, false, "")
                    : new(null, true, $"HTTP {(int)response.StatusCode} or a response without text");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                _log($"HttpTranscriber({ProviderId}): stopped at the take's deadline (streamed upload)");
                return new(null, false, "the take's deadline");
            }
            catch (Exception ex)
            {
                return new(null, true, $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CancelQuietly()
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>A stream that was never finished belongs to a take that
        /// was discarded (a tap, silence) or whose stop path failed. Cancelling
        /// it mid-body means the service never receives a complete request, so
        /// nothing is transcribed or billed.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                CancelQuietly();
                _log($"[stream] {ProviderId}: upload cancelled, the take was not sent");
            }
            // The send has to see the cancellation before its request goes.
            _ = _send.ContinueWith(_ =>
            {
                _request.Dispose();
                _cts.Dispose();
            }, TaskScheduler.Default);
        }

        /// <summary>Length of the PCM in a WAV's data chunk: what a streamed
        /// upload of the same take must have carried. -1 if there is no data
        /// chunk.</summary>
        public static long PcmLength(byte[] wav)
        {
            int at = 12;
            while (at + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                if (id == "data") return Math.Max(0, Math.Min((long)size, wav.Length - at - 8));
                if (size < 0) break;
                at += 8 + size + (size & 1);
            }
            return -1;
        }

        /// <summary>The request body's audio part: hands over the queued
        /// buffers in order, waits while the take is still being spoken, and
        /// ends when the take does.</summary>
        private sealed class QueueReadStream : Stream
        {
            private readonly ChannelReader<byte[]> _reader;
            private byte[]? _current;
            private int _offset;

            public QueueReadStream(ChannelReader<byte[]> reader) => _reader = reader;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

            public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct = default)
            {
                if (destination.Length == 0) return 0;
                while (_current == null || _offset >= _current.Length)
                {
                    // False once the take ended; throws once it was cancelled.
                    if (!await _reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
                    if (_reader.TryRead(out var next))
                    {
                        _current = next;
                        _offset = 0;
                    }
                }
                int n = Math.Min(destination.Length, _current.Length - _offset);
                _current.AsSpan(_offset, n).CopyTo(destination.Span);
                _offset += n;
                return n;
            }
        }
    }
}
