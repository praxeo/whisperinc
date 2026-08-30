using System;
using System.Collections.Generic;

namespace WhisperInk
{
    /// <summary>
    /// Which ITranscriber implementation handles batch transcription for a
    /// provider. Determines which factory branch instantiates it and which
    /// fields on ApiProvider are meaningful. New providers added via
    /// config.json must set this; legacy configs without the field fall
    /// through to <see cref="Http"/>.
    /// </summary>
    public enum TranscriberKind
    {
        Http,                 // OpenAI-compatible multipart POST (Mistral batch, OpenAI, Cohere cloud, ElevenLabs, …)
        LocalCrispAsrServer,  // Auto-spawned crispasr.exe --server (all GGUF backends)
        GoogleChirp3,         // Google Cloud STT v2 with OAuth + JSON body
        Soniox,               // Soniox async REST job: upload → create → poll → transcript (SonioxTranscriber)
        Deepgram,             // Deepgram Listen API: raw-body POST, Token auth, query-param options (DeepgramTranscriber)
        Modulate,             // Modulate Velma 2 batch: multipart upload_file, X-API-Key, model-per-endpoint (ModulateTranscriber)
        Smallest,             // Smallest.ai Waves STT: raw-body POST, Bearer auth, query-param options (SmallestTranscriber)
        Reson8,               // Reson8 prerecorded STT: raw-body POST, "ApiKey" auth, query-param options, RFC 7807 errors (Reson8Transcriber)
    }

    public class ApiProvider
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "New Provider";

        // Base URL (no trailing slash). Used to build chat endpoint and as a
        // fallback for transcription when TranscriptionEndpoint is blank.
        // e.g. "https://api.mistral.ai", "https://api.openai.com"
        public string BaseUrl { get; set; } = "";

        public string ApiKey { get; set; } = "";

        // ── Transcription endpoint override ─────────────────────────────
        // Full URL for the transcription POST.
        // If blank, defaults to {BaseUrl}/v1/audio/transcriptions (OpenAI-compat).
        // Examples:
        //   ""                                                  → https://api.mistral.ai/v1/audio/transcriptions
        //   "https://api.elevenlabs.io/v1/speech-to-text"       → used as-is
        //   "https://api.cohere.com/v2/audio/transcriptions"    → used as-is
        public string TranscriptionEndpoint { get; set; } = "";

        // ── Auth header name ────────────────────────────────────────────
        // How the API key is sent. Default empty → "Authorization: Bearer {key}"
        // Set to e.g. "xi-api-key" to send "xi-api-key: {key}" instead.
        public string AuthHeaderName { get; set; } = "";

        // ── Model field name in multipart form ──────────────────────────
        // Field name for the model identifier in transcription requests.
        // Default empty → "model". Set to "model_id" for ElevenLabs, etc.
        public string ModelFieldName { get; set; } = "";

        // Model identifier — leave blank to use the endpoint's default
        public string TranscriptionModel { get; set; } = "";

        // ── Language ─────────────────────────────────────────────────────────
        // Language code for transcription (e.g., "en", "es", "fr").
        // Explicit language setting improves accuracy by constraining the model's search space.
        // Defaults to "en" if not set.
        public string Language { get; set; } = "en";

        // Whether batch transcription is available
        public bool SupportsTranscription { get; set; } = true;

        // ── Temperature ─────────────────────────────────────────────────
        // Optional temperature for transcription (null = use endpoint default).
        // Cohere recommends 0.1 for deterministic medical output.
        // OpenAI Whisper accepts 0.0 (fully deterministic).
        // Send as a multipart form field BEFORE the file part.
        public double? TranscriptionTemperature { get; set; } = null;

        // ── Context biasing ───────────────────────────────────────────────
        // The single shared AppConfig.ContextBiasTerms list is routed to each
        // provider's NATIVE vocabulary-steering field, chosen by BiasMechanism
        // (baked per provider in CreateDefaults — never user-set). The user
        // enters terms once; each provider sends them the way it actually supports.
        //
        //   "none"                 — provider has no biasing field
        //   "whisper_prompt"       — labeled glossary in the "prompt" form field
        //                            (OpenAI Whisper, local prompt-conditioned servers)
        //   "mistral_context_bias" — comma-joined string in the "context_bias" field
        //                            (Mistral Voxtral batch; <=100 terms)
        //   "elevenlabs_keyterms"  — repeated "keyterms" form fields (ElevenLabs Scribe v2)
        //   "hotwords"             — comma-joined "hotwords" form field (CrispASR local)
        //   "phrase_sets"          — Google Chirp 3 inline phraseSets (handled natively)
        //   "context_terms"        — Soniox context.terms (handled natively)
        //   "deepgram_keyterm"     — Deepgram Nova-3 keyterm prompting / legacy
        //                            `keywords` query params (handled natively)
        //   "modulate_custom_terms" — Modulate custom_terms, inside the JSON
        //                            `config` form field (handled natively;
        //                            Velma 2 Multilingual batch only)
        //   "reson8_phrases"       — Reson8 comma-separated `phrases` query
        //                            param, up to 250 (handled natively).
        //                            NB: on this provider an over-long list
        //                            actively DEGRADES accuracy per upstream,
        //                            rather than being merely ignored.
        //
        // Blank → derived from the legacy ContextBiasMode via ResolvedBiasMechanism.
        public string BiasMechanism { get; set; } = "";

        // Legacy field, kept only so older config.json files still deserialize and
        // so user-added providers without an explicit BiasMechanism still route
        // sensibly. No longer user-editable. Values: "none" | "whisper_prompt" |
        // "cohere_terms" (the last now maps to "none" — Cohere v2 has no bias field).
        public string ContextBiasMode { get; set; } = "none";

        // Per-term hotword boost for the CrispASR Parakeet trie (CTC/TDT/RNNT).
        // null → server default (2.0, effectively inert); ~10 nudges rare terms
        // without heavy collateral. Only meaningful on a Parakeet backend; ignored
        // by Cohere/Voxtral/Granite GGUF backends (their hotwords are accepted but no-op).
        public double? HotwordsBoost { get; set; } = null;

        // ── ElevenLabs Scribe v2 keyterms ────────────────────────────────
        // Raw newline-delimited vocabulary hints. Only sent when this provider
        // uses xi-api-key auth (ElevenLabs). Parsed and validated on send, not
        // on every keystroke. Max 1000 terms, <50 chars, ≤5 words each.
        // ~20% cost surcharge per ElevenLabs pricing.
        public string ScribeKeytermsRaw { get; set; } = "";

        // ── ElevenLabs Scribe v2 audio event tagging ─────────────────────
        // When true, transcript includes (laughter), (coughing), etc.
        // Default false → clean clinical dictation output. API default is true,
        // so we MUST send this field explicitly to suppress events.
        // Only sent when this provider uses xi-api-key auth (ElevenLabs).
        public bool TagAudioEvents { get; set; } = false;

        // ── ElevenLabs Scribe v2 no_verbatim ─────────────────────────────
        // When true, strips filler words ("um", "uh"), false starts, and
        // non-speech sounds from the transcript. Default true → clean
        // dictation. CAVEAT: may also strip meaningful hesitations if
        // transcribing patient interviews — flip to false for those.
        // Scribe v2 only. Only sent for xi-api-key auth (ElevenLabs).
        public bool NoVerbatim { get; set; } = true;

        // ── Transcriber dispatch ─────────────────────────────────────────
        // Decides which ITranscriber implementation the factory instantiates
        // for this provider. Cloud/HTTP providers leave this at Http; local
        // GGUF servers set LocalCrispAsrServer plus the Local* fields below.
        public TranscriberKind TranscriberKind { get; set; } = TranscriberKind.Http;

        // ── LocalCrispAsrServer fields ──────────────────────────────────
        // All ignored unless TranscriberKind == LocalCrispAsrServer.

        // Port the auto-spawned crispasr.exe binds to. Null → derived from BaseUrl.
        // New models should use 81xx to avoid colliding with the legacy 8766–8768
        // band and other localhost services.
        public int? LocalServerPort { get; set; } = null;

        // Filename glob CrispASR matches against LocalModelFolder.
        // e.g. "parakeet-*.gguf", "cohere-transcribe-q4_k.gguf". A literal
        // filename (no wildcard) is matched as-is.
        public string LocalModelGlob { get; set; } = "";

        // Optional --backend hint for crispasr.exe. Needed when GGUF metadata
        // doesn't carry enough info for auto-detect: Cohere → "cohere",
        // Voxtral → "voxtral", Granite → "granite". Parakeet/Canary auto-detect;
        // leave blank.
        public string LocalBackendHint { get; set; } = "";

        // Per-provider GPU backend override ("auto" | "cuda" | "vulkan" | "metal" | "cpu").
        // Blank → fall back to AppConfig.CrispGpuBackend (the global setting).
        // Useful for pinning a preset to "cpu" even when the global default is "auto".
        public string LocalGpuBackend { get; set; } = "";

        // Folder under %APPDATA%\.WhisperInk\ holding the GGUF + crispasr.exe.
        // Blank → "cohere-gguf" (the unified location all newer presets share).
        public string LocalModelFolder { get; set; } = "";

        // Optional beam-search width sent as the "beam_size" form field.
        // null → server default (greedy). Requires CrispASR v0.7+ (older
        // servers ignore unknown fields). Beam search is implemented for
        // Cohere, Parakeet TDT/RNNT, Canary, FunASR and others.
        public int? LocalBeamSize { get; set; } = null;

        // Optional server-side punctuation model, passed to crispasr.exe as
        // --punc-model (e.g. "fullstop", "auto"/"firered", "punctuate-all",
        // "pcs", or a direct GGUF path). Blank → none. Restores punctuation +
        // sentence case for backends that emit raw lowercase text (Parakeet
        // RNNT/CTC). Server-mode --punc-model is upstream as of the §166 parity
        // series (CrispStrobe/CrispASR 36f35f2a + PCS/CTC auto-enable in
        // 8d803f04); older pre-§166 servers honored it only in CLI one-shot mode.
        public string LocalPuncModel { get; set; } = "";

        // Optional server-side truecasing model, passed to crispasr.exe as
        // --truecase-model (e.g. "auto", "crf", "lstm", or a direct path).
        // Applied AFTER punctuation. Blank → none. Adds proper-noun / acronym
        // casing on top of the sentence-case that --punc-model already restores,
        // for backends that emit lowercase text (Parakeet RNNT/CTC). Resident
        // startup flag, not per-request. Requires a §166-era CrispASR server.
        public string LocalTruecaseModel { get; set; } = "";

        // Per-provider passthrough of extra /v1/audio/transcriptions form fields,
        // merged verbatim into each request (e.g. {"punctuation":"false"},
        // {"vad":"true"}, {"seed":"42"}, {"suppress_nst":"true"}). The CrispASR
        // server (§166+) exposes most transcription params per request; this lets
        // config.json reach them with no recompile. Keys already set by the
        // transcriber (language / hotwords / hotwords_boost / beam_size /
        // response_format / file) are reserved and skipped. Older servers ignore
        // unknown fields.
        public Dictionary<string, string> LocalExtraParams { get; set; } = new();

        // Per-provider passthrough of extra Deepgram /v1/listen QUERY params,
        // merged verbatim into the request URL (e.g. {"measurements":"true"},
        // {"numerals":"true"}, {"dictation":"true"}, {"paragraphs":"true"}).
        // The Deepgram analog of LocalExtraParams: it lets a preset carry
        // calibration/formatting knobs (and any future Deepgram param) with no
        // recompile. Only consulted by DeepgramTranscriber. Keys the transcriber
        // already sets (model / smart_format / language / keyterm / keywords) are
        // reserved and skipped. Values are sent as-is (URL-encoded); Deepgram
        // ignores unknown params.
        public Dictionary<string, string> DeepgramExtraParams { get; set; } = new();

        // Per-provider passthrough of extra Reson8 prerecorded QUERY params,
        // merged verbatim into the request URL. The Deepgram analog for the
        // Reson8 path, and unlike Modulate/Smallest (which deliberately have no
        // passthrough because every knob they expose degrades dictation) this
        // one carries knobs worth having:
        //   {"custom_model_id":"..."}  persistent vocabulary, up to 50,000
        //                              phrases — far past the 250 that fit in
        //                              the per-request `phrases` param
        //   {"filler_mode":"clean"}    drop filler words ("um", "uh"); the API
        //                              default is "natural" (model decides),
        //                              "verbatim" keeps them
        //   {"patterns":"..."}         regex-style recovery of short
        //                              alphanumeric tokens (order codes, plates)
        // Only consulted by Reson8Transcriber. Keys it already sets — language /
        // phrases, plus encoding / sample_rate / channels (which describe the WAV
        // body itself) — are reserved and skipped.
        public Dictionary<string, string> Reson8ExtraParams { get; set; } = new();

        /// <summary>Validate an arbitrary term list against the ElevenLabs Scribe v2
        /// keyterm rules (≤1000 terms, &lt;50 chars, ≤5 words, no chars that break the
        /// multipart field / keyterm parser). Used for both the shared Context Bias
        /// list and any provider-only ScribeKeytermsRaw extras.</summary>
        public static List<string> ValidateKeyterms(IEnumerable<string> rawTerms, out List<string> warnings)
        {
            warnings = new List<string>();
            // Chars that break the multipart field encoding or ElevenLabs' keyterm parser.
            char[] forbidden = { '`', '<', '>', '{', '}', '[', ']', '\\' };

            var terms = rawTerms
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var valid = new List<string>();
            foreach (var t in terms)
            {
                if (t.Length >= 50)   { warnings.Add($"Dropped (>=50 chars): {t}"); continue; }
                if (t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length > 5)
                                      { warnings.Add($"Dropped (>5 words): {t}"); continue; }
                if (t.IndexOfAny(forbidden) >= 0)
                                      { warnings.Add($"Dropped (illegal char): {t}"); continue; }
                valid.Add(t);
            }

            if (valid.Count > 1000)
            {
                warnings.Add($"Truncated to 1000 (had {valid.Count}).");
                valid = valid.Take(1000).ToList();
            }
            return valid;
        }

        public override string ToString() => Name;

        /// <summary>Resolved transcription URL — uses override if set, else builds from BaseUrl.</summary>
        public string ResolvedTranscriptionUrl =>
            !string.IsNullOrWhiteSpace(TranscriptionEndpoint)
                ? TranscriptionEndpoint.TrimEnd('/')
                : $"{BaseUrl.TrimEnd('/')}/v1/audio/transcriptions";

        /// <summary>Resolved model field name — defaults to "model" if blank.</summary>
        public string ResolvedModelField =>
            !string.IsNullOrWhiteSpace(ModelFieldName) ? ModelFieldName : "model";

        /// <summary>True when auth should use a custom header instead of Authorization: Bearer.</summary>
        public bool UsesCustomAuthHeader => !string.IsNullOrWhiteSpace(AuthHeaderName);

        /// <summary>The effective biasing mechanism for this provider: the baked
        /// <see cref="BiasMechanism"/> when set, otherwise derived from the legacy
        /// <see cref="ContextBiasMode"/> so older configs and user-added providers
        /// still route sensibly. Cohere's old "cohere_terms" maps to "none" — Cohere
        /// Transcribe v2 has no biasing field, so it was always a silent no-op.</summary>
        public string ResolvedBiasMechanism
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(BiasMechanism) && BiasMechanism != "auto")
                    return BiasMechanism;
                return ContextBiasMode == "whisper_prompt" ? "whisper_prompt" : "none";
            }
        }

        /// <summary>True when the provider runs locally (no cloud HTTP roundtrip).</summary>
        public bool IsLocalProvider =>
            TranscriberKind == TranscriberKind.LocalCrispAsrServer;

        /// <summary>True for an OpenAI-compatible HTTP provider pointed at a
        /// local server (localhost / 127.0.0.1 / ::1) — e.g. a user-run
        /// Qwen3-ASR server. Needs no API key even though it isn't one of the
        /// built-in Local* transcriber kinds.</summary>
        public bool IsLocalHttp =>
            TranscriberKind == TranscriberKind.Http &&
            (BaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
             BaseUrl.Contains("127.0.0.1") ||
             BaseUrl.Contains("[::1]"));

        /// <summary>Whether this provider needs an API key / credential to work.
        /// Local in-process and localhost HTTP providers don't; cloud endpoints
        /// (and credentialed services like Google Chirp 3 / Soniox / Deepgram) do. Used by
        /// the health probe, diagnostics, and the record-start guard so local
        /// models never demand a (dummy) key.</summary>
        public bool RequiresApiKey => !IsLocalProvider && !IsLocalHttp;

        public static List<ApiProvider> CreateDefaults() => new()
        {
            new ApiProvider
            {
                Id = "mistral",
                BiasMechanism = "mistral_context_bias",
                Name = "Mistral",
                BaseUrl = "https://api.mistral.ai",
                TranscriptionModel = "voxtral-mini-latest",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Http,
            },
            new ApiProvider
            {
                Id = "openai",
                BiasMechanism = "whisper_prompt",
                Name = "OpenAI",
                BaseUrl = "https://api.openai.com",
                TranscriptionModel = "whisper-1",
                SupportsTranscription = true,
                TranscriptionTemperature = 0.0,
                ContextBiasMode = "whisper_prompt",
                Language = "en",
                TranscriberKind = TranscriberKind.Http,
            },
            new ApiProvider
            {
                Id = "elevenlabs",
                BiasMechanism = "elevenlabs_keyterms",
                Name = "ElevenLabs Scribe",
                BaseUrl = "https://api.elevenlabs.io",
                TranscriptionEndpoint = "https://api.elevenlabs.io/v1/speech-to-text",
                AuthHeaderName = "xi-api-key",
                ModelFieldName = "model_id",
                TranscriptionModel = "scribe_v2",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Http,
                // Clinical-dictation defaults: suppress (laughter)/(coughing)
                // tags and strip um/uh fillers from output.
                TagAudioEvents = false,
                NoVerbatim = true,
            },
            new ApiProvider
            {
                Id = "cohere-api",
                // Cohere Transcribe v2 has NO vocabulary-biasing field — terms
                // are silently dropped server-side. Don't pretend otherwise.
                BiasMechanism = "none",
                Name = "Cohere Transcribe API",
                BaseUrl = "https://api.cohere.com",
                TranscriptionEndpoint = "https://api.cohere.com/v2/audio/transcriptions",
                TranscriptionModel = "cohere-transcribe-03-2026",
                SupportsTranscription = true,
                // Cohere v2: model and language MUST appear before file in multipart body.
                // Temperature 0.1 → focused/deterministic output, good for medical dictation.
                TranscriptionTemperature = 0.1,
                ContextBiasMode = "cohere_terms",
                Language = "en",
                TranscriberKind = TranscriberKind.Http,
            },
            // ─── Local CrispASR-based providers (unified server path) ──────
            // All of these spawn `crispasr.exe --server` lazily on first
            // dictation via CrispAsrServerTranscriber. Port + model glob +
            // optional backend hint differ; everything else is identical.
            //
            // Cohere GGUF (legacy ports 8766–8768 preserved so users with
            // existing config.json files don't see surprises). New presets
            // use the 81xx band.
            new ApiProvider
            {
                Id = "cohere-gguf-server",
                // hotwords accepted but a no-op on the CrispASR cohere backend.
                BiasMechanism = "hotwords",
                Name = "Cohere Local (CrispASR server, CPU)",
                BaseUrl = "http://localhost:8766",
                TranscriptionModel = "cohere",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8766,
                LocalModelGlob = "cohere-transcribe-*.gguf",
                LocalBackendHint = "cohere",
                LocalGpuBackend = "cpu",
            },
            // The cuda/cuda-q8 server presets (ports 8767/8768) and the Q4
            // preset (8104) were removed from the defaults 2026-06-12:
            // cohere-local-q6k covers the GPU path (Q6_K ≈ F16 accuracy) and
            // cohere-gguf-server stays as the pinned-CPU fallback. The Ids
            // remain valid in KindForId/HealthProbe/ProviderDiagnostics so
            // configs that still carry them keep working.
            new ApiProvider
            {
                // Qwen3-ASR 1.7B — auto-spawned CrispASR server on port 8112.
                // Replaces the old `qwen3-asr` Http entry (port 8102), which
                // pointed at a server the user had to start by hand; this one
                // is spawned and owned by WhisperInk like every other local
                // preset. Ports 8103/8105-8109/8766 are taken by the presets
                // above, 8110 is the canary example in CLAUDE.md and 8111 was
                // the retired lfm2-audio trial, so 8112 is the first clean one.
                Id = "qwen3-asr-1.7b-local",
                // hotwords is REAL here and it is the only local preset where
                // that is true. The qwen3 backend splices the term list into
                // the decoder's ChatML prompt ("...may appear in the audio: ")
                // rather than into a CTC/TDT trie, so it steers a speech-LLM
                // instead of re-weighting lattice arcs. Measured 2026-08-29 on
                // the clinical clips in _scratch/biasing/clips: baseline missed
                // hematochezia twice (-> "hematuria" / "hematemesis") and
                // mangled ureterolithiasis (-> "bursitis with edema"); with the
                // terms supplied all three came back exactly right, 3/3
                // reproducible, and the controls were untouched. No boost knob
                // is set: unlike Parakeet's trie there is nothing to over-boost,
                // and a long list dilutes rather than garbles (a 40-term list
                // still fixed 5 of 6, softening only ureterolithiasis into
                // "ureteral lithiasis").
                BiasMechanism = "hotwords",
                Name = "Qwen3-ASR 1.7B Local (CrispASR)",
                BaseUrl = "http://localhost:8112",
                TranscriptionEndpoint = "http://localhost:8112/v1/audio/transcriptions",
                TranscriptionModel = "qwen3",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8112,
                // Pinned to the 1.7b family so a future qwen3-asr-0.6b GGUF
                // dropped in the same folder cannot hijack this preset (the
                // same trap the two Parakeet globs above are avoiding). Note
                // it would still match a qwen3-asr-1.7b-ja-anime GGUF, which
                // is the same architecture and would at least run correctly.
                LocalModelGlob = "qwen3-asr-1.7b-*.gguf",
                // Auto-detect resolves this GGUF to the plain `qwen3` backend
                // and transcribes identically, but the hint is pinned anyway:
                // it documents which of the two registered qwen3 backend names
                // this preset means, and survives a change in auto-detection.
                LocalBackendHint = "qwen3-1.7b",
                // Deliberately no LocalPuncModel. Qwen3-ASR is a speech-LLM and
                // emits punctuation and sentence case natively -- on jfk.wav it
                // produced a correct semicolon and capitalised "Americans"
                // where parakeet-rnnt needed --punc-model fullstop and still
                // returned "americans". Adding one would only re-punctuate
                // text that is already punctuated.
            },
            new ApiProvider
            {
                // Parakeet TDT 0.6b — auto-spawned CrispASR server on port 8103.
                // Glob is pinned to the TDT family on purpose: the RNNT 1.1b
                // preset below shares the cohere-gguf\ folder, and a bare
                // "parakeet-*.gguf" would match BOTH files (EnumerateFiles
                // returns "rnnt" before "tdt", so it would silently hijack this
                // preset). CrispASR auto-detects Parakeet — no backend hint.
                Id = "parakeet-local",
                // hotwords is Parakeet's native mechanism, but the boost knob is
                // left OFF by default: at a real boost the trie garbles neighboring
                // words, so it's opt-in (set HotwordsBoost in provider settings).
                BiasMechanism = "hotwords",
                Name = "Parakeet Local (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8103",
                TranscriptionEndpoint = "http://localhost:8103/v1/audio/transcriptions",
                TranscriptionModel = "parakeet",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8103,
                LocalModelGlob = "parakeet-tdt-*.gguf",
            },
            new ApiProvider
            {
                // Parakeet RNNT 1.1b — auto-spawned CrispASR server on port 8109.
                // Larger sibling of the TDT 0.6b preset above
                // (cstr/parakeet-rnnt-1.1b-GGUF, q4_k ≈770 MB). Real CTC/transducer
                // hotword biasing, same as TDT. CrispASR auto-detects the RNNT
                // backend from GGUF metadata — no hint. RNNT beam search is costly
                // like TDT's, so per-machine config.json pins LocalBeamSize=1 for a
                // greedy sub-second decode (null here = server-default beam-5).
                Id = "parakeet-rnnt-local",
                BiasMechanism = "hotwords",
                Name = "Parakeet RNNT 1.1b Local (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8109",
                TranscriptionEndpoint = "http://localhost:8109/v1/audio/transcriptions",
                TranscriptionModel = "parakeet",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8109,
                LocalModelGlob = "parakeet-rnnt-1.1b-*.gguf",
                // RNNT emits no punctuation; restore it server-side via FireRedPunc
                // (fullstop multilingual model). Needs the #161-punc CrispASR build.
                LocalPuncModel = "fullstop",
            },
            new ApiProvider
            {
                // Cohere Transcribe Q6_K — auto-spawned CrispASR server on
                // port 8105. Q6_K is mixed-precision K-quant, near-F16 accuracy
                // at essentially the same RTFx (~1.05× on 8 CPU threads).
                // Cohere GGUFs don't expose the backend marker CrispASR
                // auto-detect needs, so the explicit hint is required.
                Id = "cohere-local-q6k",
                // hotwords accepted but a no-op on the CrispASR cohere backend.
                BiasMechanism = "hotwords",
                Name = "Cohere Local Q6_K (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8105",
                TranscriptionEndpoint = "http://localhost:8105/v1/audio/transcriptions",
                TranscriptionModel = "cohere",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8105,
                LocalModelGlob = "cohere-transcribe-q6_k.gguf",
                LocalBackendHint = "cohere",
            },
            new ApiProvider
            {
                // Mistral Voxtral-Mini-3B speech-LLM. Bias terms ride the CrispASR
                // "hotwords" field, which the server splices into the decoder prompt
                // ("The following words may appear: ..."). The OpenAI "prompt" field
                // is NOT read by this backend.
                Id = "voxtral-local",
                BiasMechanism = "hotwords",
                Name = "Voxtral Local (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8106",
                TranscriptionEndpoint = "http://localhost:8106/v1/audio/transcriptions",
                TranscriptionModel = "voxtral",
                SupportsTranscription = true,
                ContextBiasMode = "whisper_prompt",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8106,
                LocalModelGlob = "voxtral-mini-3b*.gguf",
                LocalBackendHint = "voxtral",
            },
            new ApiProvider
            {
                // Mistral Voxtral-Mini-4B-Realtime. Upstream CrispASR treats
                // the 4B realtime checkpoint as a DIFFERENT backend than the
                // 3B ("voxtral4b" vs "voxtral") — hence a separate preset
                // rather than widening the 3B glob. This backend has no hotword/
                // prompt splice, so biasing is accepted but a no-op.
                Id = "voxtral4b-local",
                BiasMechanism = "hotwords",
                Name = "Voxtral 4B Realtime Local (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8108",
                TranscriptionEndpoint = "http://localhost:8108/v1/audio/transcriptions",
                TranscriptionModel = "voxtral4b",
                SupportsTranscription = true,
                ContextBiasMode = "whisper_prompt",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8108,
                LocalModelGlob = "voxtral-mini-4b*.gguf",
                LocalBackendHint = "voxtral4b",
            },
            new ApiProvider
            {
                // IBM Granite Speech 4.1 2B speech-LLM. Sent the "hotwords" field
                // like the others, but the granite backend has no biasing splice —
                // accepted and ignored (no-op).
                Id = "granite-local",
                BiasMechanism = "hotwords",
                Name = "Granite Speech 4.1 Local (CrispASR, auto-spawn)",
                BaseUrl = "http://localhost:8107",
                TranscriptionEndpoint = "http://localhost:8107/v1/audio/transcriptions",
                TranscriptionModel = "granite",
                SupportsTranscription = true,
                ContextBiasMode = "whisper_prompt",
                Language = "en",
                TranscriberKind = TranscriberKind.LocalCrispAsrServer,
                LocalServerPort = 8107,
                LocalModelGlob = "granite-speech-*.gguf",
                LocalBackendHint = "granite",
            },
            new ApiProvider
            {
                // Google Cloud Speech-to-Text v2 — Chirp 3 model. Cloud-only.
                // Bypasses the generic HTTP multipart path because Chirp 3 needs
                // OAuth bearer tokens (no API key auth), JSON body with base64
                // audio inline, and a nested response shape — all handled by
                // GoogleChirp3Transcriber.
                //
                // ApiKey field holds *either* the path to a downloaded service
                // account JSON key file *or* the raw JSON contents pasted in.
                // BaseUrl chooses the region (us or eu); change the host prefix
                // to "https://eu-speech.googleapis.com" for the EU multi-region.
                //
                // ContextBiasMode is "none" because biasing for this provider
                // doesn't flow through the generic switch — the transcriber
                // unconditionally maps the global ContextBiasTerms into
                // adaptation.phraseSets[].phrases[] on every request.
                Id = "google-chirp3",
                BiasMechanism = "phrase_sets",
                Name = "Google Chirp 3",
                BaseUrl = "https://us-speech.googleapis.com",
                TranscriptionModel = "chirp_3",
                SupportsTranscription = true,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.GoogleChirp3,
            },
            new ApiProvider
            {
                // Soniox Speech-to-Text — async REST API (api.soniox.com/v1).
                // Not OpenAI-compatible: each dictation is a multi-step job
                // (upload WAV → create transcription → poll status → fetch the
                // token array), so SonioxTranscriber handles it, not the generic
                // HTTP multipart path. ApiKey holds the Soniox API key (sent as
                // Authorization: Bearer). TranscriptionModel is the async model
                // id — user-editable so a Soniox model rename is a config edit,
                // not a recompile.
                //
                // ContextBiasMode stays "none" because biasing doesn't flow
                // through the generic switch — SonioxTranscriber maps the global
                // ContextBiasTerms into the v4 `context.terms` field directly
                // (real vocabulary steering), same pattern as Google Chirp 3.
                Id = "soniox",
                BiasMechanism = "context_terms",
                Name = "Soniox",
                BaseUrl = "https://api.soniox.com",
                TranscriptionModel = "stt-async-v5",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Soniox,
            },
            new ApiProvider
            {
                // Deepgram — pre-recorded Listen API (api.deepgram.com/v1/listen).
                // Not OpenAI-compatible: the audio is the raw request body, every
                // option is a URL query param, and auth is "Authorization: Token
                // <key>", so DeepgramTranscriber handles it, not the generic HTTP
                // multipart path. ApiKey holds the Deepgram API key.
                //
                // Nova-3 is Deepgram's flagship model; TranscriptionModel is
                // user-editable, so nova-3-medical / nova-2 / whisper-cloud are a
                // config edit, not a recompile.
                //
                // ContextBiasMode stays "none" because biasing doesn't flow through
                // the generic switch — DeepgramTranscriber maps the global
                // ContextBiasTerms onto Nova-3 keyterm-prompting `keyterm` params
                // (or legacy `keywords` on older models), same pattern as Soniox.
                Id = "deepgram",
                BiasMechanism = "deepgram_keyterm",
                Name = "Deepgram Nova-3",
                BaseUrl = "https://api.deepgram.com",
                TranscriptionModel = "nova-3",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Deepgram,
            },
            new ApiProvider
            {
                // Deepgram Nova-3 MEDICAL — separate preset (model nova-3-medical),
                // tuned for clinical dictation. Same Deepgram path as the general
                // entry above; differs only by model + medical-appropriate
                // calibration params carried in DeepgramExtraParams (merged into
                // the /v1/listen query string by DeepgramTranscriber). Keyterm
                // biasing routes the shared Context Bias list (clinical terms) to
                // Nova-3 keyterm prompting, same as the general entry.
                Id = "deepgram-medical",
                BiasMechanism = "deepgram_keyterm",
                Name = "Deepgram Nova-3 Medical",
                BaseUrl = "https://api.deepgram.com",
                TranscriptionModel = "nova-3-medical",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Deepgram,
                // DeepgramExtraParams intentionally EMPTY. A live A/B on real
                // clinical clips (hematochezia/ureterolithiasis/biliary-colic)
                // rejected every calibration knob for unattended dictation that
                // pastes verbatim into the EHR: dictation=true stripped the
                // terminal period + downcased "CT"->"Ct"; measurements risks ISMP
                // unit abbreviations (units->U, international units->IU); numerals
                // over-converts idiomatic number-words ("cranial nerve two"->"2");
                // filler_words is unsupported on nova-3-medical; paragraphs is
                // subsumed by the baked-in smart_format. smart_format=true (in
                // DeepgramTranscriber.BuildUrl) is the safe formatter. Any of these
                // is a pure config.json opt-in (add to this dict) — no recompile.
                // NB: the API reports arch="nova-3" for nova-3-medical (internal
                // name medical-nova-3); that's expected, not a fallback.
                DeepgramExtraParams = new(),
            },
            new ApiProvider
            {
                // Modulate Velma 2 — MULTILINGUAL batch (platform.modulate.ai).
                // Not OpenAI-compatible: the audio part is "upload_file", auth is
                // a bare X-API-Key header, and the model is chosen by ENDPOINT
                // PATH rather than a "model" form field — which is why all three
                // Modulate presets leave TranscriptionModel blank and differ only
                // by TranscriptionEndpoint. ModulateTranscriber handles them.
                //
                // This is the full-featured endpoint and the only one of the
                // three with custom vocabulary. ContextBiasMode stays "none"
                // because biasing doesn't flow through the generic switch —
                // ModulateTranscriber packs the shared ContextBiasTerms into the
                // JSON `config` field's custom_terms array (real vocabulary
                // steering), same pattern as Soniox/Deepgram.
                Id = "modulate",
                BiasMechanism = "modulate_custom_terms",
                Name = "Modulate Velma 2 (Multilingual)",
                BaseUrl = ModulateTranscriber.DefaultBaseUrl,
                TranscriptionEndpoint = ModulateTranscriber.DefaultBaseUrl + ModulateTranscriber.PathMultilingual,
                TranscriptionModel = "",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Modulate,
            },
            new ApiProvider
            {
                // Modulate Velma 2 — ENGLISH FAST batch. The lowest-latency
                // Modulate model, and the one to reach for when dictation speed
                // matters more than vocabulary control: English-only, no custom
                // vocabulary, and it accepts no `language` parameter at all.
                // Transcripts come back auto-capitalized and auto-punctuated.
                Id = "modulate-english-fast",
                BiasMechanism = "none",
                Name = "Modulate Velma 2 English Fast",
                BaseUrl = ModulateTranscriber.DefaultBaseUrl,
                TranscriptionEndpoint = ModulateTranscriber.DefaultBaseUrl + ModulateTranscriber.PathEnglishFast,
                TranscriptionModel = "",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Modulate,
            },
            new ApiProvider
            {
                // Modulate Velma 2 — MULTILINGUAL FAST batch. Any supported
                // language with no metadata and no custom vocabulary; faster than
                // the full Multilingual endpoint. Declaring Language takes the
                // fastest direct path (blank or "auto" lets Modulate detect).
                Id = "modulate-multilingual-fast",
                BiasMechanism = "none",
                Name = "Modulate Velma 2 Multilingual Fast",
                BaseUrl = ModulateTranscriber.DefaultBaseUrl,
                TranscriptionEndpoint = ModulateTranscriber.DefaultBaseUrl + ModulateTranscriber.PathMultilingualFast,
                TranscriptionModel = "",
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Modulate,
            },
            new ApiProvider
            {
                // Smallest.ai Waves — PULSE PRO (api.smallest.ai/waves/v1/stt/).
                // Not OpenAI-compatible: the audio is the raw request body, all
                // options are query params, and the transcript is a top-level
                // "transcription". SmallestTranscriber handles both presets.
                //
                // English-only and the steadier of the two models — measured
                // 1.37-1.69 s wall-clock on the six clinical clips with terminal
                // punctuation on 6/6, where Pulse ranged 0.85-2.40 s and dropped
                // the period twice.
                //
                // Language MUST resolve to "en": the API's enum for this model is
                // exactly ["en"] and anything else (including WhisperInk's "auto")
                // returns 400. SmallestTranscriber.ResolveLanguage enforces that,
                // so this value is belt-and-braces rather than the only guard.
                //
                // BiasMechanism is a true "none" — the Waves STT API has no
                // keyterm/hotword/custom-vocabulary field at all, so the shared
                // ContextBiasTerms list cannot be routed anywhere and a
                // mis-recognized term cannot be corrected on this provider.
                Id = "smallest-pulse-pro",
                BiasMechanism = "none",
                Name = "Smallest.ai Pulse Pro (English)",
                BaseUrl = SmallestTranscriber.DefaultBaseUrl,
                TranscriptionEndpoint = SmallestTranscriber.DefaultBaseUrl + SmallestTranscriber.PathTranscribe,
                TranscriptionModel = SmallestTranscriber.ModelPulsePro,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Smallest,
            },
            new ApiProvider
            {
                // Smallest.ai Waves — PULSE (multilingual). Same endpoint as
                // Pulse Pro; the model is a real `model` query param, so these
                // two presets differ only by TranscriptionModel.
                //
                // Accepts 46 language codes (the published reference lists 26).
                // Language must be set to SOMETHING: leaving it blank makes the
                // server fall back to its own "multi" default, which is region-
                // gated on this account and 400s. "auto" is safe — SmallestTranscriber
                // maps it to the "multi-eu" aggregator, which is verified enabled.
                Id = "smallest-pulse",
                BiasMechanism = "none",
                Name = "Smallest.ai Pulse (Multilingual)",
                BaseUrl = SmallestTranscriber.DefaultBaseUrl,
                TranscriptionEndpoint = SmallestTranscriber.DefaultBaseUrl + SmallestTranscriber.PathTranscribe,
                TranscriptionModel = SmallestTranscriber.ModelPulse,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Smallest,
            },
            new ApiProvider
            {
                // Reson8 — prerecorded STT (api.reson8.dev/v1/speech-to-text/
                // prerecorded). Not OpenAI-compatible: the audio is the raw
                // request body, every option is a query param, auth is
                // "Authorization: ApiKey <key>" (a third scheme, alongside
                // Deepgram's "Token"), and errors are RFC 7807 problem+json.
                // Reson8Transcriber handles it.
                //
                // ONE preset, because there is one endpoint and NO `model`
                // param — TranscriptionModel is unused here and stays blank.
                // The customization axis is custom_model_id (a persistent
                // vocabulary built in the Reson8 console), set via
                // Reson8ExtraParams.
                //
                // ContextBiasMode stays "none" because biasing doesn't flow
                // through the generic switch — Reson8Transcriber maps the shared
                // ContextBiasTerms onto the `phrases` query param (up to 250
                // terms), same native-routing pattern as Soniox/Deepgram.
                //
                // Language = "en" is deliberate rather than inherited. Reson8
                // supports exactly ten languages and requests auto-detection by
                // OMITTING the param (there is no "auto" value), but upstream
                // warns detection is "less reliable for short utterances" —
                // which is all a push-to-talk dictation tool ever sends. Note
                // WhisperInk's language dropdown offers ja/ko/zh/ru/ar/hi, none
                // of which Reson8 supports; Reson8Transcriber.ResolveLanguage
                // drops those to auto-detect rather than letting them 400 away
                // every dictation.
                Id = "reson8",
                BiasMechanism = "reson8_phrases",
                Name = "Reson8",
                BaseUrl = Reson8Transcriber.DefaultBaseUrl,
                TranscriptionEndpoint = Reson8Transcriber.DefaultBaseUrl + Reson8Transcriber.PathTranscribe,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                TranscriberKind = TranscriberKind.Reson8,
                // Empty by default: filler_mode "natural" is the API default and
                // "clean" would silently rewrite transcripts, so it stays a
                // deliberate opt-in. Add custom_model_id here once a custom
                // vocabulary exists in the console — config.json only, no
                // recompile.
                Reson8ExtraParams = new(),
            }
        };

        /// <summary>
        /// Fallback that infers <see cref="TranscriberKind"/> from the legacy
        /// provider id when loading an older config.json that predates the
        /// explicit field. Unknown ids default to <see cref="TranscriberKind.Http"/>,
        /// which is also correct for user-added cloud providers.
        /// </summary>
        public static TranscriberKind InferKindFromLegacyId(string id) => id switch
        {
            "cohere-gguf"                                                  => TranscriberKind.LocalCrispAsrServer,
            "cohere-gguf-server"                                           => TranscriberKind.LocalCrispAsrServer,
            "cohere-gguf-cuda-server"                                      => TranscriberKind.LocalCrispAsrServer,
            "cohere-gguf-cuda-server-q8"                                   => TranscriberKind.LocalCrispAsrServer,
            "parakeet-local" or "parakeet-rnnt-local" or "cohere-local-q4" or "cohere-local-q6k"
                or "voxtral-local" or "voxtral4b-local" or "granite-local"
                or "qwen3-asr-1.7b-local"                                  => TranscriberKind.LocalCrispAsrServer,
            "google-chirp3"                                                => TranscriberKind.GoogleChirp3,
            "soniox"                                                       => TranscriberKind.Soniox,
            "deepgram" or "deepgram-medical"                               => TranscriberKind.Deepgram,
            "modulate" or "modulate-english-fast"
                or "modulate-multilingual-fast"                            => TranscriberKind.Modulate,
            "smallest-pulse-pro" or "smallest-pulse"                       => TranscriberKind.Smallest,
            "reson8"                                                       => TranscriberKind.Reson8,
            _                                                              => TranscriberKind.Http,
        };
    }

    public class AppConfig
    {
        public string MistralApiKey { get; set; } = "";
        public bool IsSoundEnabled { get; set; } = true;
        public int SelectedDevice { get; set; } = 0;

        // Context biasing terms — the single shared vocabulary list. Each provider
        // routes these to its own native field via ApiProvider.BiasMechanism /
        // ResolvedBiasMechanism (prompt / context_bias / keyterms / hotwords /
        // phrase sets / context terms); see the BiasMechanism doc on ApiProvider.
        public List<string> ContextBiasTerms { get; set; } = new();

        // ── API Provider configuration ──────────────────────────────────
        public List<ApiProvider> Providers { get; set; } = new();
        public string ActiveProviderId { get; set; } = "mistral";

        // ── Local GPU backend for CrispASR-based providers ──────────────
        // Controls the --gpu-backend flag passed to crispasr.exe when spawning a
        // local server. Per-provider LocalGpuBackend overrides this; blank
        // there falls through to the global default below.
        // Valid values (case-insensitive): "auto", "vulkan", "cuda", "metal", "cpu".
        // On the 5825U APU, Vulkan and CPU are within ~10% of each other on Q4_K server mode.
        public string CrispGpuBackend { get; set; } = "auto";

        // ── UX / window behaviour ───────────────────────────────────────
        // When true the main window's close triggers a full process
        // exit; when false (default) it hides to the tray and WhisperInk
        // keeps running in the background. Matches the typical Windows
        // tray-app expectation.
        public bool QuitOnClose { get; set; } = false;

        // When true, an HKCU\...\Run entry is kept up to date so
        // WhisperInk launches at Windows sign-in. Synced from the
        // registry at startup so external toggles (Task Manager →
        // Startup apps) stay coherent.
        public bool LaunchAtStartup { get; set; } = false;

        // Tracks whether the first-run onboarding banner/balloon has
        // been shown once already. Flipped to true after the first
        // successful load with a valid config present.
        public bool HasSeenFirstRun { get; set; } = false;
    }
}
