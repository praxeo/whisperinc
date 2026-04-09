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
                ContextBiasMode = "none"
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
                ContextBiasMode = "whisper_prompt"
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
                ContextBiasMode = "none"
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
                ContextBiasMode = "cohere_terms"
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
                ContextBiasMode = "whisper_prompt"
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
                ContextBiasMode = "none"
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
    }
}
