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
    /// Generic adapter for CrispASR's unified `crispasr.exe --server` mode. Lazy-starts
    /// the process on first TranscribeAsync call, keeps it resident, and posts WAV
    /// audio to its OpenAI-compatible /v1/audio/transcriptions endpoint.
    ///
    /// Model-agnostic: CrispASR auto-detects the backend (Parakeet, Canary, Voxtral,
    /// etc.) from the GGUF metadata, so the same class serves every non-Cohere GGUF.
    /// Cohere has its own wrapper (CohereGgufServerTranscriber) only because it was
    /// written before CrispASR unified the server path.
    /// </summary>
    public sealed class CrispAsrServerTranscriber : IDisposable
    {
        private static readonly string ModelFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".WhisperInk", "cohere-gguf");

        private static readonly string ExePath = Path.Combine(ModelFolder, "crispasr.exe");

        private const string ServerHost = "127.0.0.1";

        private readonly string _exePath;
        private readonly string _modelPath;
        private readonly int    _port;
        private readonly int    _threads;
        private readonly string _displayName;
        private readonly string? _backendHint;
        private readonly string? _gpuBackend;

        private readonly string _inferenceUrl;
        private readonly string _healthUrl;

        private Process? _serverProc;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _serverReady;
        private bool _disposed;

        private static readonly HttpClient _http = new()
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        public CrispAsrServerTranscriber(string modelGlob, int port, string displayName, int threads = 0, string? backendHint = null, string? gpuBackend = null)
            : this(ExePath, ResolveModel(modelGlob), port, displayName, threads, backendHint, gpuBackend) { }

        public CrispAsrServerTranscriber(string exePath, string modelPath, int port, string displayName, int threads = 0, string? backendHint = null, string? gpuBackend = null)
        {
            _exePath     = exePath;
            _modelPath   = modelPath;
            _port        = port;
            _threads     = threads <= 0 ? Math.Min(8, Environment.ProcessorCount) : threads;
            _displayName = displayName;
            _backendHint = backendHint;
            _gpuBackend  = string.IsNullOrWhiteSpace(gpuBackend) ? null : gpuBackend.Trim().ToLowerInvariant();
            _inferenceUrl = $"http://{ServerHost}:{_port}/v1/audio/transcriptions";
            _healthUrl    = $"http://{ServerHost}:{_port}/health";
        }

        /// <summary>The --gpu-backend value actually passed to crispasr, or null to let it auto-pick.</summary>
        public string? GpuBackend => _gpuBackend;

        public string ExeFolder => ModelFolder;
        public string ModelPath => _modelPath;
        public int    Port      => _port;

        public bool ModelFilesExist()
        {
            if (!File.Exists(_exePath)) return false;
            return File.Exists(_modelPath);
        }

        public string DiagnoseMissing()
        {
            if (!File.Exists(_exePath)) return $"crispasr.exe not found at {_exePath}";
            if (!File.Exists(_modelPath)) return $"Model GGUF not found at {_modelPath}";
            return "";
        }

        public async Task<string?> TranscribeAsync(string filePath, string language = "en", string? prompt = null)
        {
            if (_disposed) return null;
            if (!File.Exists(filePath)) return null;

            try
            {
                if (!await EnsureServerRunningAsync().ConfigureAwait(false))
                    return null;

                using var fileStream = File.OpenRead(filePath);
                var fileContent = new StreamContent(fileStream);
                return await PostMultipartAsync(fileContent, language, prompt).ConfigureAwait(false);
            }
            catch
            {
                _serverReady = false;
                return null;
            }
        }

        public async Task<string?> TranscribeAsync(byte[] wavBytes, string language = "en", string? prompt = null)
        {
            if (_disposed) return null;
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                if (!await EnsureServerRunningAsync().ConfigureAwait(false))
                    return null;

                var fileContent = new ByteArrayContent(wavBytes);
                return await PostMultipartAsync(fileContent, language, prompt).ConfigureAwait(false);
            }
            catch
            {
                _serverReady = false;
                return null;
            }
        }

        private async Task<string?> PostMultipartAsync(HttpContent fileContent, string language, string? prompt = null)
        {
            using (fileContent)
            {
                fileContent.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/wav");

                using var content = new MultipartFormDataContent();
                if (!string.IsNullOrWhiteSpace(language))
                    content.Add(new StringContent(language), "language");
                if (!string.IsNullOrWhiteSpace(prompt))
                    content.Add(new StringContent(prompt), "prompt");
                content.Add(new StringContent("json"), "response_format");
                content.Add(fileContent, "file", "audio.wav");

                using var response = await _http.PostAsync(_inferenceUrl, content).ConfigureAwait(false);
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

                if (!File.Exists(_exePath))   return false;
                if (!File.Exists(_modelPath)) return false;

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
                psi.ArgumentList.Add("--host"); psi.ArgumentList.Add(ServerHost);
                psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(_port.ToString());
                psi.ArgumentList.Add("-m");     psi.ArgumentList.Add(_modelPath);
                psi.ArgumentList.Add("-t");     psi.ArgumentList.Add(_threads.ToString());
                psi.ArgumentList.Add("-np");
                if (!string.IsNullOrWhiteSpace(_backendHint))
                {
                    psi.ArgumentList.Add("--backend");
                    psi.ArgumentList.Add(_backendHint);
                }
                if (!string.IsNullOrWhiteSpace(_gpuBackend))
                {
                    // Recognized values per v0.4.12: "auto" | "cuda" | "vulkan" | "metal" | "cpu".
                    // Pass-through only; "auto" or null omits the flag (server default).
                    if (_gpuBackend == "cpu")
                    {
                        // Force CPU by disabling GPU outright — more robust than --gpu-backend cpu
                        // on some driver combinations.
                        psi.ArgumentList.Add("-ng");
                    }
                    else if (_gpuBackend != "auto")
                    {
                        psi.ArgumentList.Add("--gpu-backend");
                        psi.ArgumentList.Add(_gpuBackend);
                    }
                }

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

                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (!IsProcessAlive(_serverProc)) return false;
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        using var resp = await _http.GetAsync(_healthUrl, cts.Token).ConfigureAwait(false);
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

        /// <summary>
        /// Resolves a model glob like "parakeet-*.gguf" against the CrispASR model folder.
        /// If the literal filename exists, returns that; otherwise picks the first matching
        /// file. Returns a sensible default path even when nothing matches, so ModelFilesExist()
        /// can report which file is missing.
        /// </summary>
        private static string ResolveModel(string modelGlob)
        {
            string literal = Path.Combine(ModelFolder, modelGlob);
            if (File.Exists(literal)) return literal;

            if (Directory.Exists(ModelFolder))
            {
                foreach (var f in Directory.EnumerateFiles(ModelFolder, modelGlob))
                    return f;
            }

            return literal;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            KillServer();
        }
    }
}
