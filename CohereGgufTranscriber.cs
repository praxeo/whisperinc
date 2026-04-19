using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Local Cohere Transcribe 03-2026 inference via CrispASR (whisper.cpp fork).
    /// Pure native subprocess — no Python, no ONNX Runtime, no GPU required.
    ///
    /// Drop-in sibling to CohereOnnxTranscriber. Same ctor shape, same
    /// ModelFilesExist() / TranscribeAsync(path, lang) / Dispose() interface.
    ///
    /// Captures transcript from stdout directly — no JSON parsing, no temp files.
    /// </summary>
    public sealed class CohereGgufTranscriber : IDisposable
    {
        private static readonly string ModelFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".WhisperInk", "cohere-gguf");

        private static readonly string ExePath       = Path.Combine(ModelFolder, "crispasr.exe");
        private static readonly string DefaultModel  = Path.Combine(ModelFolder, "cohere-transcribe-q5_0.gguf");

        private readonly string _exePath;
        private readonly string _modelPath;
        private readonly int _threads;

        public CohereGgufTranscriber() : this(ExePath, DefaultModel) { }

        public CohereGgufTranscriber(string exePath, string modelPath, int threads = 0)
        {
            _exePath   = exePath;
            _modelPath = modelPath;
            _threads   = threads <= 0 ? Math.Min(12, Environment.ProcessorCount) : threads;
        }

        /// <summary>
        /// Check whether the exe and at least one Cohere GGUF exist in the model folder.
        /// Accepts any cohere-transcribe-*.gguf so the user can swap quant levels without
        /// touching this class.
        /// </summary>
        public bool ModelFilesExist()
        {
            if (!File.Exists(_exePath)) return false;
            if (File.Exists(_modelPath)) return true;
            if (!Directory.Exists(ModelFolder)) return false;
            foreach (var _ in Directory.EnumerateFiles(ModelFolder, "cohere-transcribe-*.gguf"))
                return true;
            return false;
        }

        /// <summary>
        /// Transcribe the audio file at <paramref name="filePath"/>. Accepts any format
        /// CrispASR supports natively (WAV/FLAC/MP3/OGG); the runtime resamples to
        /// 16 kHz mono internally. Returns transcript text, or null on error.
        /// </summary>
        public async Task<string?> TranscribeAsync(string filePath, string language = "en")
        {
            if (!File.Exists(filePath)) return null;
            if (!File.Exists(_exePath)) return null;

            // Resolve model (allow any cohere-transcribe-*.gguf in the folder as fallback)
            string modelPath = _modelPath;
            if (!File.Exists(modelPath))
            {
                if (!Directory.Exists(ModelFolder)) return null;
                bool found = false;
                foreach (var f in Directory.EnumerateFiles(ModelFolder, "cohere-transcribe-*.gguf"))
                {
                    modelPath = f;
                    found = true;
                    break;
                }
                if (!found) return null;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = _exePath,
                    WorkingDirectory       = ModelFolder,     // so ggml DLLs resolve
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                };
                psi.ArgumentList.Add("-m");        psi.ArgumentList.Add(modelPath);
                psi.ArgumentList.Add("--backend"); psi.ArgumentList.Add("cohere");
                psi.ArgumentList.Add("-f");        psi.ArgumentList.Add(filePath);
                psi.ArgumentList.Add("-l");        psi.ArgumentList.Add(string.IsNullOrWhiteSpace(language) ? "en" : language);
                psi.ArgumentList.Add("-t");        psi.ArgumentList.Add(_threads.ToString());
                psi.ArgumentList.Add("-np");       // suppress progress spam on stderr

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync().ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                _ = await stderrTask.ConfigureAwait(false);

                if (proc.ExitCode != 0) return null;

                // Stdout is the raw transcript — just trim and return
                return stdout.Trim();
            }
            catch
            {
                return null;
            }
        }

        public void Dispose() { /* no persistent state — subprocess-per-call */ }
    }
}