// CohereOnnxTranscriber.cs
// Native ONNX Runtime provider for Cohere Transcribe INT4
// Uses cstr/cohere-transcribe-onnx-int4 model files (drop-in upgrade from INT8).
//
// Required NuGet:
//   dotnet add package Microsoft.ML.OnnxRuntime.Gpu.Windows  (CUDA + CPU fallback)
//
// Model files expected in:  %APPDATA%\.WhisperInk\cohere-onnx\
//   cohere-encoder.int4.onnx        (+ .data file, ~1.8 GB)
//   cohere-decoder.int4.onnx        (+ .data file, ~137 MB)
//   tokens.txt
//
// Download from: https://huggingface.co/cstr/cohere-transcribe-onnx-int4
// INT4 is ~1.4x faster on CPU and 30% smaller than INT8 with identical accuracy.
//
// Optimizations over baseline:
//   - Pre-allocated KV cache buffers (no per-step GC pressure)
//   - Encoder results kept alive (no .ToArray() copy of cross-K/V)
//   - Reusable single-token tensor for decoder steps after prompt
//   - Span-based argmax (no multi-dim indexing overhead)
//   - GraphOptimizationLevel.ORT_ENABLE_ALL + memory pattern reuse
//   - Tuned thread counts (intra=physical cores, inter=1)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WhisperInk
{
    public class CohereOnnxTranscriber : IDisposable
    {
        // ── Model constants ─────────────────────────────────────────────
        private const int NumDecoderLayers = 8;
        private const int NumHeads = 8;
        private const int HeadDim = 128;
        private const int MaxSeqLen = 1024;
        private const int VocabSize = 16384;
        private const int SampleRate = 16000;
        private const int MaxChunkSamples = 30 * SampleRate;  // 30s chunks
        private const int OverlapSamples = 5 * SampleRate;    // 5s overlap
        private const int StrideSamples = MaxChunkSamples - OverlapSamples; // 25s stride
        private const int MaxNewTokensPerChunk = 512;
        private const int KvCacheSize = NumDecoderLayers * 1 * NumHeads * MaxSeqLen * HeadDim;
        private static readonly int[] KvCacheShape = { NumDecoderLayers, 1, NumHeads, MaxSeqLen, HeadDim };

        // ── Model filenames ─────────────────────────────────────────────
        private const string EncoderFilename = "cohere-encoder.int4.onnx";
        private const string DecoderFilename = "cohere-decoder.int4.onnx";
        private const string TokensFilename  = "tokens.txt";

        private InferenceSession? _encoder;
        private InferenceSession? _decoder;
        private Dictionary<int, string> _tokens = new();
        private Dictionary<string, int> _tokenToId = new();
        private int _eosId;
        private int[] _promptIds = Array.Empty<int>();
        private bool _isLoaded;
        private readonly string _modelDir;
        private readonly bool _useDirectML;

        // ── Pre-allocated buffers (reused across decode steps) ──────────
        private float[] _selfKA = new float[KvCacheSize];
        private float[] _selfKB = new float[KvCacheSize];
        private float[] _selfVA = new float[KvCacheSize];
        private float[] _selfVB = new float[KvCacheSize];
        private readonly long[] _singleTokenBuf = new long[1];

        /// <summary>True once the model sessions are loaded and ready.</summary>
        public bool IsLoaded => _isLoaded;

        public CohereOnnxTranscriber(string? modelDir = null, bool useDirectML = true)
        {
            _modelDir = modelDir ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".WhisperInk", "cohere-onnx");
            _useDirectML = useDirectML;
        }

        public bool ModelFilesExist()
        {
            return File.Exists(Path.Combine(_modelDir, EncoderFilename))
                && File.Exists(Path.Combine(_modelDir, DecoderFilename))
                && File.Exists(Path.Combine(_modelDir, TokensFilename));
        }

        /// <summary>
        /// Loads encoder/decoder sessions and token vocabulary.
        /// Call once at startup (or lazily on first transcription).
        /// This is slow (~5-10s) — call from a background thread.
        /// </summary>
        public void Load()
        {
            if (_isLoaded) return;

            var encoderPath = Path.Combine(_modelDir, EncoderFilename);
            var decoderPath = Path.Combine(_modelDir, DecoderFilename);
            var tokensPath  = Path.Combine(_modelDir, TokensFilename);

            if (!File.Exists(encoderPath) || !File.Exists(decoderPath) || !File.Exists(tokensPath))
                throw new FileNotFoundException(
                    $"Cohere ONNX model files not found in {_modelDir}. " +
                    "Download from https://huggingface.co/cstr/cohere-transcribe-onnx-int4");

            _tokens = LoadTokens(tokensPath);
            _tokenToId = _tokens.ToDictionary(kv => kv.Value, kv => kv.Key);
            _eosId = _tokenToId.GetValueOrDefault("<|endoftext|>", -1);
            _promptIds = BuildPromptTokens("en");

            var opts = new SessionOptions();
            opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            opts.EnableMemoryPattern = true;

            if (_useDirectML)
            {
                try
                {
                    opts.AppendExecutionProvider_CUDA(0);
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                ".WhisperInk", "debug.log"),
                            $"[{DateTime.Now:HH:mm:ss.fff}] CUDA provider FAILED: {ex.Message}\n");
                    }
                    catch { }
                    // CPU fallback: intra = physical cores, inter = 1 for single-batch
                    opts.IntraOpNumThreads = Environment.ProcessorCount;
                    opts.InterOpNumThreads = 1;
                }
            }
            else
            {
                opts.IntraOpNumThreads = Environment.ProcessorCount;
                opts.InterOpNumThreads = 1;
            }

            _encoder = new InferenceSession(encoderPath, opts);
            _decoder = new InferenceSession(decoderPath, opts);
            _isLoaded = true;

            try
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        ".WhisperInk", "debug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] CohereOnnx loaded: {encoderPath} | {decoderPath} " +
                    $"(intra={opts.IntraOpNumThreads}, inter={opts.InterOpNumThreads}, " +
                    $"opt={opts.GraphOptimizationLevel})\n");
            }
            catch { }
        }

        /// <summary>
        /// Transcribes a WAV file to text. Handles chunking for audio >30s.
        /// </summary>
        public async Task<string?> TranscribeAsync(string wavPath, string language = "en")
        {
            if (!_isLoaded) Load();

            return await Task.Run(() =>
            {
                try
                {
                    if (language != "en")
                        _promptIds = BuildPromptTokens(language);

                    float[] audio = ReadWav(wavPath);

                    if (audio.Length <= MaxChunkSamples)
                    {
                        return TranscribeChunk(audio);
                    }
                    else
                    {
                        var results = new List<string>();
                        int offset = 0;
                        while (offset < audio.Length)
                        {
                            int remaining = audio.Length - offset;
                            int chunkLen = Math.Min(MaxChunkSamples, remaining);
                            float[] chunk = new float[chunkLen];
                            Array.Copy(audio, offset, chunk, 0, chunkLen);
                            string? chunkText = TranscribeChunk(chunk);
                            if (!string.IsNullOrWhiteSpace(chunkText))
                                results.Add(chunkText);
                            offset += StrideSamples;
                        }
                        return string.Join(" ", results);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"CohereOnnx error: {ex.Message}");
                    return null;
                }
            });
        }

        /// <summary>
        /// Transcribes a single chunk of audio (up to ~30s).
        /// Optimized: encoder results kept alive, pre-allocated KV cache buffers,
        /// reusable single-token tensor, span-based argmax.
        /// </summary>
        private string? TranscribeChunk(float[] audio)
        {
            if (_encoder == null || _decoder == null)
                throw new InvalidOperationException("Model not loaded. Call Load() first.");

            // ── Encoder pass ──
            // Keep encoder results alive (no .ToArray() copy) — cross-K/V tensors
            // are read-only during decoding, so we just hold the disposable collection.
            var audioTensor = new DenseTensor<float>(audio, new[] { 1, audio.Length });
            var encoderInputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("audio", audioTensor)
            };

            using var encoderResults = _encoder.Run(encoderInputs);
            var crossKValue = encoderResults.First(r => r.Name == "n_layer_cross_k");
            var crossVValue = encoderResults.First(r => r.Name == "n_layer_cross_v");
            var crossKTensor = crossKValue.AsTensor<float>();
            var crossVTensor = crossVValue.AsTensor<float>();
            int[] crossKShape = crossKTensor.Dimensions.ToArray();
            int[] crossVShape = crossVTensor.Dimensions.ToArray();

            // ── Autoregressive decoding with double-buffered KV cache ──
            // We alternate between A and B buffers to avoid allocating new arrays.
            // After each decoder step, the output cache becomes the next input.
            var generatedIds = new List<int>(_promptIds);
            Array.Clear(_selfKA, 0, KvCacheSize);
            Array.Clear(_selfVA, 0, KvCacheSize);

            // Current input/output buffer pointers (swap each step)
            float[] curSelfK = _selfKA, curSelfV = _selfVA;
            float[] outSelfK = _selfKB, outSelfV = _selfVB;

            int offset = 0;

            // First step: feed the full prompt
            long[] promptLongs = new long[_promptIds.Length];
            for (int i = 0; i < _promptIds.Length; i++)
                promptLongs[i] = _promptIds[i];
            var currentTokensTensor = new DenseTensor<long>(promptLongs, new[] { 1, _promptIds.Length });

            // Reusable tensor wrapper for single-token steps
            var singleTokenTensor = new DenseTensor<long>(_singleTokenBuf, new[] { 1, 1 });

            for (int step = 0; step < MaxNewTokensPerChunk; step++)
            {
                var tokensTensor = (step == 0) ? currentTokensTensor : (DenseTensor<long>)singleTokenTensor;
                int nTokens = (step == 0) ? _promptIds.Length : 1;

                var decoderInputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("tokens", tokensTensor),
                    NamedOnnxValue.CreateFromTensor("in_n_layer_self_k_cache",
                        new DenseTensor<float>(curSelfK, KvCacheShape)),
                    NamedOnnxValue.CreateFromTensor("in_n_layer_self_v_cache",
                        new DenseTensor<float>(curSelfV, KvCacheShape)),
                    NamedOnnxValue.CreateFromTensor("n_layer_cross_k", crossKTensor),
                    NamedOnnxValue.CreateFromTensor("n_layer_cross_v", crossVTensor),
                    NamedOnnxValue.CreateFromTensor("offset",
                        new DenseTensor<long>(new[] { (long)offset }, Array.Empty<int>())),
                };

                using var decoderResults = _decoder.Run(decoderInputs);

                // ── Span-based greedy argmax over vocab at last position ──
                var logitsTensor = decoderResults.First(r => r.Name == "logits").AsTensor<float>();
                int lastPos = nTokens - 1;
                // DenseTensor stores data row-major: [batch, seq, vocab]
                // Index into backing buffer: (0 * seqLen + lastPos) * VocabSize
                int logitsOffset = lastPos * VocabSize;
                int bestId = 0;
                float bestScore = float.NegativeInfinity;
                if (logitsTensor is DenseTensor<float> denseLogits)
                {
                    // Fast path: direct span access to backing memory
                    var span = denseLogits.Buffer.Span.Slice(logitsOffset, VocabSize);
                    for (int v = 0; v < VocabSize; v++)
                    {
                        if (span[v] > bestScore) { bestScore = span[v]; bestId = v; }
                    }
                }
                else
                {
                    // Fallback: multi-dim indexing
                    for (int v = 0; v < VocabSize; v++)
                    {
                        float score = logitsTensor[0, lastPos, v];
                        if (score > bestScore) { bestScore = score; bestId = v; }
                    }
                }

                if (bestId == _eosId) break;
                generatedIds.Add(bestId);

                // ── Copy output KV cache into the "out" buffer, then swap ──
                var outKTensor = decoderResults.First(r => r.Name == "out_n_layer_self_k_cache").AsTensor<float>();
                var outVTensor = decoderResults.First(r => r.Name == "out_n_layer_self_v_cache").AsTensor<float>();
                if (outKTensor is DenseTensor<float> denseK)
                    denseK.Buffer.Span.CopyTo(outSelfK.AsSpan());
                else
                    outKTensor.ToArray().AsSpan().CopyTo(outSelfK.AsSpan());
                if (outVTensor is DenseTensor<float> denseV)
                    denseV.Buffer.Span.CopyTo(outSelfV.AsSpan());
                else
                    outVTensor.ToArray().AsSpan().CopyTo(outSelfV.AsSpan());

                // Swap buffers: output becomes next input
                (curSelfK, outSelfK) = (outSelfK, curSelfK);
                (curSelfV, outSelfV) = (outSelfV, curSelfV);

                offset += nTokens;

                // Set up single-token tensor for next step (no allocation)
                _singleTokenBuf[0] = bestId;
            }

            // ── Decode tokens to text ──
            var outputIds = generatedIds.Skip(_promptIds.Length).ToList();
            string text = string.Join("", outputIds
                .Where(id => _tokens.ContainsKey(id))
                .Select(id => _tokens[id])
                .Select(t => t.StartsWith("<|") ? "" : t.Replace("\u2581", " ")));

            return text.Trim();
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private int[] BuildPromptTokens(string language)
        {
            var parts = new[]
            {
                "<|startofcontext|>",
                "<|startoftranscript|>",
                "<|emo:undefined|>",
                $"<|{language}|>",
                $"<|{language}|>",
                "<|pnc|>",
                "<|noitn|>",
                "<|notimestamp|>",
                "<|nodiarize|>",
            };
            return parts
                .Where(t => _tokenToId.ContainsKey(t))
                .Select(t => _tokenToId[t])
                .ToArray();
        }

        private static Dictionary<int, string> LoadTokens(string path)
        {
            var tokens = new Dictionary<int, string>();
            foreach (var line in File.ReadAllLines(path))
            {
                int lastSpace = line.LastIndexOf(' ');
                if (lastSpace < 0) continue;
                string token = line.Substring(0, lastSpace);
                if (int.TryParse(line.Substring(lastSpace + 1), out int id))
                    tokens[id] = token;
            }
            return tokens;
        }

        /// <summary>
        /// Reads a 16-bit PCM WAV file and returns mono float32 samples.
        /// Handles stereo by averaging channels.
        /// </summary>
        private static float[] ReadWav(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));

            reader.ReadBytes(4); // "RIFF"
            reader.ReadInt32();  // file size
            reader.ReadBytes(4); // "WAVE"

            int sampleRate = 0;
            int numChannels = 1;
            int bitsPerSample = 16;

            while (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                string chunkId = new string(reader.ReadChars(4));
                int chunkSize = reader.ReadInt32();

                if (chunkId == "fmt ")
                {
                    reader.ReadInt16();  // audio format
                    numChannels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32();  // byte rate
                    reader.ReadInt16();  // block align
                    bitsPerSample = reader.ReadInt16();
                    if (chunkSize > 16)
                        reader.ReadBytes(chunkSize - 16);
                }
                else if (chunkId == "data")
                {
                    int bytesPerSample = bitsPerSample / 8;
                    int numSamples = chunkSize / bytesPerSample / numChannels;
                    var samples = new float[numSamples];

                    for (int i = 0; i < numSamples; i++)
                    {
                        float sample = 0;
                        for (int ch = 0; ch < numChannels; ch++)
                        {
                            short s = reader.ReadInt16();
                            sample += s / 32768.0f;
                        }
                        samples[i] = sample / numChannels;
                    }

                    if (sampleRate != SampleRate)
                        throw new InvalidOperationException(
                            $"Audio is {sampleRate}Hz but model requires {SampleRate}Hz. " +
                            "WhisperInk records at 16kHz so this should not happen.");

                    return samples;
                }
                else
                {
                    reader.ReadBytes(chunkSize);
                }
            }

            throw new InvalidDataException("No 'data' chunk found in WAV file.");
        }

        public void Dispose()
        {
            _encoder?.Dispose();
            _decoder?.Dispose();
            _encoder = null;
            _decoder = null;
            _isLoaded = false;
        }
    }
}
