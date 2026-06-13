using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Builds a human-readable diagnostic block for the active provider.
    /// Checks local model files, the crispasr binary, and whether the
    /// auto-spawned server is listening on its port. Never spawns
    /// anything — only observes.
    /// </summary>
    internal static class ProviderDiagnostics
    {
        private static readonly string OnnxFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".WhisperInk", "cohere-onnx");

        public static async Task<string> BuildAsync(ApiProvider? prov)
        {
            if (prov == null) return "No active provider configured.";

            var sb = new StringBuilder();
            sb.AppendLine($"Provider: {prov.Name}  (id={prov.Id})");

            switch (prov.TranscriberKind)
            {
                case TranscriberKind.LocalOnnx:
                    AppendFileCheck(sb, "cohere-encoder.int4.onnx", Path.Combine(OnnxFolder, "cohere-encoder.int4.onnx"));
                    AppendFileCheck(sb, "cohere-decoder.int4.onnx", Path.Combine(OnnxFolder, "cohere-decoder.int4.onnx"));
                    AppendFileCheck(sb, "tokens.txt",               Path.Combine(OnnxFolder, "tokens.txt"));
                    break;

                case TranscriberKind.LocalCrispAsrServer:
                    await AppendLocalGgufCheckAsync(sb, prov);
                    break;

                default:
                    sb.AppendLine($"  Base URL:                        {prov.BaseUrl}");
                    if (prov.IsLocalHttp)
                    {
                        // Local OpenAI-compatible server (e.g. qwen3-asr) — no key needed.
                        if (TryParsePort(prov.BaseUrl, out var httpPort))
                            await AppendPortCheckAsync(sb, httpPort);
                    }
                    else
                    {
                        sb.AppendLine($"  API key:                         {(string.IsNullOrWhiteSpace(prov.ApiKey) ? "MISSING" : "present")}");
                        sb.AppendLine($"  Transcription model:             {(string.IsNullOrWhiteSpace(prov.TranscriptionModel) ? "(endpoint default)" : prov.TranscriptionModel)}");
                    }
                    break;
            }

            return sb.ToString().TrimEnd();
        }

        private static async Task AppendLocalGgufCheckAsync(StringBuilder sb, ApiProvider prov)
        {
            string sub = string.IsNullOrWhiteSpace(prov.LocalModelFolder) ? "cohere-gguf" : prov.LocalModelFolder;
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk", sub);
            string modelGlob = string.IsNullOrWhiteSpace(prov.LocalModelGlob) ? "*.gguf" : prov.LocalModelGlob;

            string exe = Path.Combine(folder, "crispasr.exe");
            AppendFileCheck(sb, "crispasr.exe", exe);

            string literal = Path.Combine(folder, modelGlob);
            if (File.Exists(literal))
            {
                AppendFileCheck(sb, modelGlob, literal);
            }
            else if (Directory.Exists(folder))
            {
                string? found = null;
                foreach (var f in Directory.EnumerateFiles(folder, modelGlob)) { found = f; break; }
                if (found != null) AppendFileCheck(sb, Path.GetFileName(found), found);
                else               AppendMissing(sb, modelGlob, literal);
            }
            else
            {
                AppendMissing(sb, modelGlob, literal);
            }

            foreach (var dll in new[] { "ggml-cpu.dll", "cohere.dll", "parakeet.dll", "ggml-vulkan.dll", "ggml-cuda.dll" })
            {
                string p = Path.Combine(folder, dll);
                if (File.Exists(p)) AppendFileCheck(sb, dll, p, hint: dll.Contains("vulkan") ? "GPU acceleration available" : dll.Contains("cuda") ? "CUDA acceleration available" : null);
            }

            if (TryParsePort(prov.BaseUrl, out var port))
                await AppendPortCheckAsync(sb, port);
        }

        private static async Task AppendPortCheckAsync(StringBuilder sb, int port)
        {
            var start = DateTime.Now;
            bool listening = await HealthProbe.IsPortListeningAsync("127.0.0.1", port, timeoutMs: 500);
            int ms = (int)(DateTime.Now - start).TotalMilliseconds;
            if (listening)
                sb.AppendLine($"  Port {port} reachable:             YES (server responded in {ms}ms)");
            else
                sb.AppendLine($"  Port {port} reachable:             NO  (no server bound — first transcription will auto-spawn)");
        }

        private static bool TryParsePort(string? url, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0) { port = u.Port; return true; }
            return false;
        }

        private static void AppendFileCheck(StringBuilder sb, string label, string path, string? hint = null)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                string size = FormatSize(fi.Length);
                string tail = hint != null ? $"  ({size} — {hint})" : $"  ({size})";
                sb.AppendLine(FormatLine(label, "FOUND", tail));
            }
            else
            {
                AppendMissing(sb, label, path);
            }
        }

        private static void AppendMissing(StringBuilder sb, string label, string path)
        {
            sb.AppendLine(FormatLine(label, "MISSING", $"  (expected at {path})"));
        }

        private static string FormatLine(string label, string status, string tail)
        {
            string labelCol = (label + ":").PadRight(32);
            return $"  {labelCol} {status}{tail}";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
            if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
            return $"{bytes} B";
        }
    }
}
