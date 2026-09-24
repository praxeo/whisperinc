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
        // The current server's recent output, so a failure can say WHY.
        // One per process (created at spawn), never shared, so a dying old
        // process can't write its last lines into a new server's tail.
        private OutputTail? _serverTail;
        // The process whose exit was already logged (by the startup check or
        // a failed request), so the restart path doesn't log it a second time.
        private Process? _exitReportedFor;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private volatile bool _serverReady;
        private bool _disposed;

        // No flat per-request timeout any more. A transcription is bounded by
        // the caller's per-take deadline token (TranscriptionDeadline: 180 s +
        // the audio's length) and each /health ping by its own 500 ms token.
        // The old flat 120 s failed any take whose inference ran past two
        // minutes, however healthy the server; the backstop is longer than
        // every deadline and only ends a request that carries no token.
        private static readonly HttpClient _http = new() { Timeout = TranscriptionDeadline.HttpBackstop };

        // crispasr.exe exits with this when a DLL it links is missing — the
        // documented "silent exit, no output" failure (CLAUDE.md gotchas).
        private const int StatusDllNotFound = unchecked((int)0xC0000135);

        /// <summary>The last lines a server process printed (stdout and
        /// stderr interleaved), bounded. crispasr prints its reason before it
        /// dies — a bad or truncated model, CUDA out of memory, an unknown
        /// backend — but the output used to be read and discarded, so
        /// debug.log could only ever say "did not respond" or nothing.</summary>
        private sealed class OutputTail
        {
            private const int MaxLines = 20;
            private const int MaxLineChars = 300;
            private readonly Queue<string> _lines = new();

            public void Add(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                if (line.Length > MaxLineChars) line = line[..MaxLineChars] + "…";
                lock (_lines)
                {
                    _lines.Enqueue(line);
                    while (_lines.Count > MaxLines) _lines.Dequeue();
                }
            }

            public string Text()
            {
                lock (_lines)
                {
                    return _lines.Count == 0
                        ? " (none captured)"
                        : "\n    " + string.Join("\n    ", _lines);
                }
            }
        }

        private static string DescribeExit(Process p)
        {
            try
            {
                int code = p.ExitCode;
                string hint = code == StatusDllNotFound
                    ? " = STATUS_DLL_NOT_FOUND: a CrispASR DLL is missing from the model folder; re-run scripts\\update-crispasr.ps1"
                    : "";
                return $"exited with code {code} (0x{code:X8}){hint}";
            }
            catch
            {
                return "exited (exit code unavailable)";
            }
        }

        /// <summary>Lets an exited process's async output readers deliver
        /// their final lines (usually the error) before the tail is logged.
        /// WaitForExitAsync also waits for redirected-stream EOF; bounded, in
        /// case something still holds the pipe open.</summary>
        private static async Task DrainAsync(Process p)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        private static string Preview(string body) =>
            string.IsNullOrEmpty(body) ? "(empty body)"
            : body.Length <= 300 ? body
            : body[..300] + "…";

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
            // prompt text on the speech-LLMs (Qwen3-ASR, Voxtral 3B, and Granite
            // since upstream 8fad1cb9), accepted-but-no-op on Cohere/Voxtral-4B.
            // Sent whenever bias terms exist — older servers ignore unknown fields.
            // The OpenAI "prompt" field is NOT sent: of the backends WhisperInk
            // ships presets for, none reads it (whisper does, as its initial
            // prompt, but there is no whisper preset).
            string? hotwords = biasTerms is { Count: > 0 } ? string.Join(",", biasTerms) : null;

            try
            {
                if (!await EnsureServerRunningAsync(ct).ConfigureAwait(false))
                    return null;

                var fileContent = new ByteArrayContent(wavBytes);
                return await PostMultipartAsync(fileContent, _provider.Language ?? "en", hotwords, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The caller's per-take deadline passed (MainWindow logs the
                // error with the numbers). An inference already running can't
                // be interrupted, and the next request would queue behind it,
                // so the next dictation gets a fresh server instead.
                _log($"CrispAsr({_provider.Id}): stopped at the take's deadline; the server restarts on the next dictation. Last output:{_serverTail?.Text() ?? " (none captured)"}");
                _serverReady = false;
                return null;
            }
            catch (Exception ex)
            {
                // Name the server's state too: "A task was canceled" alone
                // can't tell a slow inference from a server that crashed
                // mid-request.
                var proc = _serverProc;
                string server = "";
                if (proc != null && !IsProcessAlive(proc))
                {
                    await DrainAsync(proc).ConfigureAwait(false);
                    server = $" — the server {DescribeExit(proc)}. Last output:{_serverTail?.Text() ?? " (none captured)"}";
                    _exitReportedFor = proc;
                }
                _log($"CrispAsr({_provider.Id}) transcribe failed: {ex.GetType().Name}: {ex.Message}{server}");
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
                if (!response.IsSuccessStatusCode)
                {
                    _log($"CrispAsr({_provider.Id}): HTTP {(int)response.StatusCode} from the server: {Preview(body)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("text", out var textEl))
                {
                    _log($"CrispAsr({_provider.Id}): response has no \"text\" field: {Preview(body)}");
                    return null;
                }
                string text = textEl.GetString()?.Trim() ?? "";
                if (text.Length == 0)
                {
                    // Not an error by itself (a genuinely silent take), but it
                    // is also how a broken backend answers. MainWindow decides
                    // by the captured level; the server's own output is the
                    // evidence for WHY, so record it now while it's fresh.
                    _log($"CrispAsr({_provider.Id}): server returned EMPTY text. Last server output:{_serverTail?.Text() ?? " (none captured)"}");
                }
                return text;
            }
        }

        private async Task<bool> EnsureServerRunningAsync(CancellationToken ct)
        {
            if (_serverReady && IsProcessAlive(_serverProc)) return true;

            await _startLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_serverReady && IsProcessAlive(_serverProc)) return true;

                // A server that died on its own since the last dictation (a
                // crash, a CUDA fault, killed from outside) used to be
                // respawned without a word. Say so first — once per process.
                var previous = _serverProc;
                if (previous != null && previous != _exitReportedFor && !IsProcessAlive(previous))
                {
                    await DrainAsync(previous).ConfigureAwait(false);
                    _log($"CrispAsr({_provider.Id}): previous server {DescribeExit(previous)} — restarting. Last output:{_serverTail?.Text() ?? " (none captured)"}");
                    _exitReportedFor = previous;
                }

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

                // Output is captured line by line into a bounded tail. It has
                // to be drained either way (a chatty server blocks on a full
                // pipe); the handlers close over THIS process's tail, so a
                // respawn can't mix two processes' output. (The old readers
                // re-read the _serverProc field inside Task.Run, which could
                // already point at the next process.)
                var tail = new OutputTail();
                var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (_, e) => tail.Add(e.Data);
                proc.ErrorDataReceived += (_, e) => tail.Add(e.Data);
                if (!proc.Start())
                {
                    _log($"CrispAsr({_provider.Id}): {ExeName} did not start");
                    proc.Dispose();
                    return false;
                }
                _serverProc = proc;
                _serverTail = tail;
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                _log($"CrispAsr({_provider.Id}): spawned PID {proc.Id} on port {_port} (gpu={gpuBackend})");

                var deadline = DateTime.UtcNow.AddSeconds(HealthDeadlineSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    if (ct.IsCancellationRequested) return false;
                    if (!IsProcessAlive(proc))
                    {
                        // The common startup failures all land here, and crispasr
                        // prints the reason just before it goes: a missing DLL
                        // (see DescribeExit), a bad model path, CUDA OOM.
                        await DrainAsync(proc).ConfigureAwait(false);
                        _log($"CrispAsr({_provider.Id}): server {DescribeExit(proc)} during startup, before /health answered. Last output:{tail.Text()}");
                        _exitReportedFor = proc;
                        return false;
                    }
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

                _log($"CrispAsr({_provider.Id}): /health did not respond within {HealthDeadlineSeconds}s — killing server. Last output:{tail.Text()}");
                KillServer();
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
