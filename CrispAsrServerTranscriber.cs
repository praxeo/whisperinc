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

        // CrispASR v0.7 auto-warms the model in server mode (a dummy
        // transcribe at init), so first /health success on a CUDA build
        // includes VRAM upload + warmup — 45s proved too tight.
        private const int HealthDeadlineSeconds = 120;

        private readonly ApiProvider _provider;
        private readonly Func<string> _resolveGlobalGpuBackend;
        private readonly Action<string> _log;

        private readonly string _modelFolder;
        private readonly string _exePath;
        private readonly string _modelPath;
        private readonly int _port;
        private readonly int _threads;
        private readonly string? _backendHint;
        private readonly string? _puncModel;
        private readonly string? _truecaseModel;
        private readonly string _inferenceUrl;
        private readonly string _healthUrl;

        private Process? _serverProc;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _serverReady;
        private bool _disposed;

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

        // Form-field names this transcriber sets itself. LocalExtraParams entries
        // that collide with these are skipped so config.json can't clobber them.
        private static readonly HashSet<string> _reservedFormFields =
            new(StringComparer.OrdinalIgnoreCase)
            { "language", "hotwords", "hotwords_boost", "beam_size", "response_format", "file" };

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
            // Capped at 8 deliberately: ggml ASR inference scales with
            // physical cores + memory bandwidth, not SMT threads, and on
            // GPU backends -t only covers small CPU-side stages. More
            // threads oversubscribe 8-core laptops for no desktop gain.
            _threads = Math.Min(8, Environment.ProcessorCount);
            _backendHint = string.IsNullOrWhiteSpace(provider.LocalBackendHint) ? null : provider.LocalBackendHint;
            _puncModel = string.IsNullOrWhiteSpace(provider.LocalPuncModel) ? null : provider.LocalPuncModel;
            _truecaseModel = string.IsNullOrWhiteSpace(provider.LocalTruecaseModel) ? null : provider.LocalTruecaseModel;
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

            // CrispASR v0.7+ accepts a "hotwords" form field (comma-separated) for
            // real contextual biasing: a CTC/TDT/RNNT phrase-boost trie on Parakeet,
            // prompt injection on Voxtral/Qwen3-style LLM decoders, accepted-but-no-op
            // on Cohere/Granite/Voxtral-4B. Sent whenever bias terms exist — older
            // servers ignore unknown fields. The OpenAI "prompt" field is NOT sent:
            // no CrispASR backend reads it (verified against the backend sources).
            string? hotwords = biasTerms is { Count: > 0 } ? string.Join(",", biasTerms) : null;

            try
            {
                if (!await EnsureServerRunningAsync(ct).ConfigureAwait(false))
                    return null;

                var fileContent = new ByteArrayContent(wavBytes);
                return await PostMultipartAsync(fileContent, _provider.Language ?? "en", hotwords, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"CrispAsr({_provider.Id}) transcribe failed: {ex.Message}");
                _serverReady = false;
                return null;
            }
        }

        private async Task<string?> PostMultipartAsync(HttpContent fileContent, string language, string? hotwords, CancellationToken ct)
        {
            using (fileContent)
            {
                fileContent.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse("audio/wav");

                using var content = new MultipartFormDataContent();
                if (!string.IsNullOrWhiteSpace(language))
                    content.Add(new StringContent(language), "language");
                if (!string.IsNullOrWhiteSpace(hotwords))
                {
                    content.Add(new StringContent(hotwords), "hotwords");
                    // Per-term boost strength for the Parakeet trie. Server default
                    // (2.0) is effectively inert; opt in to ~10+ in settings to nudge
                    // rare terms (it can garble neighboring words). Off by default.
                    // Ignored by the LLM / cohere backends.
                    if (_provider.HotwordsBoost is double boost && boost > 0)
                        content.Add(
                            new StringContent(boost.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                            "hotwords_boost");
                }
                if (_provider.LocalBeamSize is int beam and > 0)
                    content.Add(new StringContent(beam.ToString()), "beam_size");
                content.Add(new StringContent("json"), "response_format");

                // Per-provider passthrough of additional transcription params
                // (punctuation, vad, seed, suppress_nst, …). §166+ servers read
                // these per request; older servers ignore unknown fields. Keys we
                // already set above are skipped. Added before the file part so all
                // string fields stay ahead of it (the Cohere-v2 ordering rule).
                if (_provider.LocalExtraParams is { Count: > 0 })
                {
                    foreach (var kv in _provider.LocalExtraParams)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
                        if (_reservedFormFields.Contains(kv.Key)) continue;
                        content.Add(new StringContent(kv.Value), kv.Key);
                    }
                }

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

                // Server-side punctuation restoration for non-PnC backends
                // (Parakeet RNNT/CTC). Honored by the #161-punc CrispASR build;
                // older servers ignore the unknown flag.
                if (!string.IsNullOrWhiteSpace(_puncModel))
                {
                    psi.ArgumentList.Add("--punc-model");
                    psi.ArgumentList.Add(_puncModel);
                }

                // Server-side truecasing (proper-noun / acronym casing), applied
                // after punctuation. §166-era servers honor --truecase-model in
                // server mode; older builds ignore the unknown flag.
                if (!string.IsNullOrWhiteSpace(_truecaseModel))
                {
                    psi.ArgumentList.Add("--truecase-model");
                    psi.ArgumentList.Add(_truecaseModel);
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

                var deadline = DateTime.UtcNow.AddSeconds(HealthDeadlineSeconds);
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
                _log($"CrispAsr({_provider.Id}): /health did not respond within {HealthDeadlineSeconds}s — killed server");
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
