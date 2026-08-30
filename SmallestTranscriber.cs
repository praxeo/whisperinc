// SmallestTranscriber.cs
// Cloud transcription via Smallest.ai's Waves pre-recorded STT endpoint
// (https://api.smallest.ai/waves/v1/stt/).
//
// Smallest is NOT OpenAI-compatible, so it bypasses HttpTranscriber:
//   - the audio is the raw request BODY (Content-Type: application/octet-stream),
//     not a multipart "file" part;
//   - every option is a URL QUERY parameter, not a form field;
//   - the transcript is the top-level "transcription" (not "text", not the
//     nested channels/alternatives shape Deepgram uses).
//
// Auth IS plain Bearer, but that alone doesn't make it OpenAI-shaped — the
// raw body and query-param options are what force a dedicated transcriber.
//
// One synchronous POST per dictation, like Deepgram/Modulate — no job/poll
// cycle as with Soniox. Measured on jfk.wav (11 s audio): server-side
// processing ~180-200 ms at rtfx 55-62, ~1.4 s wall-clock including upload,
// comfortably inside the shared 15 s HttpClient timeout.
//
// ── The two models (one preset each; see AppConfig.CreateDefaults) ────────
//
//   pulse-pro   English only. The accurate one, and the steadier one: measured
//               1.37-1.69 s wall-clock across six clips (a tight spread) and
//               terminal punctuation on 6/6.
//   pulse       Multilingual (46 accepted language codes). Wider latency
//               spread (0.85-2.40 s on the same clips) and it DROPPED the
//               terminal period on 2 of 6 — which matters because WhisperInk
//               pastes the transcript verbatim into whatever has focus.
//
// Unlike Modulate, the model here is a real `model` query parameter rather
// than a distinct endpoint path, so both presets share one URL and differ
// only by TranscriptionModel.
//
// ── language: the one parameter that can hard-fail the request ───────────
// Validation is strict-enum. WhisperInk's house "auto" sentinel is NOT a
// legal value and returns 400 (verified), so it can never simply be passed
// through the way DeepgramTranscriber passes its language:
//
//   pulse-pro accepts exactly one value: "en". Anything else 400s, so this
//             transcriber coerces to "en" and logs the substitution.
//   pulse     accepts 46 codes. (The published docs list only 26 and omit
//             several the API actually takes; the full set was read off the
//             API's own enum-validation response.)
//
// Passing the enum is necessary but NOT sufficient — a code can be in the
// enum and still be refused for the account's region with
// error_code=LANGUAGE_NOT_ENABLED_IN_REGION. Verified on this key: of the
// four auto-detect aggregators, "multi-eu" and "multi-asian" answer 200 while
// "multi" and "multi-indic" are region-gated. So WhisperInk's "auto" maps to
// "multi-eu" — the broadest aggregator confirmed enabled, and one that
// includes English — rather than to the spec's any-language "multi", which
// would turn every dictation into a 400.
//
// We ALWAYS send an explicit language, and on Pulse that is load-bearing
// rather than tidiness: omitting the parameter makes the server fall back to
// its own "multi" default and fail with the same region error. (Pulse Pro
// tolerates omission and defaults to "en", but relying on a server-side
// default that can change underneath us would silently change transcripts.)
//
// ── No context biasing exists on this API ────────────────────────────────
// There is no keyterm / hotword / custom-vocabulary field of any kind on the
// pre-recorded endpoint. The shared ContextBiasTerms list therefore cannot be
// routed anywhere, and the provider's BiasMechanism is "none" — a real "none",
// not the Cohere-style phantom where a field is accepted and silently dropped.
// A non-empty list is logged as ignored on each call, because on this provider
// a mis-recognized term genuinely cannot be corrected.
//
// ── Every enrichment knob is deliberately left off ───────────────────────
// word_timestamps, diarize, redact_pii, redact_pci, emotion_detection and
// gender_detection all default to false and all stay false. We read only
// "transcription", so most are pure added latency (word_timestamps alone costs
// roughly a third of Pulse Pro's throughput), and the redaction pair is
// actively destructive for dictation: it REPLACES words in the transcript with
// [FIRSTNAME_1] / [PHONENUMBER_1] tokens, which would then paste as literal
// markup — the same failure mode as Modulate's pii_phi_tagging.
//
// webhook_url is likewise never sent. It switches the 200 body to
// {"status":"processing","request_id":...} with no transcript at all, which
// for a dictation tool is simply a lost utterance; ParseTranscript detects
// that shape anyway and says so plainly rather than reporting "no transcript".
//
// There is deliberately no SmallestExtraParams passthrough (unlike
// DeepgramExtraParams): unknown query params are ignored rather than rejected,
// so one could be added safely, but every knob this API exposes degrades
// dictation, so there is nothing worth opting into.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class SmallestTranscriber : ITranscriber
    {
        // Public so AppConfig.CreateDefaults builds its presets from the same
        // strings this class posts to — a preset URL and the request path can't
        // drift apart. The trailing slash is load-bearing.
        public const string DefaultBaseUrl = "https://api.smallest.ai";
        public const string PathTranscribe = "/waves/v1/stt/";

        public const string ModelPulsePro = "pulse-pro";
        public const string ModelPulse    = "pulse";

        /// <summary>Where WhisperInk's "auto" sentinel lands on Pulse. NOT the
        /// spec's any-language "multi": that is enum-legal but returned
        /// LANGUAGE_NOT_ENABLED_IN_REGION on this account, as did "multi-indic".
        /// "multi-eu" is the broadest aggregator verified enabled, and it covers
        /// English. If it is ever gated too, the 400 names the cause.</summary>
        private const string PulseAutoDetect = "multi-eu";

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public SmallestTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Smallest.ai API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string BaseUrl =>
            string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/');

        /// <summary>The POST target. Deliberately NOT ApiProvider.ResolvedTranscriptionUrl:
        /// that falls back to the OpenAI-compatible /v1/audio/transcriptions path,
        /// which Smallest does not serve.</summary>
        private string Endpoint =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionEndpoint)
                ? BaseUrl + PathTranscribe
                : _provider.TranscriptionEndpoint;

        private string Model =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionModel) ? ModelPulsePro : _provider.TranscriptionModel.Trim();

        private bool IsPulsePro =>
            Model.Equals(ModelPulsePro, StringComparison.OrdinalIgnoreCase);

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;

            // No vocabulary-steering field exists here, so terms the user is
            // relying on elsewhere are silently inert on this provider. Say so.
            if (biasTerms is { Count: > 0 })
                _log($"[smallest] {biasTerms.Count} bias term(s) ignored — the Waves STT API has no custom-vocabulary field");

            try
            {
                string url = BuildUrl();

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);

                var content = new ByteArrayContent(wavBytes);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                request.Content = content;

                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[smallest] {DescribeError(resp.StatusCode, body)}");
                    return null;
                }

                return ParseTranscript(body);
            }
            catch (OperationCanceledException)
            {
                _log($"SmallestTranscriber({_provider.Id}): cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log($"SmallestTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private string BuildUrl()
        {
            var query = new List<string>
            {
                $"model={Uri.EscapeDataString(Model)}",
                $"language={Uri.EscapeDataString(ResolveLanguage())}",
            };

            char sep = Endpoint.Contains('?') ? '&' : '?';
            return $"{Endpoint}{sep}{string.Join("&", query)}";
        }

        /// <summary>Map the provider's Language onto a value this model's strict
        /// enum actually accepts. An unmapped value would 400 the whole
        /// dictation, so both fallbacks are deliberate rather than defensive.</summary>
        private string ResolveLanguage()
        {
            string lang = (_provider.Language ?? "").Trim();
            bool isAuto = lang.Length == 0 || lang.Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (IsPulsePro)
            {
                // Pulse Pro's enum is exactly ["en"] — verified against the API.
                if (!isAuto && !lang.Equals("en", StringComparison.OrdinalIgnoreCase))
                    _log($"[smallest] language '{lang}' -> 'en' ({ModelPulsePro} is English-only and rejects every other value)");
                return "en";
            }

            // Pulse: "auto" resolves to an aggregator; everything else passes
            // through, and a code that is invalid — or enum-legal but not enabled
            // for this region — surfaces as the API's own error in the log.
            return isAuto ? PulseAutoDetect : lang;
        }

        /// <summary>Both models answer with a top-level "transcription".</summary>
        private string? ParseTranscript(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("transcription", out var tr) && tr.ValueKind == JsonValueKind.String)
            {
                // An empty string is a successful "no speech recognized", not a
                // failure. Null makes the dispatch site give it the quiet
                // Dismissed blip rather than the error buzz.
                return Clean(tr.GetString());
            }

            // The async/webhook shape: 200, but the transcript went to a webhook
            // instead of to us. Nothing sets webhook_url today, so this means a
            // hand-edited endpoint — name the cause rather than "no transcript".
            if (root.TryGetProperty("status", out var st)
                && st.ValueKind == JsonValueKind.String
                && string.Equals(st.GetString(), "processing", StringComparison.OrdinalIgnoreCase))
            {
                _log("[smallest] request was accepted in async webhook mode — the transcript goes to webhook_url, not to WhisperInk. Remove webhook_url from the endpoint URL.");
                return null;
            }

            _log($"[smallest] response had no transcript: {Preview(body)}");
            return null;
        }

        /// <summary>Smallest reports failures in three different shapes: auth is a
        /// bare {"error":"unauthorized"}; validation is
        /// {"status":"error","message":...,"errors":[{"message":...}]}; and
        /// entitlement failures are {"status":"error","error_code":...,"message":...}
        /// with no errors[] array. Surfacing the inner message — and the
        /// error_code, which is what distinguishes a region gate from a typo —
        /// beats dumping raw JSON into debug.log.</summary>
        private static string DescribeError(HttpStatusCode code, string body)
        {
            string message = TryReadMessage(body) ?? Preview(body);

            string hint = code == HttpStatusCode.Unauthorized
                ? " (check the Smallest.ai API key in Provider Settings)"
                : "";

            return $"HTTP {(int)code}: {message}{hint}";
        }

        private static string? TryReadMessage(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Auth shape: {"error":"unauthorized"}
                if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                    return err.GetString();

                // Validation shape: the top-level message is generic ("Invalid
                // query parameters"); the useful detail — which param, what was
                // received, what was expected — is in errors[0].message.
                string? top = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;

                if (root.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array
                    && errors.GetArrayLength() > 0
                    && errors[0].TryGetProperty("message", out var detail)
                    && detail.ValueKind == JsonValueKind.String)
                {
                    return top == null ? detail.GetString() : $"{top}: {detail.GetString()}";
                }

                // Entitlement shape carries no errors[]; the code is the part
                // that tells a region gate apart from a malformed request.
                if (root.TryGetProperty("error_code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                    return top == null ? codeEl.GetString() : $"[{codeEl.GetString()}] {top}";

                return top;
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
