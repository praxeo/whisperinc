using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
            // Preferred path: ask the Vulkan loader directly. vulkaninfo ships with
            // the Vulkan SDK and with most modern GPU drivers, and unlike parsing
            // `crispasr --help` it actually enumerates installed Vulkan devices —
            // the --help output never triggers ggml init banners (no model loads).
            var vk = TryVulkanInfo();
            if (vk != null) return vk;

            // Fallback: try CUDA via nvidia-smi for boxes without vulkaninfo.
            var cuda = TryNvidiaSmi();
            if (cuda != null) return cuda;

            // Last resort: surface a useful hint instead of a definitive "no GPU"
            // claim, because the actual --gpu-backend flag is still honored by
            // crispasr at server-spawn time even when this probe can't see anything.
            return "GPU probe inconclusive — try Vulkan/CUDA anyway; crispasr will fall back to CPU if unavailable.";
        }

        private static string? TryVulkanInfo()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "vulkaninfo",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                psi.ArgumentList.Add("--summary");

                using var p = new Process { StartInfo = psi };
                if (!p.Start()) return null;

                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var devices = new System.Collections.Generic.List<string>();
                foreach (Match m in Regex.Matches(stdout + "\n" + stderr,
                             @"deviceName\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase))
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(name)) devices.Add(name + " (Vulkan)");
                }

                if (devices.Count == 0) return null;
                return "Detected: " + string.Join(", ", devices);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryNvidiaSmi()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "nvidia-smi",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                psi.ArgumentList.Add("--query-gpu=name");
                psi.ArgumentList.Add("--format=csv,noheader");

                using var p = new Process { StartInfo = psi };
                if (!p.Start()) return null;

                string stdout = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(3000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var devices = new System.Collections.Generic.List<string>();
                foreach (var line in stdout.Split('\n'))
                {
                    var name = line.Trim();
                    if (!string.IsNullOrEmpty(name)) devices.Add(name + " (CUDA)");
                }
                if (devices.Count == 0) return null;
                return "Detected: " + string.Join(", ", devices);
            }
            catch
            {
                return null;
            }
        }

    }
}
