// ModulateTranscriber.cs
// Cloud transcription via Modulate's Velma 2 batch speech-to-text endpoints
// (https://platform.modulate.ai/api/velma-2-stt-batch*).
//
// Modulate is NOT OpenAI-compatible, so it bypasses HttpTranscriber:
//   - the audio part is named "upload_file", not "file";
//   - the MODEL is chosen by ENDPOINT PATH, not a "model" form field — the
//     three batch models are three different URLs;
//   - auth is a bare "X-API-Key: <key>" header (not Bearer, not xi-api-key);
//   - custom vocabulary travels only inside a JSON-encoded "config" form
//     field — there is no top-level custom_terms field.
//
// One synchronous POST per dictation, like Deepgram — no job/poll cycle as
// with Soniox. All three batch endpoints answer with a top-level "text", so a
// single parse covers every variant.
//
// ── The three batch models (one preset each; see AppConfig.CreateDefaults) ──
//
//   Multilingual      /api/velma-2-stt-batch
//                     Full-featured: custom vocabulary, per-utterance
//                     language detection, and the enrichment signals.
//   English Fast      /api/velma-2-stt-batch-english-vfast
//                     English-only, tuned for latency. No biasing, and it
//                     accepts no language parameter at all.
//   Multilingual Fast /api/velma-2-stt-batch-multilingual-vfast
//                     Any supported language, no metadata, no biasing.
//
// Which variant a provider is gets derived from its TranscriptionEndpoint, so
// each request sends only the fields its endpoint actually documents. An
// unrecognised URL falls back to Multilingual — the endpoint whose parameter
// set is a superset of the other two.
//
// ── Signals are deliberately all left off ────────────────────────────────
// speaker_diarization, emotion_signal, accent_signal, deepfake_signal and
// pii_phi_tagging are built for call analytics, not dictation. WhisperInk
// reads only "text", so every one of them is pure added latency here — and
// pii_phi_tagging is actively harmful, because it wraps sensitive spans in
// entity TAGS inside the transcript text, which would then paste as markup
// into whatever the user is dictating into. Note that speaker_diarization
// defaults to TRUE on the Multilingual endpoint, so this transcriber sends
// "false" explicitly rather than relying on the default, the same way
// HttpTranscriber must send tag_audio_events explicitly for ElevenLabs.
//
// ── Context biasing ──────────────────────────────────────────────────────
// The Multilingual endpoint's custom_terms is real vocabulary steering, and
// the richest biasing surface of any WhisperInk provider: each entry may be a
// plain string OR an object carrying a definition and pronunciations. The
// shared ContextBiasTerms list is a List<string>, so we emit plain strings;
// the object form is available for a future per-term editor. As with
// GoogleChirp3/Soniox/Deepgram the routing is native and the provider's
// BiasMechanism is informational only.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class ModulateTranscriber : ITranscriber
    {
        // Public so AppConfig.CreateDefaults builds its three presets from the
        // same strings DetectVariant matches on — otherwise a preset URL and the
        // variant detection could drift apart silently, and the only symptom
        // would be a request quietly sending the wrong field set.
        public const string DefaultBaseUrl = "https://platform.modulate.ai";

        public const string PathMultilingual     = "/api/velma-2-stt-batch";
        public const string PathEnglishFast      = "/api/velma-2-stt-batch-english-vfast";
        public const string PathMultilingualFast = "/api/velma-2-stt-batch-multilingual-vfast";

        // Modulate caps custom_terms at 1000 entries, and requires the term
        // strings serialized together as JSON to total under 8000 characters.
        // We stop short of the character ceiling to leave room for JSON escaping
        // (a term with a quote or backslash serializes longer than it reads).
        private const int MaxBiasTerms = 1000;
        private const int MaxBiasJsonChars = 7800;

        /// <summary>Which batch endpoint this provider points at — decides which
        /// form fields are legal on the request.</summary>
        private enum Variant { Multilingual, EnglishFast, MultilingualFast }

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public ModulateTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Modulate API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string BaseUrl =>
            string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/');

        /// <summary>The POST target. Deliberately NOT ApiProvider.ResolvedTranscriptionUrl:
        /// that falls back to the OpenAI-compatible /v1/audio/transcriptions path,
        /// which Modulate does not serve. A blank override means the Multilingual
        /// endpoint.</summary>
        private string Endpoint =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionEndpoint)
                ? BaseUrl + PathMultilingual
                : _provider.TranscriptionEndpoint.TrimEnd('/');

        /// <summary>Match against the path consts themselves, so this cannot drift
        /// from the preset URLs. Order matters: PathMultilingual is a prefix of
        /// both other paths, so it is never tested — it is the fallback.</summary>
        private static Variant DetectVariant(string url)
        {
            if (url.Contains(PathEnglishFast, StringComparison.OrdinalIgnoreCase))
                return Variant.EnglishFast;
            if (url.Contains(PathMultilingualFast, StringComparison.OrdinalIgnoreCase))
                return Variant.MultilingualFast;
            return Variant.Multilingual;
        }

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                string url = Endpoint;
                var variant = DetectVariant(url);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                // Bare header, no scheme prefix — not Authorization: Bearer.
                request.Headers.Add("X-API-Key", _provider.ApiKey);

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(wavBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

                // ── String fields FIRST, file LAST (house multipart convention) ──
                AddFormFields(content, variant, biasTerms);

                // Modulate validates the audio format by FILE EXTENSION, so the
                // part filename has to end in .wav or the request is rejected 400.
                content.Add(fileContent, "upload_file", "audio.wav");
                request.Content = content;

                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[modulate] {DescribeError(resp.StatusCode, body)}");
                    return null;
                }

                return ParseTranscript(body);
            }
            catch (OperationCanceledException)
            {
                _log($"ModulateTranscriber({_provider.Id}): cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log($"ModulateTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Add the string form fields legal for this endpoint. Each variant
        /// gets only what its own documentation lists — the fast endpoints take far
        /// fewer parameters than the Multilingual one.</summary>
        private void AddFormFields(MultipartFormDataContent content, Variant variant, IReadOnlyList<string> biasTerms)
        {
            if (variant == Variant.Multilingual)
            {
                // Dictation is one speaker and we read only "text", so speaker
                // attribution is wasted work. The API default here is true, so
                // this must be sent explicitly to turn it off.
                content.Add(new StringContent("false"), "speaker_diarization");
            }

            // English Fast is English-only and accepts no language parameter.
            if (variant != Variant.EnglishFast)
            {
                string lang = _provider.Language;
                if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                    content.Add(new StringContent(lang), "language");
            }

            // custom_terms exists only on the Multilingual endpoint, and only
            // inside the JSON "config" field — there is no top-level form field
            // for it. Fields set in config override their top-level twins, so we
            // put NOTHING else in here: an unset field does not override.
            if (variant == Variant.Multilingual)
            {
                var terms = BuildCustomTerms(biasTerms);
                if (terms != null)
                {
                    var config = new JsonObject { ["custom_terms"] = terms };
                    content.Add(new StringContent(config.ToJsonString()), "config");
                }
            }
            else if (biasTerms is { Count: > 0 })
            {
                _log($"[modulate] {biasTerms.Count} bias term(s) ignored — {variant} has no custom vocabulary");
            }
        }

        /// <summary>Shared ContextBiasTerms → a custom_terms JSON array, clamped to
        /// Modulate's 1000-entry and 8000-character limits. Returns null when there
        /// is nothing to send.</summary>
        private JsonArray? BuildCustomTerms(IReadOnlyList<string> biasTerms)
        {
            if (biasTerms is not { Count: > 0 }) return null;

            var arr = new JsonArray();
            int jsonChars = 0;
            int dropped = 0;

            foreach (var raw in biasTerms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string term = raw.Trim();

                // The term as it lands in the array: two quotes and a comma.
                int cost = term.Length + 3;
                if (arr.Count >= MaxBiasTerms || jsonChars + cost > MaxBiasJsonChars)
                {
                    dropped++;
                    continue;
                }

                jsonChars += cost;
                arr.Add(JsonValue.Create(term));
            }

            if (dropped > 0)
                _log($"[modulate] custom_terms clamped: sent {arr.Count}, dropped {dropped} (limits: {MaxBiasTerms} terms / {MaxBiasJsonChars} chars)");
            if (arr.Count == 0) return null;

            _log($"[modulate] custom_terms: {arr.Count} term(s)");
            return arr;
        }

        /// <summary>All three batch endpoints answer with a top-level "text".</summary>
        private string? ParseTranscript(string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                // An empty string is a documented, successful "no speech was
                // recognized" — not a failure. Null makes the dispatch site give
                // it the quiet Dismissed blip rather than the error buzz.
                return Clean(textEl.GetString());
            }

            _log($"[modulate] response had no transcript: {Preview(body)}");
            return null;
        }

        /// <summary>Modulate reports failures as {"detail": "..."} (a string, or the
        /// FastAPI validation array on 422). Surfacing that beats dumping raw JSON
        /// into debug.log.</summary>
        private static string DescribeError(HttpStatusCode code, string body)
        {
            string message = TryReadDetail(body) ?? Preview(body);

            // 401 and 403 both mean "the key is wrong" here — which endpoint you
            // hit decides which one you get, so name the cause rather than the code.
            string hint = code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " (check the Modulate API key in Provider Settings)"
                : "";

            return $"HTTP {(int)code}: {message}{hint}";
        }

        private static string? TryReadDetail(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("detail", out var detail)) return null;
                return detail.ValueKind switch
                {
                    JsonValueKind.String => detail.GetString(),
                    JsonValueKind.Array  => Preview(detail.GetRawText()),   // 422 validation errors
                    _                    => null,
                };
            }
            catch { return null; }
        }

        private static string? Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string Preview(string body) =>
            body.Length <= 500 ? body : body[..500];

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
