// Reson8Transcriber.cs
// Cloud transcription via Reson8's prerecorded speech-to-text endpoint
// (https://api.reson8.dev/v1/speech-to-text/prerecorded).
//
// Reson8 is NOT OpenAI-compatible, so it bypasses HttpTranscriber:
//   - the audio is the raw request BODY (Content-Type: application/octet-stream),
//     not a multipart "file" part;
//   - every option is a URL QUERY parameter, not a form field;
//   - auth is "Authorization: ApiKey <key>" — a THIRD scheme alongside
//     Deepgram's "Token" and the Bearer used everywhere else;
//   - errors come back as RFC 7807 application/problem+json with a lowercase
//     `code` field, not a shape any existing transcriber parses.
//
// One synchronous POST per dictation, like Deepgram/Modulate/Smallest — no
// job/poll cycle as with Soniox.
//
// There is exactly ONE endpoint and NO `model` query parameter, which is why
// this provider ships as a single preset. That is worth stating because the two
// most recent Case-C additions were the opposite: Modulate picks its model by
// ENDPOINT PATH (three presets, one per URL) and Smallest by a real `model`
// param (two presets, one URL). Here the customization axis is neither — it is
// `custom_model_id`, a persistent vocabulary built out-of-band in the Reson8
// console. So TranscriptionModel is unused on this path and left blank.
//
// ── Biasing is real, and it is the reason to care about this provider ────
// `phrases` takes a comma-separated list of up to 250 terms and biases the
// transcript toward them, so the shared ContextBiasTerms list routes straight
// onto it. BiasMechanism ("reson8_phrases") is informational only — this class
// does the routing, the same native-routing pattern as Google Chirp 3 / Soniox /
// Deepgram / Modulate.
//
// Reson8's own guidance matches what WhisperInk measured on qwen3-asr: keep the
// list tight. Upstream is explicit that "a large set of irrelevant ones can
// degrade transcription", that good phrases are "the terms the base model gets
// wrong: specialised vocabulary, uncommon names, brand and product names", and
// that "everyday words ... only dilute the model". So this is a provider where a
// bloated Context Bias list actively costs accuracy rather than merely being
// ignored — the opposite of Smallest.ai, where the list is simply inert.
//
// COMMA IS THE DELIMITER, so a bias term containing one would silently split
// into two bogus phrases server-side. Terms are sanitized (commas → spaces)
// rather than dropped, and the substitution is logged: "Smith, John" still
// biases usefully as "Smith John", where dropping it would lose the term.
//
// For vocabulary larger than 250 terms, or reused across requests, the upstream
// answer is a custom model (up to 50,000 phrases) referenced by custom_model_id
// — set that via Reson8ExtraParams, no recompile.
//
// ── language: omitting it IS auto-detect, and there is no "auto" value ───
// This is the inverse of SmallestTranscriber's trap. There, an explicit
// language must ALWAYS be sent because the server's own default is region-
// gated. Here, auto-detection is requested by OMITTING the parameter — there is
// no "auto" sentinel to send, so WhisperInk's house value must be translated
// into an absent param rather than forwarded.
//
// Reson8 supports exactly ten languages (de/en/es/fr/fy/it/nl/pl/pt/sv), and
// WhisperInk's language dropdown offers six codes that are NOT among them
// (ja/ko/zh/ru/ar/hi). Sending one would 400 with invalid_query_parameter and
// lose the dictation — every press, for a user who just picked their language
// from a combo box. So an unsupported code is dropped with a loud log line and
// the request falls back to auto-detection: the closest legal behaviour, since
// it still transcribes rather than hard-failing, and it cannot invent support
// the service does not have.
//
// Pinning beats detecting here, and it matters more for WhisperInk than for
// most callers: upstream notes auto-detect is "less reliable for short
// utterances", and short utterances are exactly what a push-to-talk dictation
// tool produces. Hence the preset ships Language = "en". A comma-separated list
// ("nl,en") is legal and passes through — it constrains detection to those
// candidates, better than unrestricted auto-detect but still worse than pinning
// one.
//
// ── Response-shaping knobs are deliberately left off ─────────────────────
// include_timestamps / include_words / include_language / include_confidence /
// diarize / max_speakers all default false-or-absent and stay that way: we read
// only `text`, so each is pure added latency. Unlike Modulate's pii_phi_tagging
// or Smallest's redact_pii, none of them CORRUPT the transcript — `text` remains
// the full clean transcript even with diarize=true (segments are added ALONGSIDE
// it, not substituted for it) — so ParseTranscript needs no defence against
// them, only a cheap segments fallback.
//
// Unlike Modulate and Smallest, this provider DOES get an extra-params
// passthrough (Reson8ExtraParams, the Deepgram analog). The rule those two
// followed was "every knob this API exposes degrades dictation, so there is
// nothing worth opting into" — which is simply not true here. Three knobs are
// useful and none can be guessed on the user's behalf:
//   custom_model_id  — the persistent-vocabulary path, the strongest biasing
//                      lever this API has (50,000 phrases vs 250).
//   filler_mode      — `clean` removes filler words, `natural` (the default)
//                      lets the model decide, `verbatim` preserves them.
//                      `clean` is plausibly what dictation wants, but it
//                      silently changes transcripts, so it stays an opt-in.
//   patterns         — regex-style recovery for short alphanumeric tokens
//                      (order codes, licence plates).
// Keys this class owns are reserved and skipped so a stray entry cannot fight
// the wired-in values. encoding/sample_rate/channels are reserved for a
// different reason than language/phrases: this transcriber posts a complete WAV,
// so declaring pcm_s16le would make the server misparse the 44-byte RIFF header
// as audio and put a burst of noise at the front of every transcript.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class Reson8Transcriber : ITranscriber
    {
        // Public so AppConfig.CreateDefaults builds its preset from the same
        // strings this class posts to — a preset URL and the request path can't
        // drift apart.
        public const string DefaultBaseUrl = "https://api.reson8.dev";
        public const string PathTranscribe = "/v1/speech-to-text/prerecorded";

        /// <summary>Reson8's documented `phrases` ceiling.</summary>
        private const int MaxPhrases = 250;

        /// <summary>Character budget for the joined `phrases` value, before URL
        /// encoding. 250 terms is legal but rides in the QUERY STRING, and
        /// percent-encoding roughly triples anything non-alphanumeric — so the
        /// count limit alone does not bound the URL. This keeps even a full list
        /// well inside the ~8 KB request line that servers and proxies commonly
        /// cap at, since a 414 would fail the dictation as surely as a 400.</summary>
        private const int MaxPhrasesChars = 4000;

        /// <summary>The ten codes Reson8 accepts. Anything else is a 400
        /// (invalid_query_parameter), so an unsupported value is dropped rather
        /// than forwarded — see the header comment.</summary>
        private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            "de", "en", "es", "fr", "fy", "it", "nl", "pl", "pt", "sv",
        };

        /// <summary>Query params this transcriber owns. A Reson8ExtraParams entry
        /// for any of these is ignored so a config typo cannot fight the wired-in
        /// values or contradict the WAV body we actually post.</summary>
        private static readonly HashSet<string> ReservedParams = new(StringComparer.OrdinalIgnoreCase)
        {
            "encoding", "sample_rate", "channels", "language", "phrases",
        };

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public Reson8Transcriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Reson8 API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string BaseUrl =>
            string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/');

        /// <summary>The POST target. Deliberately NOT ApiProvider.ResolvedTranscriptionUrl:
        /// that falls back to the OpenAI-compatible /v1/audio/transcriptions path,
        /// which Reson8 does not serve.</summary>
        private string Endpoint =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionEndpoint)
                ? BaseUrl + PathTranscribe
                : _provider.TranscriptionEndpoint;

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                string url = BuildUrl(biasTerms);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                // "ApiKey <key>" — not Bearer, not Deepgram's "Token".
                request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", _provider.ApiKey);

                var content = new ByteArrayContent(wavBytes);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
                request.Content = content;

                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[reson8] {DescribeError(resp.StatusCode, body)}");
                    return null;
                }

                return ParseTranscript(body);
            }
            catch (OperationCanceledException)
            {
                _log($"Reson8Transcriber({_provider.Id}): cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log($"Reson8Transcriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private string BuildUrl(IReadOnlyList<string> biasTerms)
        {
            // encoding=auto is already the server default; sending it explicitly
            // documents that we post a container (WAV) rather than raw PCM, and
            // survives a change to that default. sample_rate/channels are NOT
            // sent: with a container they are ignored in favour of the RIFF
            // header, and stating them invites a mismatch if MicCapture's format
            // ever changes.
            var query = new List<string> { "encoding=auto" };

            string? lang = ResolveLanguage();
            if (lang != null)
                query.Add($"language={Uri.EscapeDataString(lang)}");

            string? phrases = BuildPhrases(biasTerms);
            if (phrases != null)
                query.Add($"phrases={Uri.EscapeDataString(phrases)}");

            // Per-provider passthrough (custom_model_id, filler_mode, patterns, …).
            // Reserved keys are skipped so a stray entry cannot override the
            // wired-in values or contradict the WAV body.
            if (_provider.Reson8ExtraParams is { Count: > 0 })
            {
                foreach (var kv in _provider.Reson8ExtraParams)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    if (ReservedParams.Contains(kv.Key))
                    {
                        _log($"[reson8] Reson8ExtraParams['{kv.Key}'] ignored — reserved (set by the transcriber)");
                        continue;
                    }
                    query.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}");
                }
            }

            char sep = Endpoint.Contains('?') ? '&' : '?';
            return $"{Endpoint}{sep}{string.Join("&", query)}";
        }

        /// <summary>Resolve the provider's Language into a value Reson8 accepts, or
        /// null to OMIT the parameter — which is how auto-detection is requested,
        /// since there is no "auto" value to send.</summary>
        private string? ResolveLanguage()
        {
            string lang = (_provider.Language ?? "").Trim();
            if (lang.Length == 0 || lang.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return null;

            // A comma-separated list is legal and constrains detection to those
            // candidates, so filter per-code rather than rejecting the whole value.
            var codes = lang.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var keep = codes.Where(c => SupportedLanguages.Contains(c)).ToList();
            var drop = codes.Where(c => !SupportedLanguages.Contains(c)).ToList();

            if (drop.Count > 0)
            {
                _log($"[reson8] language '{string.Join(",", drop)}' not supported — Reson8 accepts only " +
                     $"{string.Join("/", SupportedLanguages.OrderBy(s => s, StringComparer.Ordinal))}. " +
                     (keep.Count > 0
                        ? $"Using '{string.Join(",", keep)}'."
                        : "Falling back to auto-detection (less reliable on short dictation clips)."));
            }

            return keep.Count > 0 ? string.Join(",", keep) : null;
        }

        /// <summary>Join the shared Context Bias list into the comma-separated
        /// `phrases` value, or null when there is nothing to send. Clamped to
        /// Reson8's 250-phrase limit and to a URL-safe character budget.</summary>
        private string? BuildPhrases(IReadOnlyList<string> biasTerms)
        {
            if (biasTerms is not { Count: > 0 }) return null;

            var kept = new List<string>();
            int chars = 0;
            int sanitized = 0;
            bool charBudgetHit = false;

            foreach (var raw in biasTerms)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // Comma is the delimiter: an embedded one would split the term
                // into two bogus phrases. Replace rather than drop, so the term
                // still biases ("Smith, John" -> "Smith John").
                string term = raw.Trim();
                if (term.Contains(','))
                {
                    term = string.Join(" ", term.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    if (term.Length == 0) continue;
                    sanitized++;
                }

                if (kept.Count >= MaxPhrases)
                {
                    _log($"[reson8] phrases truncated to {MaxPhrases} (had {biasTerms.Count})");
                    break;
                }

                int cost = term.Length + (kept.Count > 0 ? 1 : 0); // +1 for the joining comma
                if (chars + cost > MaxPhrasesChars)
                {
                    charBudgetHit = true;
                    break;
                }

                kept.Add(term);
                chars += cost;
            }

            if (charBudgetHit)
                _log($"[reson8] phrases truncated to {kept.Count} term(s) — {MaxPhrasesChars}-char query budget reached");
            if (sanitized > 0)
                _log($"[reson8] {sanitized} bias term(s) contained a comma (the phrases delimiter) — commas replaced with spaces");

            if (kept.Count == 0) return null;

            _log($"[reson8] phrases: {kept.Count} term(s)");
            return string.Join(",", kept);
        }

        /// <summary>`text` is always present on a 200 — including when diarize=true,
        /// where per-speaker `segments` are added ALONGSIDE the full transcript
        /// rather than replacing it. The segments path is therefore unreachable
        /// with the params we send; it is kept as cheap insurance in case a
        /// Reson8ExtraParams entry turns diarization on and a future response
        /// shape ever drops the top-level field.</summary>
        private string? ParseTranscript(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                // An empty string is a successful "no speech recognized", not a
                // failure. Null makes the dispatch site give it the quiet
                // Dismissed blip rather than the error buzz.
                return Clean(text.GetString());
            }

            if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var seg in segments.EnumerateArray())
                {
                    if (seg.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String)
                    {
                        string? s = st.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim());
                    }
                }

                if (parts.Count > 0) return Clean(string.Join(" ", parts));
            }

            _log($"[reson8] response had no transcript: {Preview(body)}");
            return null;
        }

        /// <summary>Errors are RFC 7807 application/problem+json with a lowercase
        /// `code` field alongside the standard `title`/`detail`. The code is what
        /// separates causes that share a status — a 400 is either
        /// invalid_query_parameter (a bad language or param) or session_rejected
        /// (an unknown custom_model_id), which are very different fixes. 402/413/429
        /// carry a status more diagnostic than any message, so each gets a
        /// plain-language hint.</summary>
        private static string DescribeError(HttpStatusCode code, string body)
        {
            string message = TryReadProblem(body) ?? Preview(body);

            string hint = (int)code switch
            {
                401 => " (check the Reson8 API key in Provider Settings)",
                402 => " (Reson8 credit limit exceeded — check plan/usage at console.reson8.dev)",
                413 => " (audio too large for the prerecorded endpoint)",
                429 => " (Reson8 concurrent-connection limit — another request is still in flight)",
                _   => "",
            };

            return $"HTTP {(int)code}: {message}{hint}";
        }

        private static string? TryReadProblem(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string? Str(string name) =>
                    root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                        ? el.GetString()
                        : null;

                // `detail` is the specific one; `title` is the generic RFC 7807
                // summary. Prefer detail, fall back to title.
                string? text = Str("detail") ?? Str("title");
                string? errCode = Str("code");

                if (errCode == null) return text;
                return text == null ? errCode : $"[{errCode}] {text}";
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
