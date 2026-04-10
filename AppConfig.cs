using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperInk
{
    public class RawParameterOverride
    {
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string ValueTypeHint { get; set; } = "string"; // string | number | bool | json
        public bool Enabled { get; set; } = true;

        public override string ToString() => Enabled ? $"{Key}={Value}" : $"{Key}=<disabled>";
    }

    public class TranscriptionModelProfile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string DisplayName { get; set; } = "Default";
        public string ModelId { get; set; } = "";

        // Common typed parameters
        public bool SendLanguage { get; set; } = true;
        public string Language { get; set; } = "en";
        public double? Temperature { get; set; } = null;

        // "inherit" | "none" | "whisper_prompt" | "cohere_terms"
        public string ContextBiasMode { get; set; } = "inherit";
        public string Prompt { get; set; } = "";
        public string ContextBiasTerms { get; set; } = ""; // newline or comma delimited

        // UI helper text shown in settings to explain best-practice usage.
        public string Hints { get; set; } = "";
        public bool Enabled { get; set; } = true;

        // Advanced raw multipart key/value overrides. Keys here can override typed fields.
        public List<RawParameterOverride> RawOverrides { get; set; } = new();

        public override string ToString() =>
            string.IsNullOrWhiteSpace(ModelId)
                ? DisplayName
                : $"{DisplayName} ({ModelId})";
    }

    public static class TranscriptionParameterCatalog
    {
        public static string BuildHints(string providerId, string? modelId)
        {
            providerId = (providerId ?? "").Trim().ToLowerInvariant();
            modelId = (modelId ?? "").Trim();
            string m = modelId.ToLowerInvariant();

            return providerId switch
            {
                "openai" when m.Contains("gpt-4o-transcribe") =>
                    "OpenAI GPT-4o Transcribe profile. Keep language='en' when dictating English. Use prompt for style/term priming. Temperature near 0 is most deterministic.",

                "openai" when m.Contains("gpt-4o-mini-transcribe") =>
                    "OpenAI GPT-4o Mini Transcribe profile. Lower latency, generally robust for quick dictation. Prompt can bias terminology.",

                "openai" when m.Contains("whisper-1") =>
                    "OpenAI Whisper-1 profile. Prompt is the main vocabulary-bias tool. Keep prompt concise and domain-specific.",

                "mistral" =>
                    "Mistral Voxtral batch profile. Prefer language='en'. Keep temperature null unless explicitly needed. Realtime mode settings are configured separately.",

                "cohere-api" =>
                    "Cohere v2 Transcribe profile. context_bias_terms accepts JSON array semantics; keep terms concise. Temperature 0.1 is a good deterministic baseline.",

                "elevenlabs" =>
                    "ElevenLabs Scribe profile. Uses custom auth header and model_id field. Avoid unsupported extras unless tested via raw overrides.",

                "local" =>
                    "Local OpenAI-compatible server profile. Prompt-based context biasing usually works for Whisper-compatible endpoints.",

                "cohere-onnx" =>
                    "Local ONNX profile. HTTP multipart parameters are ignored in this mode. Model behavior is controlled by local ONNX runtime.",

                _ => "Set model-specific parameters here. Use Raw Overrides for experimental fields."
            };
        }

        public static List<TranscriptionModelProfile> CreateDefaultProfiles(string providerId, string? fallbackModel)
        {
            providerId = (providerId ?? "").Trim().ToLowerInvariant();

            static TranscriptionModelProfile P(string name, string model, double? temp, string biasMode, bool sendLang, string lang, string prompt, string biasTerms, string hints)
                => new()
                {
                    DisplayName = name,
                    ModelId = model,
                    Temperature = temp,
                    ContextBiasMode = biasMode,
                    SendLanguage = sendLang,
                    Language = lang,
                    Prompt = prompt,
                    ContextBiasTerms = biasTerms,
                    Hints = hints,
                    Enabled = true
                };

            if (providerId == "openai")
            {
                var profiles = new List<TranscriptionModelProfile>
                {
                    P("4o Transcribe", "gpt-4o-transcribe", 0.0, "whisper_prompt", true, "en", "", "", BuildHints("openai", "gpt-4o-transcribe")),
                    P("4o Mini Transcribe", "gpt-4o-mini-transcribe", 0.0, "whisper_prompt", true, "en", "", "", BuildHints("openai", "gpt-4o-mini-transcribe")),
                    P("Whisper-1", "whisper-1", 0.0, "whisper_prompt", true, "en", "", "", BuildHints("openai", "whisper-1"))
                };
                return profiles;
            }

            if (providerId == "mistral")
            {
                return new List<TranscriptionModelProfile>
                {
                    P("Voxtral Mini", string.IsNullOrWhiteSpace(fallbackModel) ? "voxtral-mini-latest" : fallbackModel, null, "none", true, "en", "", "", BuildHints("mistral", fallbackModel))
                };
            }

            if (providerId == "cohere-api")
            {
                return new List<TranscriptionModelProfile>
                {
                    P("Cohere Transcribe", string.IsNullOrWhiteSpace(fallbackModel) ? "cohere-transcribe-03-2026" : fallbackModel, 0.1, "cohere_terms", true, "en", "", "", BuildHints("cohere-api", fallbackModel))
                };
            }

            if (providerId == "elevenlabs")
            {
                return new List<TranscriptionModelProfile>
                {
                    P("Scribe v2", string.IsNullOrWhiteSpace(fallbackModel) ? "scribe_v2" : fallbackModel, null, "none", false, "", "", "", BuildHints("elevenlabs", fallbackModel))
                };
            }

            if (providerId == "local")
            {
                return new List<TranscriptionModelProfile>
                {
                    P("Local Whisper-Compatible", fallbackModel ?? "", null, "whisper_prompt", true, "en", "", "", BuildHints("local", fallbackModel))
                };
            }

            if (providerId == "cohere-onnx")
            {
                return new List<TranscriptionModelProfile>
                {
                    P("Cohere ONNX", "", null, "none", false, "", "", "", BuildHints("cohere-onnx", fallbackModel))
                };
            }

            return new List<TranscriptionModelProfile>
            {
                P("Default", fallbackModel ?? "", null, "inherit", true, "en", "", "", BuildHints(providerId, fallbackModel))
            };
        }

        public static TranscriptionModelProfile CreateFromLegacy(ApiProvider provider, IEnumerable<string>? legacyTerms)
        {
            var terms = legacyTerms == null
                ? ""
                : string.Join(", ", legacyTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));

            return new TranscriptionModelProfile
            {
                DisplayName = "Legacy Default",
                ModelId = provider.TranscriptionModel,
                Temperature = provider.TranscriptionTemperature,
                ContextBiasMode = provider.ContextBiasMode,
                SendLanguage = string.IsNullOrWhiteSpace(provider.AuthHeaderName),
                Language = string.IsNullOrWhiteSpace(provider.AuthHeaderName) ? "en" : "",
                Prompt = "",
                ContextBiasTerms = terms,
                Hints = BuildHints(provider.Id, provider.TranscriptionModel),
                Enabled = true
            };
        }
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

        // ── Per-model transcription profiles ─────────────────────────────
        public List<TranscriptionModelProfile> TranscriptionProfiles { get; set; } = new();
        public string ActiveTranscriptionProfileId { get; set; } = "";

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

        public TranscriptionModelProfile? GetActiveTranscriptionProfile()
        {
            if (TranscriptionProfiles == null || TranscriptionProfiles.Count == 0)
                return null;

            var active = TranscriptionProfiles.FirstOrDefault(p => p.Id == ActiveTranscriptionProfileId && p.Enabled);
            if (active != null) return active;

            active = TranscriptionProfiles.FirstOrDefault(p => p.Enabled);
            if (active != null)
            {
                ActiveTranscriptionProfileId = active.Id;
                return active;
            }

            ActiveTranscriptionProfileId = TranscriptionProfiles[0].Id;
            return TranscriptionProfiles[0];
        }

        public void EnsureTranscriptionProfiles(IEnumerable<string>? legacyTerms = null)
        {
            if (TranscriptionProfiles == null || TranscriptionProfiles.Count == 0)
            {
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles(Id, TranscriptionModel);
                if (TranscriptionProfiles.Count == 0)
                    TranscriptionProfiles.Add(TranscriptionParameterCatalog.CreateFromLegacy(this, legacyTerms));

                if (TranscriptionProfiles.Count > 0)
                    ActiveTranscriptionProfileId = TranscriptionProfiles[0].Id;
            }

            if (string.IsNullOrWhiteSpace(ActiveTranscriptionProfileId) || !TranscriptionProfiles.Any(p => p.Id == ActiveTranscriptionProfileId))
                ActiveTranscriptionProfileId = TranscriptionProfiles[0].Id;

            // Backfill missing hints for old profiles
            foreach (var profile in TranscriptionProfiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Hints))
                    profile.Hints = TranscriptionParameterCatalog.BuildHints(Id, profile.ModelId);
            }
        }

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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("mistral", "voxtral-mini-latest")
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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("openai", "whisper-1")
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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("elevenlabs", "scribe_v2")
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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("cohere-api", "cohere-transcribe-03-2026")
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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("local", "")
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
                TranscriptionProfiles = TranscriptionParameterCatalog.CreateDefaultProfiles("cohere-onnx", "")
            }
        };

        public static void NormalizeDefaults(List<ApiProvider> providers)
        {
            foreach (var p in providers)
            {
                p.EnsureTranscriptionProfiles();
                if (string.IsNullOrWhiteSpace(p.ActiveTranscriptionProfileId) && p.TranscriptionProfiles.Count > 0)
                    p.ActiveTranscriptionProfileId = p.TranscriptionProfiles[0].Id;
            }
        }
    }

    public class AppConfig
    {
        public int ConfigSchemaVersion { get; set; } = 2;
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
