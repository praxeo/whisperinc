using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    // ════════════════════════════════════════════════════════════════════
    // Drop-in local models. A CrispASR model (.gguf) copied into the model
    // folder is listed in 🔌 Provider as "➕ <name>"; one click adds a local
    // provider pinned to that exact file, on a port of its own, and switches
    // to it. No config.json edit, no rebuild. CrispASR itself works out which
    // of its backends runs the file; WhisperInk only reads the header to name
    // the model, skip files that aren't speech-to-text, and see whether it
    // needs punctuation restored.
    // ════════════════════════════════════════════════════════════════════

    /// <summary>What a GGUF model file says about itself, read from its header
    /// without loading the model.</summary>
    /// <param name="Architecture">general.architecture ("parakeet",
    /// "granite_speech", "qwen3asr", …): what the model is. Null when the
    /// converter didn't write it.</param>
    /// <param name="Name">general.name, when the converter wrote one.</param>
    /// <param name="VocabHasCasedWords">Whether the tokenizer has a word
    /// piece with a capital in it ("▁The", "ĠThe", "St"): a model trained on
    /// cased text has thousands, one trained on raw lowercase text has none.
    /// Null when the file has no tokenizer.ggml.tokens list. False means the
    /// model writes lowercase with no punctuation (Parakeet RNNT 1.1b), which
    /// is what --punc-model is for.</param>
    public sealed record GgufInfo(int Version, string? Architecture, string? Name, bool? VocabHasCasedWords);

    /// <summary>Reads the metadata at the start of a GGUF file: the
    /// architecture, the name, and one fact about the tokenizer. It stops as
    /// soon as it has them, which for every model measured on 2026-09-24 is
    /// within the first 120 KB, and never reads more than 64 MB whatever the
    /// file claims.</summary>
    public static class GgufHeader
    {
        /// <summary>The problem reported for a file that something still has
        /// open for writing: a copy or a download that hasn't finished.</summary>
        public const string StillWriting = "still being written (a copy or download in progress)";

        private const uint Magic = 0x46554747;   // "GGUF", little-endian
        private const long MaxHeaderBytes = 64L << 20;
        private const int MaxKeyBytes = 64 << 10;
        private const int MaxValueBytes = 1 << 20;

        private enum GgufType : uint { U8, I8, U16, I16, U32, I32, F32, Bool, Str, Arr, U64, I64, F64 }

        /// <summary>Reads a GGUF header. Throws InvalidDataException or
        /// EndOfStreamException for anything that isn't a readable one.</summary>
        public static GgufInfo Read(Stream stream)
        {
            var r = new Reader(stream);
            if (r.U32() != Magic) throw new InvalidDataException("not a GGUF file");
            uint version = r.U32();
            // v1 wrote 32-bit lengths; ggml, and so CrispASR, no longer loads it.
            if (version == 1) throw new InvalidDataException("GGUF v1, which CrispASR no longer loads");
            if (version > 3) throw new InvalidDataException($"GGUF v{version}, newer than this reader knows");
            r.U64();                               // tensor count
            ulong kvCount = r.U64();
            if (kvCount > 1_000_000) throw new InvalidDataException($"implausible metadata count ({kvCount})");

            string? arch = null, name = null;
            bool? vocab = null;
            for (ulong i = 0; i < kvCount && !(arch != null && vocab != null); i++)
            {
                string key = r.Str(MaxKeyBytes);
                var type = (GgufType)r.U32();
                if (type == GgufType.Str && key == "general.architecture") arch = r.Str(MaxValueBytes);
                else if (type == GgufType.Str && key == "general.name") name = r.Str(MaxValueBytes);
                else if (type == GgufType.Arr && key == "tokenizer.ggml.tokens") vocab = ScanVocabulary(r, stopAtAnswer: arch != null);
                else r.SkipValue(type, 0);
            }
            return new GgufInfo((int)version, arch, name, vocab);
        }

        /// <summary>Reads a GGUF file's header. Null, with the reason, for a
        /// file that isn't a readable GGUF or is still being written.</summary>
        public static GgufInfo? TryRead(string path, out string? problem)
        {
            try
            {
                // FileShare.Read without Write: while anything still has the
                // file open for writing, this open fails, which is the signal
                // that a copy or download hasn't finished.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.Read | FileShare.Delete, bufferSize: 64 * 1024);
                problem = null;
                return Read(fs);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)   // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
            {
                problem = StillWriting;
            }
            catch (EndOfStreamException)
            {
                problem = "the file ends inside its header (an incomplete download?)";
            }
            catch (InvalidDataException ex)
            {
                problem = ex.Message;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem = $"can't be read ({ex.Message})";
            }
            return null;
        }

        // Looks through the token list until a cased word piece turns up.
        // With the architecture already known, that's the last thing wanted
        // from the file, so it stops there; otherwise it consumes the rest of
        // the list unread, so the metadata after it can still be parsed. Null
        // for a list that isn't strings.
        private static bool? ScanVocabulary(Reader r, bool stopAtAnswer)
        {
            var elem = (GgufType)r.U32();
            ulong count = r.U64();
            if (elem != GgufType.Str)
            {
                r.SkipArray(elem, count, 1);
                return null;
            }
            bool found = false;
            for (ulong i = 0; i < count; i++)
            {
                ulong len = r.U64();
                if (found || len > (ulong)r.Scratch.Length)
                {
                    r.Skip(len);
                    continue;
                }
                var token = r.Scratch.AsSpan(0, (int)len);
                r.Take(token);
                if (IsCasedWordPiece(token))
                {
                    found = true;
                    if (stopAtAnswer) return true;
                }
            }
            return found;
        }

        private static ReadOnlySpan<byte> SentencePieceSpace => new byte[] { 0xE2, 0x96, 0x81 };   // "▁"
        private static ReadOnlySpan<byte> ByteLevelSpace => new byte[] { 0xC4, 0xA0 };            // "Ġ"

        /// <summary>A word piece with a capital in it: two or more characters
        /// once the word-boundary marker is off ("▁The", "ĠThe", "St"). Single
        /// characters don't count, because a byte-level vocabulary carries the
        /// whole alphabet and every mark whatever the model was trained on:
        /// Granite Speech 5.0's has "A" to "Z" and "." but not one capital in
        /// its 16,127 merges. Neither do special and byte-fallback tokens
        /// (&lt;unk&gt;, &lt;0x41&gt;, [INST]), which every vocabulary has.
        /// Checked 2026-09-24 against eight real vocabularies: Parakeet RNNT
        /// 1.1b and Granite 5.0 have none; Parakeet TDT v2 has 38, v3 431, and
        /// Cohere, Qwen3-ASR, Granite 4.1 and Gemma 4 over a thousand.</summary>
        internal static bool IsCasedWordPiece(ReadOnlySpan<byte> token)
        {
            if (token.IsEmpty || token[0] == (byte)'<' || token[0] == (byte)'[') return false;
            var text = token;
            if (text.StartsWith(SentencePieceSpace)) text = text[SentencePieceSpace.Length..];
            else if (text.StartsWith(ByteLevelSpace)) text = text[ByteLevelSpace.Length..];
            else if (text[0] == (byte)' ') text = text[1..];
            if (text.Length < 2) return false;
            foreach (byte b in text)
                if (b is >= (byte)'A' and <= (byte)'Z') return true;
            return false;
        }

        private sealed class Reader
        {
            private readonly Stream _s;
            private readonly byte[] _num = new byte[8];
            private long _consumed;

            /// <summary>Holds one token at a time while the vocabulary is scanned.</summary>
            public readonly byte[] Scratch = new byte[256];

            public Reader(Stream s) => _s = s;

            public uint U32() { Take(_num.AsSpan(0, 4)); return BinaryPrimitives.ReadUInt32LittleEndian(_num); }
            public ulong U64() { Take(_num.AsSpan(0, 8)); return BinaryPrimitives.ReadUInt64LittleEndian(_num); }

            public void Take(Span<byte> into)
            {
                Count(into.Length);
                _s.ReadExactly(into);
            }

            public string Str(int maxBytes)
            {
                ulong len = U64();
                if (len > (ulong)maxBytes) throw new InvalidDataException($"a {len}-byte string where at most {maxBytes} is plausible");
                var bytes = new byte[(int)len];
                Take(bytes);
                return Encoding.UTF8.GetString(bytes);
            }

            public void Skip(ulong n)
            {
                if (n > (ulong)MaxHeaderBytes) throw new InvalidDataException("the metadata runs past 64 MB");
                Count((long)n);
                if (_s.CanSeek)
                {
                    long to = _s.Position + (long)n;
                    if (to > _s.Length) throw new EndOfStreamException();
                    _s.Position = to;
                    return;
                }
                var sink = new byte[Math.Min(n, 64 * 1024)];
                while (n > 0)
                {
                    int chunk = (int)Math.Min(n, (ulong)sink.Length);
                    _s.ReadExactly(sink, 0, chunk);
                    n -= (ulong)chunk;
                }
            }

            public void SkipValue(GgufType type, int depth)
            {
                switch (type)
                {
                    case GgufType.U8 or GgufType.I8 or GgufType.Bool: Skip(1); break;
                    case GgufType.U16 or GgufType.I16: Skip(2); break;
                    case GgufType.U32 or GgufType.I32 or GgufType.F32: Skip(4); break;
                    case GgufType.U64 or GgufType.I64 or GgufType.F64: Skip(8); break;
                    case GgufType.Str: Skip(U64()); break;
                    case GgufType.Arr:
                        var elem = (GgufType)U32();
                        SkipArray(elem, U64(), depth + 1);
                        break;
                    default: throw new InvalidDataException($"unknown metadata type {(uint)type}");
                }
            }

            public void SkipArray(GgufType elem, ulong count, int depth)
            {
                if (depth > 4) throw new InvalidDataException("metadata arrays nested too deep");
                int size = elem switch
                {
                    GgufType.U8 or GgufType.I8 or GgufType.Bool => 1,
                    GgufType.U16 or GgufType.I16 => 2,
                    GgufType.U32 or GgufType.I32 or GgufType.F32 => 4,
                    GgufType.U64 or GgufType.I64 or GgufType.F64 => 8,
                    _ => 0,
                };
                if (size > 0)
                {
                    if (count > (ulong)MaxHeaderBytes / (ulong)size) throw new InvalidDataException("the metadata runs past 64 MB");
                    Skip(count * (ulong)size);
                    return;
                }
                // Strings and nested arrays are at least 8 bytes each.
                if (count > (ulong)MaxHeaderBytes / 8) throw new InvalidDataException("the metadata runs past 64 MB");
                for (ulong i = 0; i < count; i++) SkipValue(elem, depth);
            }

            private void Count(long n)
            {
                _consumed += n;
                if (_consumed > MaxHeaderBytes) throw new InvalidDataException("the metadata runs past 64 MB");
            }
        }
    }

    public enum ModelUse
    {
        /// <summary>An architecture CrispASR runs as speech-to-text.</summary>
        SpeechToText,
        /// <summary>A voice, a punctuation or language-ID helper, translation,
        /// music: CrispASR runs it, but it doesn't transcribe dictation.</summary>
        NotSpeechToText,
        /// <summary>An architecture WhisperInk doesn't know. Offered anyway:
        /// CrispASR adds models faster than this list, and it's what decides
        /// whether it can run the file.</summary>
        Unrecognized,
    }

    /// <summary>One .gguf in the model folder, as the last scan found it.
    /// <see cref="Info"/> is null when its header couldn't be read, and
    /// <see cref="Problem"/> says why.</summary>
    public sealed record LocalModelFile(string Path, long Bytes, DateTime ModifiedUtc, GgufInfo? Info, string? Problem)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
        public bool StillWriting => Problem == GgufHeader.StillWriting;
        public ModelUse Use => LocalModels.UseOf(Info?.Architecture);
    }

    /// <summary>What to do with a model file: whether to offer it, what to call
    /// it, and the local provider it becomes.</summary>
    public static class LocalModels
    {
        /// <summary>First port handed to an added model. Shipped presets take
        /// theirs from 8103 up (8103-8112 so far, and 8766), and the owner's
        /// own crispasr servers use 8001 and 8880 (CLAUDE.md 6.1). Starting
        /// added models at 8200 keeps a preset shipped later from landing on a
        /// port an added model already has.</summary>
        public const int FirstAutoPort = 8200;

        // Speech-to-text architectures, by the general.architecture value
        // CrispASR's converters write (upstream's src/core/arch_backend_map.h),
        // with a family name to show.
        private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
        {
            ["parakeet"] = "Parakeet", ["parakeet-tdt"] = "Parakeet", ["parakeet-ja"] = "Parakeet", ["parakeet_ja"] = "Parakeet",
            ["fastconformer-ctc"] = "FastConformer CTC", ["stt-fastconformer-ctc"] = "FastConformer CTC",
            ["stt_fastconformer_ctc"] = "FastConformer CTC", ["canary-ctc"] = "FastConformer CTC",
            ["canary"] = "Canary", ["canary-qwen"] = "Canary-Qwen", ["canary_qwen"] = "Canary-Qwen",
            ["nemotron"] = "Nemotron", ["nemotron-asr"] = "Nemotron", ["nemotron-streaming"] = "Nemotron",
            ["qwen3asr"] = "Qwen3-ASR", ["qwen3-asr"] = "Qwen3-ASR", ["qwen3_asr"] = "Qwen3-ASR",
            ["granite_speech"] = "Granite Speech", ["granite-speech"] = "Granite Speech", ["granitespeech"] = "Granite Speech",
            ["granite_nle"] = "Granite Speech NAR", ["granite-nle"] = "Granite Speech NAR", ["granitenle"] = "Granite Speech NAR",
            ["cohere-transcribe"] = "Cohere Transcribe", ["cohere"] = "Cohere Transcribe",
            ["voxtral"] = "Voxtral", ["voxtral4b"] = "Voxtral Realtime", ["voxtral-4b"] = "Voxtral Realtime", ["voxtral_4b"] = "Voxtral Realtime",
            ["whisper"] = "Whisper", ["moonshine"] = "Moonshine", ["moonshine_streaming"] = "Moonshine Streaming",
            ["gemma4e2b"] = "Gemma 4 E2B", ["gemma4_e2b"] = "Gemma 4 E2B",
            ["gigaam"] = "GigaAM", ["firered-asr"] = "FireRedASR", ["firered_asr"] = "FireRedASR", ["firered"] = "FireRedASR",
            ["sensevoice"] = "SenseVoice", ["funasr"] = "FunASR", ["paraformer"] = "Paraformer",
            ["omniasr"] = "OmniASR", ["glm-asr"] = "GLM-ASR", ["glmasr"] = "GLM-ASR", ["glm_asr"] = "GLM-ASR",
            ["kyutai-stt"] = "Kyutai STT", ["kyutai_stt"] = "Kyutai STT",
            ["wav2vec2"] = "wav2vec 2.0", ["hubert"] = "HuBERT", ["data2vec"] = "data2vec",
        };

        // Families that accept the hotwords field and ignore it, measured on
        // the clinical clips (CLAUDE.md 4.3). Their provider says "none", so
        // the settings dialog doesn't claim the Context Bias list does anything.
        private static readonly HashSet<string> IgnoreHotwords = new(StringComparer.Ordinal) { "Cohere Transcribe", "Voxtral Realtime" };

        // Architectures that aren't speech-to-text: voices, punctuation,
        // truecasing and language-ID helpers, alignment, embeddings,
        // translation, music and source separation. CrispASR runs them all,
        // so one parked in the model folder would otherwise be offered as a
        // dictation model.
        private static readonly Regex NotSpeech = new(
            @"tts|punc|truecas|(^|[-_])lid([-_]|$)|(^|[-_])vad([-_]|$)|aligner|vae|vocoder|codec|embed|" +
            @"kokoro|styletts|piper|^vits|melo|orpheus|chatterbox|kartoffel|bark|^dia$|^csm|zonos|supertonic|irodori|" +
            @"^tada|omnivoice|bananamind|fastpitch|speecht5|cosyvoice|voxcpm|indextts|parler|^confucius4$|miotts|kugelaudio|" +
            @"demucs|roformer|crepe|^btc|tabcnn|^rvc|beat|piano|pitch|^mt3|onsets|hft|m2m|madlad|^t5$|sidon",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static ModelUse UseOf(string? architecture)
        {
            if (string.IsNullOrWhiteSpace(architecture)) return ModelUse.Unrecognized;
            if (Families.ContainsKey(architecture)) return ModelUse.SpeechToText;
            return NotSpeech.IsMatch(architecture) ? ModelUse.NotSpeechToText : ModelUse.Unrecognized;
        }

        /// <summary>"Parakeet", "Granite Speech NAR", …; null for an
        /// architecture not in the list.</summary>
        public static string? FamilyOf(string? architecture) =>
            architecture != null && Families.TryGetValue(architecture, out var family) ? family : null;

        private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
            { "asr", "stt", "tdt", "ctc", "rnnt", "nar", "nle", "it", "llm", "e2b", "e4b" };

        /// <summary>A readable name from the file name, the way CrispASR's
        /// repos name them: "orukeet-q4_k.gguf" → "Orukeet Q4_K",
        /// "granite-speech-4.1-2b-nar-q4_k.gguf" → "Granite Speech 4.1 2B NAR Q4_K".
        /// The quantisation stays in, so two quants of one model are told apart.</summary>
        public static string DisplayName(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(fileName);
            var words = stem.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Word).ToList();
            return words.Count == 0 ? stem : string.Join(" ", words);

            static string Word(string w)
            {
                if (Regex.IsMatch(w, @"^(i?q\d\w*|b?f(16|32)\w*)$", RegexOptions.IgnoreCase))
                    return w.ToUpperInvariant();                                  // Q4_K, Q8_0, IQ4_XS, F16, BF16
                if (Regex.IsMatch(w, @"^\d+(\.\d+)?[kmb]$", RegexOptions.IgnoreCase))
                    return w[..^1] + char.ToUpperInvariant(w[^1]);                // 0.6B, 470M
                if (Regex.IsMatch(w, @"^v\d", RegexOptions.IgnoreCase))
                    return w.ToLowerInvariant();                                  // v3
                if (w.Contains('_'))
                    return string.Join("-", w.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(Word));   // tdt_ctc -> TDT-CTC
                if (Acronyms.Contains(w)) return w.ToUpperInvariant();
                return char.ToUpperInvariant(w[0]) + w[1..];
            }
        }

        /// <summary>Size in the units ProviderDiagnostics uses.</summary>
        public static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):F0} MB"
            : $"{Math.Max(1, bytes >> 10)} KB";

        /// <summary>One line on what the file is, for the menu's tooltip and
        /// the log: its name, size, family and what its vocabulary means.</summary>
        public static string Describe(LocalModelFile file)
        {
            var info = file.Info;
            if (info == null) return $"{file.FileName} · {FormatSize(file.Bytes)} · {file.Problem}";
            string arch = info.Architecture ?? "no architecture in its header";
            string what = FamilyOf(info.Architecture) is { } family ? $"{family} ({arch})"
                        : file.Use == ModelUse.NotSpeechToText ? $"{arch}, not a speech-to-text model"
                        : $"model type \"{arch}\", which WhisperInk doesn't know; CrispASR decides whether it runs it";
            string vocab = info.VocabHasCasedWords == false
                ? " · writes lowercase with no punctuation (no capitalised words in its vocabulary), so punctuation and sentence case are restored (--punc-model fullstop)"
                : "";
            return $"{file.FileName} · {FormatSize(file.Bytes)} · {what}{vocab}";
        }

        /// <summary>The local provider for a model file: pinned to that exact
        /// file, on the first free port from <see cref="FirstAutoPort"/>, with
        /// no --backend (CrispASR detects it from the file, from the same
        /// table its converters write), and a punctuation model only when the
        /// vocabulary shows the model can't punctuate.</summary>
        /// <param name="listeningPorts">Ports something already listens on
        /// (<see cref="ListeningPorts"/>), so the new server doesn't collide
        /// with a program WhisperInk doesn't know about.</param>
        public static ApiProvider CreateProvider(LocalModelFile file, IReadOnlyCollection<ApiProvider> existing, ISet<int> listeningPorts)
        {
            if (file.Info == null) throw new ArgumentException($"{file.FileName}: {file.Problem}", nameof(file));
            int port = FreePort(existing, listeningPorts);
            bool ignoresHotwords = FamilyOf(file.Info.Architecture) is { } family && IgnoreHotwords.Contains(family);
            return new ApiProvider
            {
                Id = UniqueId(existing, file.FileName),
                Name = $"{DisplayName(file.FileName)} (local)",
                BaseUrl = $"http://localhost:{port}",
                TranscriptionEndpoint = $"http://localhost:{port}/v1/audio/transcriptions",
                TranscriptionModel = file.Info.Architecture ?? "",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                // CrispAsrServerTranscriber sends the list as hotwords either
                // way; this only keeps the settings dialog's label honest.
                BiasMechanism = ignoresHotwords ? "none" : "hotwords",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = port,
                // The exact file, never a family glob: a glob is how a later
                // download silently took over the Granite and Parakeet presets.
                LocalModelGlob = file.FileName,
                LocalBackendHint = "",
                LocalPuncModel = file.Info.VocabHasCasedWords == false ? "fullstop" : "",
            };
        }

        /// <summary>The first port from <see cref="FirstAutoPort"/> that no
        /// provider uses and nothing listens on.</summary>
        public static int FreePort(IEnumerable<ApiProvider> existing, ISet<int> listeningPorts)
        {
            var taken = new HashSet<int>(listeningPorts);
            foreach (var p in existing)
            {
                if (p.LocalServerPort is int port) taken.Add(port);
                foreach (var url in new[] { p.BaseUrl, p.TranscriptionEndpoint })
                    if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0) taken.Add(u.Port);
            }
            for (int port = FirstAutoPort; port < 9000; port++)
                if (!taken.Contains(port)) return port;
            throw new InvalidOperationException($"no free port between {FirstAutoPort} and 8999");
        }

        /// <summary>Every TCP port something on this machine listens on. Empty
        /// if Windows won't say.</summary>
        public static ISet<int> ListeningPorts()
        {
            try { return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet(); }
            catch { return new HashSet<int>(); }
        }

        private static string UniqueId(IEnumerable<ApiProvider> existing, string fileName)
        {
            string slug = Regex.Replace(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), "[^a-z0-9._]+", "-").Trim('-');
            if (slug.Length == 0) slug = "model";
            var ids = existing.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            string id = "local-" + slug;
            for (int n = 2; ids.Contains(id); n++) id = $"local-{slug}-{n}";
            return id;
        }

        /// <summary>The model files local providers load now, resolved the way
        /// the transcriber resolves them (its folder, then the literal name,
        /// then the first glob match). A file in here is already in use; one
        /// that isn't is new.</summary>
        /// <param name="folderOf">The folder a provider's model is looked up
        /// in; the harness points it at a test folder.</param>
        public static HashSet<string> UsedModelFiles(IEnumerable<ApiProvider> providers, Func<ApiProvider, string>? folderOf = null)
        {
            folderOf ??= CrispAsrServerTranscriber.ResolveModelFolder;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in providers)
            {
                if (!p.IsLocalProvider || string.IsNullOrWhiteSpace(p.LocalModelGlob)) continue;
                try
                {
                    string path = CrispAsrServerTranscriber.ResolveModel(folderOf(p), p.LocalModelGlob);
                    if (File.Exists(path)) used.Add(Path.GetFullPath(path));
                }
                catch { }   // a malformed glob in config.json: that provider claims nothing
            }
            return used;
        }

        /// <summary>The files to offer: speech-to-text (or unrecognized)
        /// models no provider uses, plus unreadable ones so the menu can say
        /// why they aren't offered. Newest first.</summary>
        public static List<LocalModelFile> NewModels(IEnumerable<LocalModelFile> scanned, IEnumerable<ApiProvider> providers,
                                                     Func<ApiProvider, string>? folderOf = null)
        {
            var used = UsedModelFiles(providers, folderOf);
            return scanned
                .Where(f => !used.Contains(Path.GetFullPath(f.Path)) && f.Use != ModelUse.NotSpeechToText)
                .OrderByDescending(f => f.ModifiedUtc)
                .ToList();
        }
    }

    /// <summary>
    /// Keeps a current list of the .gguf files in the model folder, off the UI
    /// thread. A FileSystemWatcher (and every menu open) triggers a rescan; only
    /// files whose size or time changed are opened again. The menu reads
    /// <see cref="Snapshot"/>, so building it never touches a multi-gigabyte
    /// file, which on a fresh download can mean waiting for a virus scan.
    /// </summary>
    public sealed class LocalModelScanner : IDisposable
    {
        // A copy in progress raises a stream of Changed events; this lets a
        // burst settle into one scan.
        private const int DebounceMs = 300;

        private readonly Action<string> _log;
        private readonly object _gate = new();
        private readonly Dictionary<string, LocalModelFile> _known = new(StringComparer.OrdinalIgnoreCase);
        // "path|state" for what the log has already said about a file.
        private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<LocalModelFile> _snapshot = Array.Empty<LocalModelFile>();
        private FileSystemWatcher? _watcher;
        private bool _scannedOnce;
        private int _running;
        private int _pending;
        private volatile bool _disposed;

        public string Folder { get; }

        public LocalModelScanner(string folder, Action<string>? log)
        {
            Folder = folder;
            _log = log ?? (_ => { });
        }

        /// <summary>The files the last scan found, newest first.</summary>
        public IReadOnlyList<LocalModelFile> Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>Rescans in the background, soon. Calls made while a scan
        /// runs are folded into one more.</summary>
        public void Refresh()
        {
            if (_disposed) return;
            Volatile.Write(ref _pending, 1);
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (Interlocked.Exchange(ref _pending, 0) == 1 && !_disposed)
                    {
                        await Task.Delay(DebounceMs).ConfigureAwait(false);
                        Scan();
                    }
                }
                catch (Exception ex)
                {
                    _log($"[models] scanning {Folder} failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _running, 0);
                    // A request that landed between the loop's last check and
                    // the line above would otherwise wait for the next one.
                    if (Volatile.Read(ref _pending) == 1) Refresh();
                }
            });
        }

        /// <summary>Scans now, on the calling thread, and returns the new
        /// snapshot.</summary>
        public IReadOnlyList<LocalModelFile> Scan()
        {
            lock (_gate)
            {
                EnsureWatcher();
                var found = new List<LocalModelFile>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(Folder))
                {
                    foreach (var path in Directory.EnumerateFiles(Folder, "*.gguf"))
                    {
                        if (!path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
                        FileInfo fi;
                        try { fi = new FileInfo(path); _ = fi.Length; }
                        catch { continue; }   // gone between the listing and here
                        seen.Add(path);
                        if (_known.TryGetValue(path, out var known) && !known.StillWriting
                            && known.Bytes == fi.Length && known.ModifiedUtc == fi.LastWriteTimeUtc)
                        {
                            found.Add(known);
                            continue;
                        }
                        var info = GgufHeader.TryRead(path, out string? problem);
                        var file = new LocalModelFile(path, fi.Length, fi.LastWriteTimeUtc, info, problem);
                        _known[path] = file;
                        Report(file);
                        found.Add(file);
                    }
                }
                foreach (var gone in _known.Keys.Where(k => !seen.Contains(k)).ToList())
                    _known.Remove(gone);
                _scannedOnce = true;
                var snapshot = found.OrderByDescending(f => f.ModifiedUtc).ToList();
                Volatile.Write(ref _snapshot, snapshot);
                return snapshot;
            }
        }

        // One line per file per state. The files already there at startup are
        // only mentioned if something is wrong with them; one that arrives
        // while WhisperInk runs is always mentioned, which answers "I copied
        // it in, why isn't it in the menu?" from the log.
        private void Report(LocalModelFile f)
        {
            string state = f.Problem ?? f.Info?.Architecture ?? "";
            if (!_reported.Add(f.Path + "|" + state)) return;
            if (f.StillWriting)
                _log($"[models] {f.FileName}: {GgufHeader.StillWriting}; it's offered once that finishes");
            else if (f.Info == null)
                _log($"[models] {f.FileName} isn't offered: {f.Problem}");
            else if (f.Use == ModelUse.NotSpeechToText)
            {
                if (_scannedOnce) _log($"[models] {f.FileName} is a {f.Info.Architecture} model, not speech-to-text; not offered");
            }
            else if (_scannedOnce)
                _log($"[models] new model file: {LocalModels.Describe(f)}");
        }

        private void EnsureWatcher()
        {
            if (_watcher != null || _disposed || !Directory.Exists(Folder)) return;
            try
            {
                var w = new FileSystemWatcher(Folder, "*.gguf")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                w.Created += (_, _) => Refresh();
                w.Changed += (_, _) => Refresh();
                w.Deleted += (_, _) => Refresh();
                w.Renamed += (_, _) => Refresh();   // a download that ends by renaming its .part file
                w.Error += (_, _) => Refresh();     // the event buffer overflowed: rescan to be sure
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex)
            {
                _log($"[models] can't watch {Folder} ({ex.Message}); new files show on the next menu open");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            lock (_gate)
            {
                try { _watcher?.Dispose(); } catch { }
                _watcher = null;
            }
        }
    }
}
