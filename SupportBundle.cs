using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WhisperInk
{
    /// <summary>
    /// Produces a zip of everything useful for debugging a stuck or
    /// misbehaving WhisperInk install. API keys are redacted. GGUF
    /// weights are NOT included (too big). The zip is saved to the
    /// desktop and put on the clipboard as a file reference so the
    /// user can paste it into Slack / Discord / email in one step.
    /// </summary>
    internal static class SupportBundle
    {
        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk");

        public static string Build(IEnumerable<ApiProvider> providers, string activeProviderId)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string zipPath = Path.Combine(desktop, $"WhisperInk-support-{ts}.zip");

            // Overwrite if it already exists.
            if (File.Exists(zipPath)) File.Delete(zipPath);

            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // about.txt
                AddText(zip, "about.txt", BuildAbout(providers, activeProviderId));

                // debug.log (tail — last 500 lines)
                string logPath = Path.Combine(ConfigFolder, "debug.log");
                if (File.Exists(logPath))
                {
                    AddText(zip, "debug.log", TailLines(logPath, 500));
                }
                else
                {
                    AddText(zip, "debug.log", "(no debug.log present)");
                }

                // config.json (redacted)
                string cfgPath = Path.Combine(ConfigFolder, "config.json");
                if (File.Exists(cfgPath))
                {
                    AddText(zip, "config.json", RedactConfig(File.ReadAllText(cfgPath)));
                }
                else
                {
                    AddText(zip, "config.json", "{}");
                }
            }

            TryCopyToClipboard(zipPath);
            return zipPath;
        }

        public static string BuildAbout(IEnumerable<ApiProvider> providers, string activeProviderId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WhisperInk support bundle");
            sb.AppendLine($"Captured:              {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Version:               {GetInformationalVersion()}");
            sb.AppendLine($"File version:          {GetFileVersion()}");
            sb.AppendLine($"Build date:            {GetBuildDate():yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($".NET runtime:          {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS:                    {RuntimeInformation.OSDescription}");
            sb.AppendLine($"Process architecture:  {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine();
            sb.AppendLine($"Active provider:       {activeProviderId}");
            sb.AppendLine();
            sb.AppendLine("Providers installed:");
            foreach (var p in providers)
            {
                string apiKey  = string.IsNullOrWhiteSpace(p.ApiKey)  ? "(none)" : "***present***";
                sb.AppendLine($"  - {p.Id,-28} {p.Name}");
                sb.AppendLine($"      base url:    {p.BaseUrl}");
                sb.AppendLine($"      api key:     {apiKey}");
                if (!string.IsNullOrWhiteSpace(p.TranscriptionModel))
                    sb.AppendLine($"      model:       {p.TranscriptionModel}");
            }

            sb.AppendLine();
            sb.AppendLine("Local model folder contents:");
            string modelFolder = Path.Combine(ConfigFolder, "cohere-gguf");
            if (Directory.Exists(modelFolder))
            {
                foreach (var f in Directory.EnumerateFiles(modelFolder))
                {
                    var fi = new FileInfo(f);
                    sb.AppendLine($"  {fi.Name,-45} {FormatSize(fi.Length)}");
                }
            }
            else
            {
                sb.AppendLine("  (cohere-gguf folder missing — no local models installed)");
            }

            return sb.ToString();
        }

        public static string GetInformationalVersion()
        {
            var a = Assembly.GetExecutingAssembly();
            var attr = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "1.0.0";
        }

        public static string GetFileVersion()
        {
            var a = Assembly.GetExecutingAssembly();
            var attr = a.GetCustomAttribute<AssemblyFileVersionAttribute>();
            return attr?.Version ?? a.GetName().Version?.ToString() ?? "0.0.0.0";
        }

        public static string GetCommitHash()
        {
            var v = GetInformationalVersion();
            int plus = v.IndexOf('+');
            if (plus >= 0 && plus + 1 < v.Length) return v[(plus + 1)..];
            return "";
        }

        public static DateTime GetBuildDate()
        {
            // Single-file publish zeroes out Assembly.Location, so read
            // the exe's last-write time via AppContext / the process exe
            // path instead.
            try
            {
                string? exe = null;
                try { exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; } catch { }
                if (string.IsNullOrEmpty(exe))
                {
                    string dir = AppContext.BaseDirectory;
                    string candidate = Path.Combine(dir, "WhisperInk.exe");
                    if (File.Exists(candidate)) exe = candidate;
                }
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    return File.GetLastWriteTime(exe);
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void AddText(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(content);
        }

        private static string TailLines(string path, int count)
        {
            try
            {
                var all = File.ReadAllLines(path);
                if (all.Length <= count) return string.Join("\n", all);
                return string.Join("\n", all.Skip(all.Length - count));
            }
            catch (Exception ex)
            {
                return $"(error reading log: {ex.Message})";
            }
        }

        public static string RedactConfig(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                using var ms  = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    RedactElement(doc.RootElement, w);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return raw; // if the config isn't valid JSON, return the raw text — better than nothing.
            }
        }

        private static void RedactElement(JsonElement el, Utf8JsonWriter w)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (IsSensitiveKey(prop.Name))
                        {
                            w.WritePropertyName(prop.Name);
                            bool hasValue = prop.Value.ValueKind == JsonValueKind.String
                                            && !string.IsNullOrEmpty(prop.Value.GetString());
                            w.WriteStringValue(hasValue ? "***redacted***" : "");
                        }
                        else
                        {
                            w.WritePropertyName(prop.Name);
                            RedactElement(prop.Value, w);
                        }
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in el.EnumerateArray()) RedactElement(item, w);
                    w.WriteEndArray();
                    break;
                default:
                    el.WriteTo(w);
                    break;
            }
        }

        private static bool IsSensitiveKey(string name)
        {
            return name.Equals("ApiKey",         StringComparison.OrdinalIgnoreCase)
                || name.Equals("MistralApiKey",  StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F1} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
            if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F1} KB";
            return $"{bytes} B";
        }

        private static void TryCopyToClipboard(string filePath)
        {
            // Must run on the UI thread with STA. Caller dispatches to
            // avoid cross-thread Clipboard errors.
            try
            {
                var files = new System.Collections.Specialized.StringCollection { filePath };
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    try { System.Windows.Clipboard.SetFileDropList(files); } catch { }
                });
            }
            catch { }
        }
    }
}
