using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// One-shot probe that runs `crispasr.exe --help` and parses the backend
    /// banners it prints to stderr to discover which GPU backends are available
    /// on this machine (e.g. Vulkan device names).
    ///
    /// Runs once at app start, caches the result, and exposes a short
    /// human-readable summary for the UI. Doesn't throw — any failure yields
    /// a useful fallback string instead.
    /// </summary>
    public static class CrispGpuProbe
    {
        private static readonly string ModelFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".WhisperInk", "cohere-gguf");

        private static readonly string ExePath = Path.Combine(ModelFolder, "crispasr.exe");

        private static string _summary = "Detecting GPU…";
        private static readonly object _lock = new();

        public static string Summary
        {
            get { lock (_lock) return _summary; }
            private set { lock (_lock) _summary = value; }
        }

        /// <summary>Run the probe in the background. Safe to call multiple times; only the
        /// first call does the work. Caller can keep reading <see cref="Summary"/>.</summary>
        public static Task StartAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    Summary = ProbeSync();
                }
                catch (Exception ex)
                {
                    Summary = $"GPU probe failed: {ex.Message}";
                }
            });
        }

        private static string ProbeSync()
        {
            if (!File.Exists(ExePath))
                return "crispasr.exe missing — GPU detection unavailable.";

            var psi = new ProcessStartInfo
            {
                FileName               = ExePath,
                WorkingDirectory       = ModelFolder,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            psi.ArgumentList.Add("--help");

            var stderr = new StringBuilder();
            var stdout = new StringBuilder();

            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived  += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            if (!p.Start()) return "GPU probe: could not start crispasr.exe";
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // --help should exit fast; cap at 8s to be safe.
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return "GPU probe timed out.";
            }
            // Drain async buffers.
            p.WaitForExit();

            return Summarize(stderr.ToString() + "\n" + stdout.ToString());
        }

        private static string Summarize(string blob)
        {
            // crispasr / ggml print lines like:
            //   ggml_vulkan: Found 1 Vulkan devices:
            //   ggml_vulkan: 0 = AMD Radeon(TM) Graphics (AMD proprietary driver) ...
            //   ggml_cuda_init: found 1 CUDA devices:
            //   Device 0: NVIDIA GeForce RTX 4090 ...
            var devices = new System.Collections.Generic.List<string>();

            foreach (Match m in Regex.Matches(blob, @"ggml_vulkan:\s*\d+\s*=\s*([^\r\n|]+?)(?:\s*\||$)", RegexOptions.IgnoreCase))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name)) devices.Add(name + " (Vulkan)");
            }
            foreach (Match m in Regex.Matches(blob, @"Device\s+\d+:\s*([^,\r\n]+)", RegexOptions.IgnoreCase))
            {
                var name = m.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(name)) devices.Add(name + " (CUDA)");
            }

            if (devices.Count == 0)
                return "No GPU detected — CPU will be used regardless of backend setting.";

            return "Detected: " + string.Join(", ", devices);
        }
    }
}
