using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    /// <summary>
    /// Generic OpenAI-compatible multipart-form transcription client. Covers
    /// Mistral, OpenAI Whisper, ElevenLabs Scribe v2, Cohere v2 cloud, and
    /// any externally-managed local server that speaks the same protocol
    /// (Qwen3-ASR, hand-launched CrispASR, …).
    ///
    /// Per-provider quirks live here, gated on <see cref="ApiProvider"/>
    /// fields so the same code services everyone:
    ///   - Auth header (Bearer vs. xi-api-key)
    ///   - Model field name ("model" vs. "model_id")
    ///   - Multipart field ordering (Cohere v2 needs strings BEFORE file)
    ///   - Context biasing routed to each provider's native field (prompt /
    ///     context_bias / keyterms) per ApiProvider.ResolvedBiasMechanism
    ///   - ElevenLabs Scribe keyterms + tag_audio_events + no_verbatim
    /// </summary>
    public sealed class HttpTranscriber : ITranscriber
    {
        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public HttpTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            // Cloud providers fail at request time if the key is wrong; the
            // best we can do up front is flag a totally empty config.
            if (string.IsNullOrWhiteSpace(_provider.ResolvedTranscriptionUrl))
            {
                diagnostic = "Transcription URL not configured";
                return false;
            }
            diagnostic = null;
            return true;
        }

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            if (wavBytes == null || wavBytes.Length == 0) return null;
            string url = _provider.ResolvedTranscriptionUrl;
            _log($"[diag] HttpTranscriber({_provider.Id}): POST {url}");

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);

                if (!string.IsNullOrEmpty(_provider.ApiKey))
                {
                    if (_provider.UsesCustomAuthHeader)
                        request.Headers.Add(_provider.AuthHeaderName, _provider.ApiKey);
                    else
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
                }

                using var content = new MultipartFormDataContent();
                var fileContent = new ByteArrayContent(wavBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

                // ── String fields FIRST (Cohere v2 multipart-ordering quirk) ──
                if (!string.IsNullOrWhiteSpace(_provider.TranscriptionModel))
                    content.Add(new StringContent(_provider.TranscriptionModel), _provider.ResolvedModelField);

                // ElevenLabs rejects a redundant `language` field — they
                // detect language themselves and 422 on the parameter.
                if (!_provider.UsesCustomAuthHeader)
                    content.Add(new StringContent(_provider.Language ?? "en"), "language");

                if (_provider.TranscriptionTemperature.HasValue)
                {
                    content.Add(
                        new StringContent(_provider.TranscriptionTemperature.Value
                            .ToString("0.##", CultureInfo.InvariantCulture)),
                        "temperature");
                }

                // ── Context biasing: route the shared bias-terms list to this
                // provider's NATIVE field. ResolvedBiasMechanism is baked per
                // provider (the user never picks it). ────────────────────────
                switch (_provider.ResolvedBiasMechanism)
                {
                    case "mistral_context_bias" when biasTerms is { Count: > 0 }:
                        // Mistral Voxtral batch: comma-joined, NO space, <=100 terms.
                        // (The API schema also lists array<string>; the documented
                        // examples use this comma string form, so prefer it.)
                        content.Add(new StringContent(string.Join(",", biasTerms.Take(100))), "context_bias");
                        break;

                    case "whisper_prompt" when biasTerms is { Count: > 0 }:
                        // OpenAI Whisper / local prompt-conditioned servers. A labeled
                        // glossary primes rare vocabulary better than a bare list (and
                        // avoids the Qwen3 "list-dictation" regression).
                        content.Add(new StringContent("Glossary: " + string.Join(", ", biasTerms) + "."), "prompt");
                        break;

                    case "elevenlabs_keyterms":
                    {
                        // ElevenLabs Scribe v2 keyterms (repeated form fields, FastAPI
                        // List[str]). Sourced from the SHARED Context Bias list — the one
                        // place the user enters vocabulary — merged with any provider-only
                        // extras still in ScribeKeytermsRaw.
                        var merged = new List<string>();
                        if (biasTerms != null) merged.AddRange(biasTerms);
                        if (!string.IsNullOrWhiteSpace(_provider.ScribeKeytermsRaw))
                            merged.AddRange(_provider.ScribeKeytermsRaw
                                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
                        if (merged.Count > 0)
                        {
                            var keyterms = ApiProvider.ValidateKeyterms(merged, out var ktWarnings);
                            foreach (var w in ktWarnings) _log($"[keyterms] {w}");
                            if (keyterms.Count > 0)
                            {
                                _log($"[keyterms] sending {keyterms.Count} terms");
                                foreach (var term in keyterms)
                                    content.Add(new StringContent(term), "keyterms");
                            }
                        }
                        break;
                    }

                    // "none" (incl. Cohere v2 — no native biasing field exists) sends nothing.
                }

                // ElevenLabs Scribe v2 — tag_audio_events / no_verbatim.
                // Always emitted (when ElevenLabs) because the API defaults
                // are wrong for clinical dictation; our config values must win.
                if (_provider.UsesCustomAuthHeader)
                {
                    content.Add(new StringContent(_provider.TagAudioEvents ? "true" : "false"), "tag_audio_events");
                    content.Add(new StringContent(_provider.NoVerbatim ? "true" : "false"), "no_verbatim");
                    _log($"[scribe] tag_audio_events={_provider.TagAudioEvents} no_verbatim={_provider.NoVerbatim}");
                }

                // File LAST — Cohere v2 rejects any string field that appears
                // after the file part.
                content.Add(fileContent, "file", "audio.wav");
                request.Content = content;

                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                int previewLen = Math.Min(500, body.Length);
                _log($"[{_provider.Id}] HTTP {(int)response.StatusCode}: {body[..previewLen]}");
                if (!response.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textEl))
                    return textEl.GetString();
                return null;
            }
            catch (Exception ex)
            {
                _log($"HttpTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
