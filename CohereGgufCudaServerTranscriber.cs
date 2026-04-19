using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Local Cohere Transcribe inference via a persistent crispasr-server process
    /// running on CUDA. Port 8767. Lazy-started on first call.
    ///
    /// Supports two upload modes:
    ///  - TranscribeAsync(string filePath, ...): reads WAV from disk
    ///  - TranscribeAsync(byte[] wavBytes, ...): uploads WAV bytes directly
    ///    from memory (saves ~20-30ms of disk I/O per call).
    /// </summary>
    public sealed class CohereGgufCudaServerTranscriber : IDisposable
    {
        private static readonly string ModelFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".WhisperInk", "cohere-gguf-cuda");

        private static readonly string ExePath      = Path.Combine(ModelFolder, "crispasr.exe");
        private static readonly string DefaultModel = Path.Combine(ModelFolder, "cohere-transcribe-q5_0.gguf");

        private const string ServerHost = "127.0.0.1";
        private const int    ServerPort = 8767;
        private static readonly string ServerBaseUrl = $"http://{ServerHost}:{ServerPort}";
        private static readonly string InferenceUrl  = $"{ServerBaseUrl}/v1/audio/transcriptions";
        private static readonly string HealthUrl     = $"{ServerBaseUrl}/health";

        private readonly string _exePath;
        private readonly string _modelPath;
        private readonly int    _threads;

        private Process? _serverProc;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _serverReady;
        private bool _disposed;

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        public CohereGgufCudaServerTranscriber() : this(ExePath, DefaultModel) { }

        public CohereGgufCudaServerTranscriber(string exePath, string modelPath, int threads = 0)
        {
            _exePath   = exePath;
            _modelPath = modelPath;
            _threads   = threads <= 0 ? Math.Min(8, Environment.ProcessorCount) : threads;
        }

        public bool ModelFilesExist()
        {
            if (!File.Exists(_exePath)) return false;
            if (File.Exists(_modelPath)) return true;
            if (!Directory.Exists(ModelFolder)) return false;
            foreach (var _ in Directory.EnumerateFiles(ModelFolder, "cohere-transcribe-*.gguf"))
                return true;
            return false;
        }

        /// <summary>File-path overload — reads WAV from disk, then uploads.</summary>
        public async Task<string?> TranscribeAsync(string filePath, string language = "en", IReadOnlyList<string>? biasTerms = null)
        {
            if (_disposed) return null;
            if (!File.Exists(filePath)) return null;

            try
            {
                if (!await EnsureServerRunningAsync().ConfigureAwait(false))
                    return null;

                using var fileStream = File.OpenRead(filePath);
                var fileContent = new StreamContent(fileStream);
                return await PostMultipartAsync(fileContent, language, biasTerms).ConfigureAwait(false);
            }
            catch
            {
                _serverReady = false;
                return null;
            }
        }

        /// <summary>
        /// In-memory overload — uploads WAV bytes directly. Saves ~20-30ms vs.
        /// writing to disk and reading back.
        /// </summary>
        public async Task<string?> TranscribeAsync(byte[] wavBytes, string language = "en", IReadOnlyList<string>? biasTerms = null)
        {
            if (_disposed) return null;
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                if (!await EnsureServerRunningAsync().ConfigureAwait(false))
                    return null;

                var fileContent = new ByteArrayContent(wavBytes);
                return await PostMultipartAsync(fileContent, language, biasTerms).ConfigureAwait(false);
            }
            catch
            {
                _serverReady = false;
                return null;
            }
        }

        private async Task<string?> PostMultipartAsync(HttpContent fileContent, string language, IReadOnlyList<string>? biasTerms)
        {
            using (fileContent)
            {
                fileContent.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/wav");

                using var content = new MultipartFormDataContent();
                content.Add(new StringContent("cohere"), "model");
                content.Add(new StringContent(string.IsNullOrWhiteSpace(language) ? "en" : language), "language");
                if (biasTerms != null && biasTerms.Count > 0)
                    content.Add(new StringContent(string.Join(", ", biasTerms)), "prompt");
                content.Add(fileContent, "file", "audio.wav");

                using var response = await _http.PostAsync(InferenceUrl, content).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textEl))
                    return textEl.GetString()?.Trim();

                return null;
            }
        }

        private async Task<bool> EnsureServerRunningAsync()
        {
            if (_serverReady && IsProcessAlive(_serverProc)) return true;

            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_serverReady && IsProcessAlive(_serverProc)) return true;

                KillServer();

                if (!File.Exists(_exePath)) return false;

                string modelPath = _modelPath;
                if (!File.Exists(modelPath))
                {
                    if (!Directory.Exists(ModelFolder)) return false;
                    bool found = false;
                    foreach (var f in Directory.EnumerateFiles(ModelFolder, "cohere-transcribe-*.gguf"))
                    {
                        modelPath = f;
                        found = true;
                        break;
                    }
                    if (!found) return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName               = _exePath,
                    WorkingDirectory       = ModelFolder,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                psi.ArgumentList.Add("--server");
                psi.ArgumentList.Add("--host");    psi.ArgumentList.Add(ServerHost);
                psi.ArgumentList.Add("--port");    psi.ArgumentList.Add(ServerPort.ToString());
                psi.ArgumentList.Add("-m");        psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add("cohere");
                psi.ArgumentList.Add("-l");        psi.ArgumentList.Add("en");
                psi.ArgumentList.Add("-t");        psi.ArgumentList.Add(_threads.ToString());
                psi.ArgumentList.Add("-np");

                _serverProc = new Process { StartInfo = psi };
                if (!_serverProc.Start()) return false;

                _ = Task.Run(async () =>
                {
                    try { await _serverProc.StandardOutput.ReadToEndAsync().ConfigureAwait(false); }
                    catch { }
                });
                _ = Task.Run(async () =>
                {
                    try { await _serverProc.StandardError.ReadToEndAsync().ConfigureAwait(false); }
                    catch { }
                });

                // CUDA startup can be slower than CPU on first launch due to
                // kernel compilation — give it longer.
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (!IsProcessAlive(_serverProc)) return false;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        using var resp = await _http.GetAsync(HealthUrl, cts.Token).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            _serverReady = true;
                            return true;
                        }
                    }
                    catch { }
                    await Task.Delay(200).ConfigureAwait(false);
                }

                KillServer();
                return false;
            }
            finally
            {
                _startLock.Release();
            }
        }

        private static bool IsProcessAlive(Process? p)
        {
            if (p == null) return false;
            try { return !p.HasExited; }
            catch { return false; }
        }

        private void KillServer()
        {
            _serverReady = false;
            var p = _serverProc;
            _serverProc = null;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            KillServer();
        }
    }
}