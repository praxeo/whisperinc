using System;
using System.Collections.Generic;

namespace WhisperInk
{
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

        // Model identifiers — leave blank to use the endpoint's default
        public string TranscriptionModel { get; set; } = "";
        public string ChatModel { get; set; } = "";
        public string PostProcessModel { get; set; } = "";

        // ── Language ─────────────────────────────────────────────────────────
        // Language code for transcription (e.g., "en", "es", "fr").
        // Explicit language setting improves accuracy by constraining the model's search space.
        // Defaults to "en" if not set.
        public string Language { get; set; } = "en";

        // Whether this provider supports the realtime WebSocket protocol (Mistral-specific)
        public bool SupportsRealtime { get; set; } = false;

        // Whether batch transcription is available
        public bool SupportsTranscription { get; set; } = true;

        // ── Temperature ─────────────────────────────────────────────────
        // Optional temperature for transcription (null = use endpoint default).
        // Cohere recommends 0.1 for deterministic medical output.
        // OpenAI Whisper accepts 0.0 (fully deterministic).
        // Send as a multipart form field BEFORE the file part.
        public double? TranscriptionTemperature { get; set; } = null;

        // ── Context biasing mode ─────────────────────────────────────────
        // Controls how _contextBiasTerms are sent to the transcription endpoint:
        //
        //   "none"          — don't send any bias field (Mistral, ElevenLabs)
        //   "whisper_prompt"— join terms as a comma-delimited string sent as "prompt"
        //                     (OpenAI Whisper, Groq, DeepInfra, local Whisper-based servers)
        //   "cohere_terms"  — send terms as a JSON array in "context_bias_terms"
        //                     (Cohere v2 cloud API only; NOT used for local ONNX)
        //
        // "whisper_prompt" seeds the Whisper decoder vocabulary so it expects those words.
        // "cohere_terms" instructs Cohere's model to treat those strings as high-priority.
        public string ContextBiasMode { get; set; } = "none";

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

        public List<string> GetValidatedKeyterms(out List<string> warnings)
        {
            warnings = new List<string>();
            var terms = (ScribeKeytermsRaw ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
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

        public static List<ApiProvider> CreateDefaults() => new()
        {
            new ApiProvider
            {
                Id = "mistral",
                Name = "Mistral",
                BaseUrl = "https://api.mistral.ai",
                TranscriptionModel = "voxtral-mini-latest",
                ChatModel = "mistral-medium-latest",
                PostProcessModel = "mistral-medium-latest",
                SupportsRealtime = true,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
            {
                Id = "openai",
                Name = "OpenAI",
                BaseUrl = "https://api.openai.com",
                TranscriptionModel = "whisper-1",
                ChatModel = "gpt-4o-mini",
                PostProcessModel = "gpt-4o-mini",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = 0.0,
                ContextBiasMode = "whisper_prompt",
                Language = "en"
            },
            new ApiProvider
            {
                Id = "elevenlabs",
                Name = "ElevenLabs Scribe",
                BaseUrl = "https://api.elevenlabs.io",
                TranscriptionEndpoint = "https://api.elevenlabs.io/v1/speech-to-text",
                AuthHeaderName = "xi-api-key",
                ModelFieldName = "model_id",
                TranscriptionModel = "scribe_v2",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en",
                // Clinical-dictation defaults: suppress (laughter)/(coughing)
                // tags and strip um/uh fillers from output.
                TagAudioEvents = false,
                NoVerbatim = true
            },
            new ApiProvider
            {
                Id = "cohere-api",
                Name = "Cohere Transcribe API",
                BaseUrl = "https://api.cohere.com",
                TranscriptionEndpoint = "https://api.cohere.com/v2/audio/transcriptions",
                TranscriptionModel = "cohere-transcribe-03-2026",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                // Cohere v2: model and language MUST appear before file in multipart body.
                // Temperature 0.1 → focused/deterministic output, good for medical dictation.
                TranscriptionTemperature = 0.1,
                ContextBiasMode = "cohere_terms",
                Language = "en"
            },
            new ApiProvider
            {
                Id = "local",
                Name = "Local Server",
                BaseUrl = "http://localhost:8100",
                TranscriptionModel = "",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                // Whisper-based local servers accept the OpenAI `prompt` field for vocabulary seeding.
                ContextBiasMode = "whisper_prompt",
                Language = "en"
            },
            new ApiProvider
            {
                Id = "cohere-onnx",
                Name = "Cohere Local (ONNX)",
                BaseUrl = "local://cohere-onnx",
                TranscriptionModel = "",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                // Local ONNX inference — no HTTP multipart, so bias/temp fields are irrelevant here.
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
	    {
    	  	Id = "cohere-gguf",
    		Name = "Cohere Local (CrispASR GGUF)",
    		BaseUrl = "local://cohere-gguf",
    		TranscriptionModel = "",
   		ChatModel = "",
    		PostProcessModel = "",
    		SupportsRealtime = false,
    		SupportsTranscription = true,
    		TranscriptionTemperature = null,
    		// Local CrispASR subprocess — no HTTP multipart, so bias/temp fields are irrelevant 			here.
    		ContextBiasMode = "none",
    		Language = "en"
	    },
	    new ApiProvider
	    {
    		Id = "cohere-gguf-server",
    		Name = "Cohere Local (CrispASR server)",
   		 BaseUrl = "local://cohere-gguf-server",
   		 TranscriptionModel = "",
    		ChatModel = "",
    		PostProcessModel = "",
    		SupportsRealtime = false,
    		SupportsTranscription = true,
    		TranscriptionTemperature = null,
    		ContextBiasMode = "none",
    		Language = "en"
	    },
	    new ApiProvider
	    {
    		Id = "cohere-gguf-cuda-server",
    		Name = "Cohere Local (CrispASR CUDA)",
    		BaseUrl = "local://cohere-gguf-cuda-server",
    		TranscriptionModel = "",
    		ChatModel = "",
    		PostProcessModel = "",
    		SupportsRealtime = false,
    		SupportsTranscription = true,
    		TranscriptionTemperature = null,
    		ContextBiasMode = "none",
    		Language = "en"
	    },
	    new ApiProvider
	    {
    		Id = "cohere-gguf-cuda-server-q8",
    		Name = "Cohere Local (CrispASR CUDA Q8)",
    		BaseUrl = "local://cohere-gguf-cuda-server-q8",
    		TranscriptionModel = "",
    		ChatModel = "",
    		PostProcessModel = "",
    		SupportsRealtime = false,
    		SupportsTranscription = true,
    		TranscriptionTemperature = null,
    		ContextBiasMode = "cohere_terms",
    		Language = "en"
	    },
	    new ApiProvider
            {
                Id = "qwen3-asr",
                Name = "Qwen3-ASR Local",
                BaseUrl = "http://localhost:8102",
                TranscriptionModel = "",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                // Qwen3-ASR accepts language via form field; no context bias support.
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
            {
                // CrispASR --server speaks the OpenAI /v1/audio/transcriptions protocol,
                // so no dedicated transcriber class is needed — the generic HTTP path
                // handles it the same way it handles qwen3-asr.
                // User workflow: run `crispasr.exe --server -m parakeet.gguf --port 8103`
                // externally, then activate this provider.
                Id = "parakeet-local",
                Name = "Parakeet Local (CrispASR)",
                BaseUrl = "http://localhost:8103",
                TranscriptionEndpoint = "http://localhost:8103/v1/audio/transcriptions",
                TranscriptionModel = "parakeet",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
            {
                // Cohere Transcribe Q4_K CPU — auto-spawned CrispASR server.
                // Mirrors the Parakeet preset but uses port 8104 and the
                // cohere-transcribe-q4_k.gguf file. The dispatch in
                // MainWindow.xaml.cs passes backendHint: "cohere" because
                // Cohere GGUFs may not expose the backend marker that
                // CrispASR's auto-detect relies on.
                Id = "cohere-local-q4",
                Name = "Cohere Local Q4 (CrispASR)",
                BaseUrl = "http://localhost:8104",
                TranscriptionEndpoint = "http://localhost:8104/v1/audio/transcriptions",
                TranscriptionModel = "cohere",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
            {
                // Cohere Transcribe Q6_K CPU — auto-spawned CrispASR server.
                // Same pattern as Q4 but points at port 8105 and the
                // cohere-transcribe-q6_k.gguf file. Q6_K is K-quant mixed
                // precision, noticeably closer to F16 accuracy than Q4_K
                // at essentially the same RTFx (~1.05× on 8 CPU threads).
                // Accuracy-first pick; use Q4 only if disk footprint matters.
                Id = "cohere-local-q6k",
                Name = "Cohere Local Q6_K (CrispASR)",
                BaseUrl = "http://localhost:8105",
                TranscriptionEndpoint = "http://localhost:8105/v1/audio/transcriptions",
                TranscriptionModel = "cohere",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "none",
                Language = "en"
            },
            new ApiProvider
            {
                // Mistral Voxtral-Mini-3B local via CrispASR server (batch mode).
                // Voxtral is a speech-LLM: Whisper encoder + Mistral 3B LLM with
                // audio-token injection, which means free-form prompt conditioning
                // is native to the architecture — context bias terms flow via the
                // OpenAI "prompt" field (whisper_prompt mode).
                // Needs explicit --backend voxtral; auto-detect does not cover it.
                // Place the GGUF in %APPDATA%\.WhisperInk\cohere-gguf\ —
                // e.g. voxtral-mini-3b-2507-q4_k.gguf from cstr/voxtral-mini-3b-2507-GGUF.
                Id = "voxtral-local",
                Name = "Voxtral Local (CrispASR)",
                BaseUrl = "http://localhost:8106",
                TranscriptionEndpoint = "http://localhost:8106/v1/audio/transcriptions",
                TranscriptionModel = "voxtral",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "whisper_prompt",
                Language = "en"
            },
            new ApiProvider
            {
                // IBM Granite Speech 4.1 2B local via CrispASR server.
                // Granite Speech is a speech-LLM (Granite 3B LLM + audio encoder
                // + projector). Like Voxtral, prompt-style conditioning is native;
                // ContextBiasMode = whisper_prompt sends bias terms via the OpenAI
                // `prompt` field. Backend hint matches granite_speech.dll.
                // Place granite-speech-*.gguf in %APPDATA%\.WhisperInk\cohere-gguf\.
                Id = "granite-local",
                Name = "Granite Speech 4.1 Local (CrispASR)",
                BaseUrl = "http://localhost:8107",
                TranscriptionEndpoint = "http://localhost:8107/v1/audio/transcriptions",
                TranscriptionModel = "granite",
                ChatModel = "",
                PostProcessModel = "",
                SupportsRealtime = false,
                SupportsTranscription = true,
                TranscriptionTemperature = null,
                ContextBiasMode = "whisper_prompt",
                Language = "en"
            }
        };
    }

    public class AppConfig
    {
        public string MistralApiKey { get; set; } = "";
        public bool IsSoundEnabled { get; set; } = true;
        public string SystemPrompt { get; set; } = "You are a precise execution engine. The user will give you text and a voice instruction. Follow the instruction exactly. Return only the result — no commentary, no markdown, no explanation.";
        public int SelectedDevice { get; set; } = 0;

        // Mistral Realtime API parameter: balances latency vs accuracy
        public int TargetStreamingDelayMs { get; set; } = 480;

        // "Realtime" = live streaming via WebSocket, "Batch" = record-then-transcribe
        public string DictationMode { get; set; } = "Realtime";

        // Path to the Mistral realtime proxy script (Python)
        public string ProxyPath { get; set; } = "";

        // Context biasing terms for batch transcription (up to 100 words/phrases).
        // Delivery method is controlled per-provider via ApiProvider.ContextBiasMode:
        //   "whisper_prompt"  → joined as comma-delimited string in the "prompt" field
        //   "cohere_terms"    → sent as JSON array in "context_bias_terms" field
        //   "none"            → not sent
        public List<string> ContextBiasTerms { get; set; } = new();

        // When true, batch transcription results are passed through a fast LLM correction pass
        public bool PostProcessBatch { get; set; } = false;

        // Prompt used for the post-processing correction pass
        public string PostProcessPrompt { get; set; } = "This is emergency department clinical documentation. All dictation is for patient chart notes. You correct speech recognition errors in medical dictation. You receive text after INPUT: and produce corrected text after OUTPUT: with no other text. Never use markdown, asterisks, or any formatting. Only fix words that are clearly garbled or nonsensical — do NOT replace real English words with medical terms. If a word makes sense as-is, leave it alone.\n\nINPUT:\nThe patient has a pear a cardial a fusion.\nOUTPUT:\nThe patient has a pericardial effusion.\n\nINPUT:\nWe will start him on nora epinephrine.\nOUTPUT:\nWe will start him on norepinephrine.\n\nINPUT:\nChest x-ray shows no abnormalities.\nOUTPUT:\nChest x-ray shows no abnormalities.\n\nINPUT:\nI like the Mistral API.\nOUTPUT:\nI like the Mistral API.";

        // ── API Provider configuration ──────────────────────────────────
        public List<ApiProvider> Providers { get; set; } = new();
        public string ActiveProviderId { get; set; } = "mistral";

        // ── Local GPU backend for CrispASR-based providers ──────────────
        // Controls the --gpu-backend flag passed to crispasr.exe when spawning a
        // local server for the Cohere Local Q4 / Q6_K / Parakeet providers.
        // Valid values (case-insensitive): "auto", "vulkan", "cuda", "metal", "cpu".
        // Default "auto" lets crispasr pick the best compiled backend.
        // Set "cpu" to disable GPU entirely (uses -ng flag), or "vulkan" to force Vulkan.
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
