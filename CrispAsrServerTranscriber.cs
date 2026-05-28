using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Generic adapter for CrispASR's unified <c>crispasr.exe --server</c>
    /// mode. Lazy-starts the process on first transcription, keeps it
    /// resident, and posts WAV audio to its OpenAI-compatible
    /// <c>/v1/audio/transcriptions</c> endpoint.
    ///
    /// Backend-agnostic: CrispASR auto-detects Parakeet / Canary / Voxtral /
    /// Granite / Cohere from the GGUF metadata. Backends whose metadata
    /// doesn't carry that marker (Cohere, Voxtral, Granite) set
    /// <see cref="ApiProvider.LocalBackendHint"/> so we pass <c>--backend</c>
    /// explicitly.
    /// </summary>
    public sealed class CrispAsrServerTranscriber : ITranscriber
    {
        private const string DefaultModelFolder = "cohere-gguf";
        private const string ExeName = "crispasr.exe";
        private const string ServerHost = "127.0.0.1";

        private readonly ApiProvider _provider;
        private readonly Func<string> _resolveGlobalGpuBackend;
        private readonly Action<string> _log;

        private readonly string _modelFolder;
        private readonly string _exePath;
        private readonly string _modelPath;
        private readonly int _port;
        private readonly int _threads;
        private readonly string? _backendHint;
        private readonly string _inferenceUrl;
        private readonly string _healthUrl;

        private Process? _serverProc;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _serverReady;
        private bool _disposed;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

        public string DisplayName => _provider.Name;
        public int Port => _port;
        public string ModelPath => _modelPath;
        public string ExeFolder => _modelFolder;

        public CrispAsrServerTranscriber(ApiProvider provider, Func<string>? resolveGlobalGpuBackend, Action<string>? log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _resolveGlobalGpuBackend = resolveGlobalGpuBackend ?? (() => "auto");
            _log = log ?? (_ => { });

            _modelFolder = ResolveModelFolder(provider);
            _exePath = Path.Combine(_modelFolder, ExeName);
            _modelPath = ResolveModel(_modelFolder, provider.LocalModelGlob);
            _port = ResolvePort(provider);
            _threads = Math.Min(8, Environment.ProcessorCount);
            _backendHint = string.IsNullOrWhiteSpace(provider.LocalBackendHint) ? null : provider.LocalBackendHint;
            _inferenceUrl = $"http://{ServerHost}:{_port}/v1/audio/transcriptions";
            _healthUrl = $"http://{ServerHost}:{_port}/health";
        }

        public bool IsReady(out string? diagnostic)
        {
            if (!File.Exists(_exePath))
            {
                diagnostic = $"{ExeName} not found at {_exePath}";
                return false;
            }
            if (!File.Exists(_modelPath))
            {
                diagnostic = string.IsNullOrWhiteSpace(_provider.LocalModelGlob)
                    ? $"Model GGUF not found in {_modelFolder}"
                    : $"Model GGUF '{_provider.LocalModelGlob}' not found in {_modelFolder}";
                return false;
            }
            diagnostic = null;
            return true;
        }

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (_disposed) return null;
            if (wavBytes == null || wavBytes.Length == 0) return null;

            // CrispASR only understands the OpenAI "prompt" field — terms are
            // joined when ContextBiasMode == "whisper_prompt", otherwise dropped.
            // Per project memory, Cohere/Voxtral/Granite local all ignore
            // biasing in practice, but we honour the configured mode so the
            // user can toggle and observe.
            string? prompt = null;
            if (biasTerms is { Count: > 0 } && _provider.ContextBiasMode == "whisper_prompt")
                prompt = string.Join(", ", biasTerms);

            try
            {
                if (!await EnsureServerRunningAsync(ct).ConfigureAwait(false))
                    return null;

                var fileContent = new ByteArrayContent(wavBytes);
                return await PostMultipartAsync(fileContent, _provider.Language ?? "en", prompt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"CrispAsr({_provider.Id}) transcribe failed: {ex.Message}");
                _serverReady = false;
                return null;
            }
        }

        private async Task<string?> PostMultipartAsync(HttpContent fileContent, string language, string? prompt, CancellationToken ct)
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

                using var response = await _http.PostAsync(_inferenceUrl, content, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textEl))
                    return textEl.GetString()?.Trim();
                return null;
            }
        }

        private async Task<bool> EnsureServerRunningAsync(CancellationToken ct)
        {
            if (_serverReady && IsProcessAlive(_serverProc)) return true;

            await _startLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_serverReady && IsProcessAlive(_serverProc)) return true;

                KillServer();
                if (!File.Exists(_exePath) || !File.Exists(_modelPath)) return false;

                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    WorkingDirectory = _modelFolder,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
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

                string gpuBackend = ResolveEffectiveGpuBackend();
                if (gpuBackend == "cpu")
                {
                    // Force CPU by disabling GPU outright — more robust than
                    // --gpu-backend cpu on some driver combinations.
                    psi.ArgumentList.Add("-ng");
                }
                else if (!string.IsNullOrWhiteSpace(gpuBackend) && gpuBackend != "auto")
                {
                    psi.ArgumentList.Add("--gpu-backend");
                    psi.ArgumentList.Add(gpuBackend);
                }

                _serverProc = new Process { StartInfo = psi };
                if (!_serverProc.Start()) return false;
                _log($"CrispAsr({_provider.Id}): spawned PID {_serverProc.Id} on port {_port} (gpu={gpuBackend})");

                _ = Task.Run(async () =>
                {
                    try { await _serverProc.StandardOutput.ReadToEndAsync().ConfigureAwait(false); } catch { }
                });
                _ = Task.Run(async () =>
                {
                    try { await _serverProc.StandardError.ReadToEndAsync().ConfigureAwait(false); } catch { }
                });

                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline)
                {
                    if (ct.IsCancellationRequested) return false;
                    if (!IsProcessAlive(_serverProc)) return false;
                    try
                    {
                        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        pingCts.CancelAfter(TimeSpan.FromMilliseconds(500));
                        using var resp = await _http.GetAsync(_healthUrl, pingCts.Token).ConfigureAwait(false);
                        if (resp.IsSuccessStatusCode)
                        {
                            _serverReady = true;
                            _log($"CrispAsr({_provider.Id}): healthy on port {_port}");
                            return true;
                        }
                    }
                    catch { }
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }

                KillServer();
                _log($"CrispAsr({_provider.Id}): /health did not respond within 45s — killed server");
                return false;
            }
            finally
            {
                _startLock.Release();
            }
        }

        private string ResolveEffectiveGpuBackend()
        {
            string raw = string.IsNullOrWhiteSpace(_provider.LocalGpuBackend)
                ? _resolveGlobalGpuBackend()
                : _provider.LocalGpuBackend;
            return NormalizeGpuBackend(raw);
        }

        private static string NormalizeGpuBackend(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "auto";
            string v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "auto" or "cpu" or "vulkan" or "cuda" or "metal" => v,
                _ => "auto",
            };
        }

        private static int ResolvePort(ApiProvider provider)
        {
            if (provider.LocalServerPort is int p and > 0) return p;
            // Fall back to parsing the BaseUrl/TranscriptionEndpoint.
            string? url = !string.IsNullOrWhiteSpace(provider.TranscriptionEndpoint)
                ? provider.TranscriptionEndpoint
                : provider.BaseUrl;
            if (!string.IsNullOrWhiteSpace(url)
                && Uri.TryCreate(url, UriKind.Absolute, out var u)
                && u.Port > 0)
                return u.Port;
            return 8103; // matches the Parakeet preset; arbitrary but stable.
        }

        private static string ResolveModelFolder(ApiProvider provider)
        {
            string sub = string.IsNullOrWhiteSpace(provider.LocalModelFolder)
                ? DefaultModelFolder
                : provider.LocalModelFolder;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk", sub);
        }

        /// <summary>Resolves a literal filename or glob against the model
        /// folder, picking the first match. Returns a stable path even when
        /// nothing exists, so <see cref="IsReady"/> can report which file is
        /// missing by name.</summary>
        private static string ResolveModel(string folder, string glob)
        {
            if (string.IsNullOrWhiteSpace(glob))
                return Path.Combine(folder, "model.gguf");

            string literal = Path.Combine(folder, glob);
            if (File.Exists(literal)) return literal;

            if (Directory.Exists(folder))
            {
                foreach (var f in Directory.EnumerateFiles(folder, glob))
                    return f;
            }
            return literal;
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
