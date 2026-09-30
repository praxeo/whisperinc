// OmiTranscriber.cs
// Cloud transcription via Omi Health's medical speech-to-text API
// (https://api.omi.health/v1/audio/transcriptions; docs: docs.omi.health).
//
// The request is OpenAI-style multipart, but three things keep it out of
// HttpTranscriber:
//   - the default response_format is diarized_json, not {"text": …}, so
//     `json` (or, with a list, `verbose_json`) must be sent explicitly;
//   - biasing is a `vocabulary` field holding a JSON ARRAY string, which none of
//     HttpTranscriber's mechanisms produce;
//   - audio over 60.000 s (or any request with a webhook) is answered 202 with a
//     job envelope instead of text, and the transcript has to be polled for —
//     the Soniox pattern. Without this, every take over a minute would fail.
//
// Two models, one endpoint, so two presets that differ only by
// TranscriptionModel:
//   omi-medical-1       the flagship: eight languages, `vocabulary`, $0.29/h batch.
//   omi-medical-edge-1  the open Omi Med STT v1 weights (Parakeet TDT 0.6B v2
//                       plus a medical adapter) hosted: English only, NO
//                       vocabulary, NO language detection, $0.10/h. It's the
//                       quick way to measure the open model before running it
//                       locally, which CrispASR can't do yet.
// Both share 25 included hours a month.
//
// ── Vocabulary ────────────────────────────────────────────────────────────
// The shared Context Bias list goes to the flagship as `vocabulary`: up to 1,000
// terms of at most 96 characters, of which Omi itself keeps the 50 most relevant
// when there are more. Edge rejects the field (400), so it's never sent there and
// the skipped count is logged on every take, as Smallest.ai does.
//
// ── Language ──────────────────────────────────────────────────────────────
// Omitting `language` asks for dominant-language detection; "auto" would ask
// for per-utterance switching, which a single dictation never needs, so the
// house "auto" maps to omission. Edge accepts no detection at all, so it always
// gets "en". Both presets pin "en".
//
// ── Long takes ────────────────────────────────────────────────────────────
// The 202 envelope carries poll_url. GET poll_url?wait=20&include_result=true is
// a server-side long poll: it returns as soon as the job changes state, with the
// final JSON inline (up to 4 MiB) in result.content, else a result.download_url.
// Cancellation is unsupported in Omi's v1 (DELETE is reserved), so a take that
// hits its deadline just stops polling. Uploaded audio is deleted once processed;
// a job's result is kept until the expiry set in Omi's console (24 h by default).
//
// ── Errors ────────────────────────────────────────────────────────────────
// Every error is {"error": {"code", "message"}}. The code is logged with a
// plain-language hint, plus the x-request-id Omi asks for in support requests.
// 429 and 503 mean capacity (Edge yields to the flagship and may be turned away
// briefly), so the first POST is retried once after Retry-After, capped at 3 s;
// anything else fails the take, which stays journaled for ↻ retry.
//
// ── The list can be dropped, and Omi says so ─────────────────────────────
// A transcript sent with a `vocabulary` comes back with Omi's audit of it,
// but a job's (a take over 60 s) only in `verbose_json`, so that's what a
// take with a list asks for. On a 111 s take (2026-09-28) the audit reported
// "safety_fallback": true, "hinted output was not retained": every list term
// dropped, and the plain transcript wrote hematemesis for every
// hematochezia. Such a take is still delivered, but through
// ITranscriptWarning: the Warn cue, "⚠ Check terms!", and the take kept for a
// retry. application_status reads "partial" even on a right transcript, so
// it isn't a signal.

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
    public sealed class OmiTranscriber : ITranscriber, ITranscriptWarning
    {
        // Public so AppConfig.CreateDefaults builds its presets from the same
        // strings this class posts to.
        public const string DefaultBaseUrl = "https://api.omi.health";
        public const string PathTranscribe = "/v1/audio/transcriptions";
        public const string ModelFlagship = "omi-medical-1";
        public const string ModelEdge = "omi-medical-edge-1";

        /// <summary>Omi's `vocabulary` ceiling; past 50 it keeps the most relevant.</summary>
        private const int MaxVocabulary = 1000;
        private const int MaxTermChars = 96;

        /// <summary>Seconds the server may hold a poll open waiting for a change.</summary>
        private const int PollWaitSeconds = 20;

        /// <summary>The longest Retry-After worth waiting out inside a dictation.</summary>
        private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(3);

        /// <summary>Hosts Omi serves results from, which take the API key. A
        /// result link anywhere else is a signed storage URL and must not.</summary>
        private static readonly HashSet<string> OmiHosts = new(StringComparer.OrdinalIgnoreCase)
        {
            "api.omi.health", "api.eu.omi.health",
        };

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        /// <summary>Set when Omi's audit says the last take's list wasn't used.</summary>
        public string? LastWarning { get; private set; }

        public OmiTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Omi API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string BaseUrl =>
            string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/');

        private string Endpoint =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionEndpoint)
                ? BaseUrl + PathTranscribe
                : _provider.TranscriptionEndpoint;

        private string Model =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionModel) ? ModelFlagship : _provider.TranscriptionModel;

        /// <summary>The hosted open model: English only, no vocabulary.</summary>
        private bool IsEdge => Model.Contains("edge", StringComparison.OrdinalIgnoreCase);

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            LastWarning = null;
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                var fields = BuildFields(biasTerms);

                using var resp = await PostWithOneRetryAsync(wavBytes, fields, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.Accepted)
                    return await AwaitJobAsync(body, resp.Headers.Location, ct).ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[omi] {DescribeError(resp, body)}");
                    return null;
                }

                return ReadText(body, "response");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The per-take deadline (MainWindow logs it as the error).
                _log($"OmiTranscriber({_provider.Id}): stopped at the take's deadline");
                return null;
            }
            catch (OperationCanceledException ex)
            {
                // Not our token: the HttpClient's own timeout.
                _log($"OmiTranscriber({_provider.Id}): HTTP request timed out: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _log($"OmiTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private List<(string Name, string Value)> BuildFields(IReadOnlyList<string> biasTerms)
        {
            // String fields first, file last, as everywhere else. With a list,
            // verbose_json: a job's `json` result (a take over 60 s) carries
            // only the text, not the audit that says whether the list was used.
            string? vocabulary = BuildVocabulary(biasTerms);
            var fields = new List<(string, string)>
            {
                ("model", Model),
                ("response_format", vocabulary != null ? "verbose_json" : "json"),
            };

            string? lang = ResolveLanguage();
            if (lang != null) fields.Add(("language", lang));

            if (vocabulary != null) fields.Add(("vocabulary", vocabulary));

            return fields;
        }

        /// <summary>The language to send, or null to omit it, which asks Omi to
        /// detect the dominant one. Edge can't detect, so it always gets "en".</summary>
        private string? ResolveLanguage()
        {
            string lang = (_provider.Language ?? "").Trim();
            bool auto = lang.Length == 0 || lang.Equals("auto", StringComparison.OrdinalIgnoreCase);

            if (IsEdge)
            {
                if (!auto && !lang.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                    _log($"[omi] {Model} is English only; language '{lang}' ignored, sending en");
                return auto || !lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : lang;
            }
            return auto ? null : lang;
        }

        /// <summary>The shared list as a JSON array for `vocabulary`, or null when
        /// there's nothing to send or the model doesn't take it.</summary>
        private string? BuildVocabulary(IReadOnlyList<string> biasTerms)
        {
            if (biasTerms is not { Count: > 0 }) return null;

            if (IsEdge)
            {
                _log($"[omi] {Model} takes no vocabulary; {biasTerms.Count} bias term(s) not sent");
                return null;
            }

            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tooLong = new List<string>();
            foreach (var raw in biasTerms)
            {
                string term = (raw ?? "").Trim();
                if (term.Length == 0 || !seen.Add(term)) continue;
                if (term.Length > MaxTermChars) { tooLong.Add(term); continue; }
                if (kept.Count >= MaxVocabulary)
                {
                    _log($"[omi] vocabulary truncated to {MaxVocabulary} (had {biasTerms.Count})");
                    break;
                }
                kept.Add(term);
            }

            if (tooLong.Count > 0)
                _log($"[omi] {tooLong.Count} bias term(s) over {MaxTermChars} characters dropped: {string.Join(" | ", tooLong.Select(t => t[..Math.Min(40, t.Length)] + "…"))}");
            if (kept.Count == 0) return null;

            _log($"[omi] vocabulary: {kept.Count} term(s)" + (kept.Count > 50 ? " (Omi keeps the 50 most relevant)" : ""));
            return JsonSerializer.Serialize(kept);
        }

        /// <summary>POST the take, retrying once on a capacity answer (429/503)
        /// when Retry-After is short. The content is rebuilt for the retry: an
        /// HttpContent can't be sent twice.</summary>
        private async Task<HttpResponseMessage> PostWithOneRetryAsync(
            byte[] wavBytes, List<(string Name, string Value)> fields, CancellationToken ct)
        {
            HttpResponseMessage resp;
            using (var first = BuildRequest(wavBytes, fields))
                resp = await _http.SendAsync(first, ct).ConfigureAwait(false);
            if (resp.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
                return resp;

            TimeSpan wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
            if (wait > MaxRetryWait) return resp;   // too long to wait inside a dictation

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log($"[omi] {DescribeError(resp, body)}; retrying once in {wait.TotalSeconds:F0} s");
            resp.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
            using var second = BuildRequest(wavBytes, fields);
            return await _http.SendAsync(second, ct).ConfigureAwait(false);
        }

        private HttpRequestMessage BuildRequest(byte[] wavBytes, List<(string Name, string Value)> fields)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            AddAuth(request);

            var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
                form.Add(new StringContent(value), name);
            var file = new ByteArrayContent(wavBytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(file, "file", "audio.wav");
            request.Content = form;
            return request;
        }

        private void AddAuth(HttpRequestMessage req) =>
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);

        /// <summary>A 202: the take is over 60 s and became a job. Long-poll it
        /// until it succeeds or fails; the take's deadline ends it otherwise.</summary>
        private async Task<string?> AwaitJobAsync(string envelope, Uri? location, CancellationToken ct)
        {
            string? pollUrl = null, jobId = null;
            try
            {
                using var doc = JsonDocument.Parse(envelope);
                pollUrl = Str(doc.RootElement, "poll_url");
                jobId = Str(doc.RootElement, "id");
            }
            catch (JsonException) { }
            pollUrl ??= location?.IsAbsoluteUri == true ? location.ToString()
                      : location != null ? new Uri(new Uri(Endpoint), location).ToString()
                      : jobId != null ? $"{BaseUrl}/v1/jobs/{jobId}" : null;
            if (pollUrl == null)
            {
                _log($"[omi] 202 without a job to poll: {Preview(envelope)}");
                return null;
            }
            _log($"[omi] over 60 s: transcribing as job {jobId ?? "?"}");

            string url = pollUrl + (pollUrl.Contains('?') ? "&" : "?") + $"wait={PollWaitSeconds}&include_result=true";
            string? lastStatus = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuth(request);
                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[omi] job {jobId}: poll {DescribeError(resp, body)}");
                    return null;
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string? status = Str(root, "status");
                if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                    return await ReadJobResultAsync(root, jobId, ct).ConfigureAwait(false);
                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    _log($"[omi] job {jobId} failed: {DescribeEnvelope(body) ?? Preview(body)}");
                    return null;
                }

                // The long poll returns on a change of state; a quick answer with
                // no change must not turn into a tight loop.
                if (status == lastStatus) await Task.Delay(500, ct).ConfigureAwait(false);
                lastStatus = status;
            }
        }

        private async Task<string?> ReadJobResultAsync(JsonElement job, string? jobId, CancellationToken ct)
        {
            if (!job.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            {
                _log($"[omi] job {jobId} succeeded with no result");
                return null;
            }
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                return ReadText(content.GetRawText(), $"job {jobId}");
            if (result.TryGetProperty("expired", out var exp) && exp.ValueKind == JsonValueKind.True)
            {
                _log($"[omi] job {jobId}: its result has already expired");
                return null;
            }

            string? link = Str(result, "download_url");
            if (link == null || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
            {
                _log($"[omi] job {jobId} succeeded with no readable result: {Preview(result.GetRawText())}");
                return null;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            // The key goes only to Omi's own hosts; a signed storage link needs none.
            string endpointHost = Uri.TryCreate(Endpoint, UriKind.Absolute, out var ep) ? ep.Host : "";
            if (OmiHosts.Contains(uri.Host) || uri.Host.Equals(endpointHost, StringComparison.OrdinalIgnoreCase))
                AddAuth(request);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[omi] job {jobId}: result download {DescribeError(resp, body)}");
                return null;
            }
            return ReadText(body, $"job {jobId}");
        }

        /// <summary>`text` is always present on a transcript. Empty text is a real
        /// "nothing recognized", returned as "" rather than as a failure, so the
        /// dispatch site can tell silence from a broken request.</summary>
        private string? ReadText(string json, string what)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                NoteVocabularyAudit(doc.RootElement, what);
                return (text.GetString() ?? "").Trim();
            }
            _log($"[omi] {what} had no transcript: {Preview(json)}");
            return null;
        }

        /// <summary>Reads the `vocabulary` audit Omi returns with a transcript
        /// sent with a list, and sets <see cref="LastWarning"/> when the list
        /// wasn't used: a safety fallback, or nothing of it prompted.</summary>
        private void NoteVocabularyAudit(JsonElement root, string what)
        {
            if (!root.TryGetProperty("vocabulary", out var v) || v.ValueKind != JsonValueKind.Object) return;
            static int Int(JsonElement el, string name) =>
                el.TryGetProperty(name, out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int i) ? i : 0;

            if (v.TryGetProperty("safety_fallback", out var sf) && sf.ValueKind == JsonValueKind.True)
                LastWarning = "dropped your term list for this take (its safety fallback), so none of the terms were applied";
            else if (Int(v, "requested_terms") > 0 && Int(v, "prompted_terms") == 0)
                LastWarning = "didn't use your term list for this take";
            else
                return;
            _log($"[omi] {what}: {LastWarning} (Omi: {Str(v, "coverage_summary") ?? "no summary"})");
        }

        /// <summary>"HTTP 402 [billing_blocked] message (hint) request …".</summary>
        private static string DescribeError(HttpResponseMessage resp, string body)
        {
            int code = (int)resp.StatusCode;
            string message = DescribeEnvelope(body) ?? Preview(body);
            string hint = code switch
            {
                401 => " (check the Omi API key in Provider Settings)",
                402 => " (Omi usage is paused for billing: the included hours are used up, a spend cap was hit, or a payment failed; see console.omi.health)",
                403 => " (this Omi key isn't entitled to that feature)",
                413 => " (audio too large for Omi)",
                422 => " (Omi rejected a field value: the language or the vocabulary)",
                429 or 503 => " (Omi is at capacity; the take is kept for ↻ retry)",
                _ => "",
            };
            string requestId = resp.Headers.TryGetValues("x-request-id", out var ids) ? $" request {ids.First()}" : "";
            return $"HTTP {code}: {message}{hint}{requestId}";
        }

        /// <summary>"[code] message" from Omi's {"error": {"code", "message"}}.</summary>
        private static string? DescribeEnvelope(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object)
                    return null;
                string? code = Str(err, "code");
                string? message = Str(err, "message");
                if (code == null) return message;
                return message == null ? $"[{code}]" : $"[{code}] {message}";
            }
            catch (JsonException) { return null; }
        }

        private static string? Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string Preview(string body) =>
            body.Length <= 500 ? body : body[..500];

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
