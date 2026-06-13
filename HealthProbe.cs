using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public enum HealthStatus { Unknown, Ok, Fail }

    public sealed class HealthReport
    {
        public HealthStatus Status { get; init; } = HealthStatus.Unknown;
        public string Summary     { get; init; } = "";
        public DateTime CheckedAt { get; init; } = DateTime.Now;

        public string Dot => Status switch
        {
            HealthStatus.Ok   => "🟢",
            HealthStatus.Fail => "🔴",
            _                 => "🟡"
        };
    }

    /// <summary>
    /// Background health probe for the currently active provider.
    /// Probes on demand, on provider switch, and every 60s while running.
    /// Never blocks the UI thread — all HTTP is async with a short timeout.
    /// </summary>
    public sealed class HealthProbe : IDisposable
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

        private readonly Func<ApiProvider?> _getActiveProvider;
        private readonly Action<HealthReport> _onReport;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public HealthReport Last { get; private set; } = new();

        public HealthProbe(Func<ApiProvider?> getActiveProvider, Action<HealthReport> onReport)
        {
            _getActiveProvider = getActiveProvider;
            _onReport          = onReport;
        }

        public void Start()
        {
            if (_loop != null) return;
            _loop = Task.Run(LoopAsync);
        }

        public void RequestProbe() => _ = Task.Run(() => ProbeOnceAsync());

        private async Task LoopAsync()
        {
            var token = _cts.Token;
            try
            {
                await ProbeOnceAsync().ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(60), token).ConfigureAwait(false); }
                    catch (TaskCanceledException) { return; }
                    await ProbeOnceAsync().ConfigureAwait(false);
                }
            }
            catch { }
        }

        private async Task ProbeOnceAsync()
        {
            var prov = _getActiveProvider();
            if (prov == null)
            {
                Publish(new HealthReport { Status = HealthStatus.Unknown, Summary = "No active provider" });
                return;
            }

            HealthReport report;
            try { report = await ProbeProviderAsync(prov).ConfigureAwait(false); }
            catch (Exception ex) { report = new HealthReport { Status = HealthStatus.Fail, Summary = ex.Message }; }

            Publish(report);
        }

        private void Publish(HealthReport r)
        {
            Last = r;
            try { _onReport(r); } catch { }
        }

        private static async Task<HealthReport> ProbeProviderAsync(ApiProvider prov)
        {
            // Dispatch on transcriber TYPE, not hardcoded provider ids. Keying on
            // ids meant any local provider not in the list (parakeet-rnnt-local,
            // granite-local, voxtral4b-local, or any user-added one) fell through
            // to the cloud branch and was flagged "Missing API key" — which is why
            // a dummy key was needed to clear the red banner. Type-based dispatch
            // covers every local provider automatically.
            switch (prov.TranscriberKind)
            {
                case TranscriberKind.LocalOnnx:
                    return ProbeOnnx();

                case TranscriberKind.LocalCrispAsrServer:
                    return await ProbeLocalServerAsync(prov).ConfigureAwait(false);

                default:
                    // An OpenAI-compatible server on localhost (e.g. qwen3-asr) needs
                    // no key — probe its /health. Otherwise it's a cloud/credentialed
                    // provider: require a key to consider it "ok".
                    if (prov.IsLocalHttp)
                        return await ProbeHttpHealthAsync(prov.BaseUrl).ConfigureAwait(false);
                    if (prov.RequiresApiKey && string.IsNullOrWhiteSpace(prov.ApiKey))
                        return new HealthReport { Status = HealthStatus.Fail, Summary = $"Missing API key for {prov.Name}" };
                    return new HealthReport { Status = HealthStatus.Ok, Summary = $"{prov.Name} — API key present" };
            }
        }

        private static HealthReport ProbeOnnx()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk", "cohere-onnx");
            string encoder = Path.Combine(dir, "cohere-encoder.int4.onnx");
            string decoder = Path.Combine(dir, "cohere-decoder.int4.onnx");
            string tokens  = Path.Combine(dir, "tokens.txt");
            var missing = new List<string>();
            if (!File.Exists(encoder)) missing.Add("cohere-encoder.int4.onnx");
            if (!File.Exists(decoder)) missing.Add("cohere-decoder.int4.onnx");
            if (!File.Exists(tokens))  missing.Add("tokens.txt");
            if (missing.Count > 0)
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"Missing ONNX files: {string.Join(", ", missing)}" };
            return new HealthReport { Status = HealthStatus.Ok, Summary = "ONNX files present" };
        }

        private static HealthReport ProbeLocalGgufFiles(string subFolder, string glob)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk", subFolder);
            string exe = Path.Combine(dir, "crispasr.exe");
            var missing = new List<string>();
            if (!File.Exists(exe)) missing.Add("crispasr.exe");
            bool modelFound = false;
            if (Directory.Exists(dir))
            {
                // literal filename first (no wildcard), then the glob pattern.
                if (!glob.Contains('*') && File.Exists(Path.Combine(dir, glob)))
                    modelFound = true;
                else
                    foreach (var _ in Directory.EnumerateFiles(dir, glob)) { modelFound = true; break; }
            }
            if (!modelFound) missing.Add(glob);
            if (missing.Count > 0)
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"Missing: {string.Join(", ", missing)}" };
            return new HealthReport { Status = HealthStatus.Ok, Summary = "crispasr.exe + model present" };
        }

        private static async Task<HealthReport> ProbeLocalServerAsync(ApiProvider prov)
        {
            string subFolder = string.IsNullOrWhiteSpace(prov.LocalModelFolder) ? "cohere-gguf" : prov.LocalModelFolder;
            string modelGlob = string.IsNullOrWhiteSpace(prov.LocalModelGlob) ? "*.gguf" : prov.LocalModelGlob;

            var files = ProbeLocalGgufFiles(subFolder, modelGlob);
            if (files.Status != HealthStatus.Ok) return files;

            int port = prov.LocalServerPort ?? 0;
            if (!string.IsNullOrWhiteSpace(prov.BaseUrl) && Uri.TryCreate(prov.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0)
                port = u.Port;
            if (port <= 0) port = 8103;

            if (!await IsPortListeningAsync("127.0.0.1", port).ConfigureAwait(false))
            {
                return new HealthReport
                {
                    Status  = HealthStatus.Ok,
                    Summary = $"Files ready; server not yet spawned (port {port})"
                };
            }

            try
            {
                using var resp = await _http.GetAsync($"http://127.0.0.1:{port}/health").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return new HealthReport { Status = HealthStatus.Ok, Summary = $"Server healthy on :{port}" };
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"Server on :{port} returned {(int)resp.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"/health on :{port} — {ex.Message}" };
            }
        }

        private static async Task<HealthReport> ProbeHttpHealthAsync(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new HealthReport { Status = HealthStatus.Fail, Summary = "Empty base URL" };
            try
            {
                using var resp = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/health").ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    return new HealthReport { Status = HealthStatus.Ok, Summary = $"{baseUrl} /health OK" };
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"{baseUrl} /health {(int)resp.StatusCode}" };
            }
            catch (Exception ex)
            {
                return new HealthReport { Status = HealthStatus.Fail, Summary = $"{baseUrl} — {ex.Message}" };
            }
        }

        public static async Task<bool> IsPortListeningAsync(string host, int port, int timeoutMs = 300)
        {
            using var client = new TcpClient();
            try
            {
                var connect = client.ConnectAsync(host, port);
                var finished = await Task.WhenAny(connect, Task.Delay(timeoutMs)).ConfigureAwait(false);
                return finished == connect && client.Connected;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
