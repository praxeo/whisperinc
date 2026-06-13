// SonioxTranscriber.cs
// Cloud transcription via the Soniox Speech-to-Text async REST API
// (https://api.soniox.com/v1).
//
// Unlike the OpenAI-compatible one-shot providers (HttpTranscriber), Soniox
// async is a multi-step job — there is no synchronous "POST audio, get text"
// endpoint. Each dictation runs the full flow:
//
//   1. POST /v1/files                      (multipart) → { "id": <file_id> }
//   2. POST /v1/transcriptions             (JSON: model, file_id,
//                                            language_hints, context.terms)
//                                          → { "id": <transcription_id>,
//                                              "status": "pending" }
//   3. GET  /v1/transcriptions/{id}        poll until status ==
//                                            "completed" | "error"
//   4. GET  /v1/transcriptions/{id}/transcript
//                                          → { "tokens": [ { "text": … }, … ] }
//   5. DELETE the transcription + file     (best-effort cleanup so jobs/files
//                                            don't accumulate under the key)
//
// Auth is Authorization: Bearer <ApiProvider.ApiKey>.
//
// Context biasing: Soniox's `context` object (StructuredContext) carries a
// `terms` array for real vocabulary steering. WhisperInk's global
// ContextBiasTerms map straight onto context.terms here, so the provider's
// BiasMechanism is informational only — same approach as GoogleChirp3Transcriber.
//
// Soniox transcript tokens carry their own leading spacing, so concatenating
// tokens[].text rebuilds the sentence without inserting our own separators.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class SonioxTranscriber : ITranscriber
    {
        private const string DefaultBaseUrl = "https://api.soniox.com";
        private const string DefaultModel = "stt-async-v5";

        // Poll cadence + overall ceiling. Short dictation clips finish in a
        // couple of seconds; the ceiling only exists so a wedged job can't hang
        // the dictation pipeline forever. Each individual request is still
        // bounded by the shared HttpClient timeout.
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
        private static readonly TimeSpan PollCeiling = TimeSpan.FromSeconds(120);

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public SonioxTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Soniox API key not set";
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

            string? fileId = null;
            string? transcriptionId = null;
            try
            {
                fileId = await UploadFileAsync(wavBytes, ct).ConfigureAwait(false);
                if (fileId == null) return null;

                transcriptionId = await CreateTranscriptionAsync(fileId, biasTerms, ct).ConfigureAwait(false);
                if (transcriptionId == null) return null;

                if (!await WaitForCompletionAsync(transcriptionId, ct).ConfigureAwait(false))
                    return null;

                return await FetchTranscriptAsync(transcriptionId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log($"SonioxTranscriber({_provider.Id}): cancelled");
                return null;
            }
            catch (Exception ex)
            {
                _log($"SonioxTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                // Best-effort cleanup — runs even on cancellation/error so we
                // don't orphan a file or job under the user's key. Not passed
                // the caller's ct: cleanup should proceed after a cancel.
                if (transcriptionId != null)
                    await TryDeleteAsync($"{BaseUrl}/v1/transcriptions/{transcriptionId}").ConfigureAwait(false);
                if (fileId != null)
                    await TryDeleteAsync($"{BaseUrl}/v1/files/{fileId}").ConfigureAwait(false);
            }
        }

        private void AddAuth(HttpRequestMessage req) =>
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);

        private async Task<string?> UploadFileAsync(byte[] wavBytes, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/files");
            AddAuth(request);

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(wavBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(fileContent, "file", "audio.wav");
            request.Content = form;

            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[soniox] upload HTTP {(int)resp.StatusCode}: {Preview(body)}");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        private async Task<string?> CreateTranscriptionAsync(string fileId, IReadOnlyList<string> biasTerms, CancellationToken ct)
        {
            var bodyNode = new JsonObject
            {
                ["model"] = Model,
                ["file_id"] = fileId,
            };

            // language_hints constrains detection; omit for "auto" so Soniox
            // identifies the language itself.
            string lang = _provider.Language ?? "en";
            if (!string.IsNullOrWhiteSpace(lang) && !string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
                bodyNode["language_hints"] = new JsonArray(lang);

            // Vocabulary steering via the context.terms array (StructuredContext).
            if (biasTerms is { Count: > 0 })
            {
                var terms = new JsonArray();
                foreach (var t in biasTerms)
                {
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (terms.Count >= 100)   // keep the context well under Soniox's budget
                    {
                        _log($"[soniox] context.terms truncated to 100 (had {biasTerms.Count})");
                        break;
                    }
                    terms.Add(t);
                }
                if (terms.Count > 0)
                {
                    bodyNode["context"] = new JsonObject { ["terms"] = terms };
                    _log($"[soniox] context.terms: {terms.Count} term(s)");
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v1/transcriptions");
            AddAuth(request);
            request.Content = new StringContent(bodyNode.ToJsonString(), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[soniox] create HTTP {(int)resp.StatusCode}: {Preview(body)}");
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }

        /// <summary>Polls the job until it completes. True = completed; false =
        /// error/timeout (already logged).</summary>
        private async Task<bool> WaitForCompletionAsync(string transcriptionId, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow + PollCeiling;
            string url = $"{BaseUrl}/v1/transcriptions/{transcriptionId}";

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddAuth(request);
                using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _log($"[soniox] poll HTTP {(int)resp.StatusCode}: {Preview(body)}");
                    return false;
                }

                using (var doc = JsonDocument.Parse(body))
                {
                    string? status = doc.RootElement.TryGetProperty("status", out var st) ? st.GetString() : null;
                    if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        string? msg = doc.RootElement.TryGetProperty("error_message", out var em) ? em.GetString() : null;
                        _log($"[soniox] transcription failed: {msg ?? "(no message)"}");
                        return false;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        _log($"[soniox] {transcriptionId} not done within {PollCeiling.TotalSeconds:F0}s (last status: {status ?? "?"})");
                        return false;
                    }
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }

        private async Task<string?> FetchTranscriptAsync(string transcriptionId, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v1/transcriptions/{transcriptionId}/transcript");
            AddAuth(request);
            using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log($"[soniox] transcript HTTP {(int)resp.StatusCode}: {Preview(body)}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);

            // Primary shape: a token array; each token's text carries its own
            // leading spacing, so straight concatenation rebuilds the text.
            if (doc.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var tok in tokens.EnumerateArray())
                    if (tok.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                return Clean(sb.ToString());
            }

            // Fallback: some responses surface a flat "text" field.
            if (doc.RootElement.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return Clean(textEl.GetString());

            return null;
        }

        private async Task TryDeleteAsync(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Delete, url);
                AddAuth(request);
                using var resp = await _http.SendAsync(request).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    _log($"[soniox] cleanup DELETE {url} -> HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { _log($"[soniox] cleanup DELETE {url} failed: {ex.Message}"); }
        }

        private static string? Clean(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static string Preview(string body) =>
            body.Length <= 500 ? body : body[..500];

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
