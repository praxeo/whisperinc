// DeepgramTranscriber.cs
// Cloud transcription via Deepgram's pre-recorded Listen API
// (https://api.deepgram.com/v1/listen).
//
// Deepgram is NOT OpenAI-compatible, so it bypasses HttpTranscriber:
//   - the audio is the raw request BODY (Content-Type: audio/wav), not a
//     multipart "file" part;
//   - every option (model, smart_format, language, keyterms) is a URL QUERY
//     parameter, not a form field;
//   - auth is "Authorization: Token <key>", not Bearer;
//   - the transcript lives at results.channels[0].alternatives[0].transcript.
//
// One synchronous POST per dictation — there's no job/poll cycle like Soniox.
// A short clip returns in ~1-3s, comfortably inside the shared HttpClient
// timeout.
//
// Best model: Nova-3 (TranscriptionModel = "nova-3"), Deepgram's latest. The
// field is user-editable, so nova-3-medical / nova-2 / whisper-cloud are a
// config edit, not a recompile. smart_format=true is baked on — it restores
// punctuation, capitalization, and number/date formatting, which is what
// dictation wants.
//
// Context biasing: Nova-3 (English) supports Keyterm Prompting — real
// vocabulary steering via repeated `keyterm` query params. WhisperInk's global
// ContextBiasTerms map straight onto those, so the provider's BiasMechanism is
// informational only, same approach as GoogleChirp3/Soniox. Older models
// (nova-2 and earlier) don't accept `keyterm`; for those we fall back to the
// legacy `keywords` param so a user who switches model still gets biasing.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class DeepgramTranscriber : ITranscriber
    {
        private const string DefaultBaseUrl = "https://api.deepgram.com";
        private const string DefaultModel = "nova-3";

        // Keep the keyterm/keywords list well under Deepgram's budget — same
        // conservative cap as the other native-bias providers.
        private const int MaxBiasTerms = 100;

        // Query params the transcriber owns — a DeepgramExtraParams entry for any
        // of these is ignored so a config typo can't fight the wired-in values.
        private static readonly HashSet<string> ReservedParams = new(StringComparer.OrdinalIgnoreCase)
        {
            "model", "smart_format", "language", "keyterm", "keywords",
        };

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public DeepgramTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Deepgram API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string BaseUrl =>
            string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/');

        private string Model =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionModel) ? DefaultModel : _provider.TranscriptionModel;

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                string url = BuildUrl(biasTerms);

                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Token", _provider.ApiKey);

                var content = new ByteArrayContent(wavBytes);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
                request.Content = content;

                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[deepgram] HTTP {(int)resp.StatusCode}: {Preview(body)}");
                    return null;
                }

                return ParseTranscript(body);
            }
            catch (OperationCanceledException)
            {
                _log($"DeepgramTranscriber({_provider.Id}): cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log($"DeepgramTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private string BuildUrl(IReadOnlyList<string> biasTerms)
        {
            // smart_format restores punctuation/casing/number formatting — the
            // dictation-friendly default. model + language constrain the engine.
            var query = new List<string>
            {
                $"model={Uri.EscapeDataString(Model)}",
                "smart_format=true",
            };

            string lang = _provider.Language;
            if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                query.Add($"language={Uri.EscapeDataString(lang)}");

            if (biasTerms is { Count: > 0 })
            {
                // Nova-3 (English) takes Keyterm Prompting via `keyterm`; older
                // models use the legacy `keywords` param. Pick per configured model
                // so biasing survives a user model swap.
                bool nova3 = Model.Contains("nova-3", StringComparison.OrdinalIgnoreCase);
                string field = nova3 ? "keyterm" : "keywords";

                int n = 0;
                foreach (var t in biasTerms)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (n >= MaxBiasTerms)
                    {
                        _log($"[deepgram] {field} truncated to {MaxBiasTerms} (had {biasTerms.Count})");
                        break;
                    }
                    query.Add($"{field}={Uri.EscapeDataString(t.Trim())}");
                    n++;
                }
                if (n > 0) _log($"[deepgram] {field}: {n} term(s)");
            }

            // Per-provider calibration / formatting passthrough (measurements,
            // numerals, dictation, paragraphs, …). Reserved keys are skipped so a
            // stray entry can't override model/smart_format/language/keyterm.
            if (_provider.DeepgramExtraParams is { Count: > 0 })
            {
                foreach (var kv in _provider.DeepgramExtraParams)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key) || ReservedParams.Contains(kv.Key)) continue;
                    query.Add($"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}");
                }
            }

            return $"{BaseUrl}/v1/listen?{string.Join("&", query)}";
        }

        private string? ParseTranscript(string body)
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("results", out var results)
                && results.TryGetProperty("channels", out var channels)
                && channels.ValueKind == JsonValueKind.Array
                && channels.GetArrayLength() > 0)
            {
                var ch0 = channels[0];
                if (ch0.TryGetProperty("alternatives", out var alts)
                    && alts.ValueKind == JsonValueKind.Array
                    && alts.GetArrayLength() > 0)
                {
                    var alt0 = alts[0];
                    if (alt0.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String)
                        return Clean(tr.GetString());
                }
            }

            _log($"[deepgram] response had no transcript: {Preview(body)}");
            return null;
        }

        private static string? Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string Preview(string body) =>
            body.Length <= 500 ? body : body[..500];

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
