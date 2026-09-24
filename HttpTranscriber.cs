using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    ///   - ElevenLabs Scribe: language_code, temperature 0, one speaker, word
    ///     timestamps, keyterms, tag_audio_events, no_verbatim — the request
    ///     shape elevenlabs-web runs in production — plus its transcript
    ///     cleanup and the word timing for the incomplete-transcript check
    /// </summary>
    public sealed class HttpTranscriber : ITranscriber, ITranscriptCoverage
    {
        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public double? LastWordEndSeconds { get; private set; }
        public double? DecodedAudioSeconds { get; private set; }

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
            LastWordEndSeconds = null;
            DecodedAudioSeconds = null;
            if (wavBytes == null || wavBytes.Length == 0) return null;
            string url = _provider.ResolvedTranscriptionUrl;
            bool eleven = _provider.IsElevenLabs;
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

                string language = string.IsNullOrWhiteSpace(_provider.Language) ? "en" : _provider.Language.Trim();
                if (eleven)
                {
                    // ElevenLabs calls it language_code (it 422s on a bare
                    // `language`). Pinned rather than detected: detection on a
                    // short take is where Scribe drifts into another language.
                    // "auto" omits it, which is how Scribe is asked to detect.
                    if (!string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
                        content.Add(new StringContent(language), "language_code");
                }
                else
                {
                    content.Add(new StringContent(language), "language");
                }

                // ElevenLabs gets temperature 0 unless one is configured: the
                // most deterministic decode, and what elevenlabs-web sends on
                // every dictation. Everyone else keeps the endpoint default.
                double? temperature = _provider.TranscriptionTemperature ?? (eleven ? 0.0 : null);
                if (temperature.HasValue)
                {
                    content.Add(
                        new StringContent(temperature.Value.ToString("0.##", CultureInfo.InvariantCulture)),
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
                        // place the user enters vocabulary — merged with the ElevenLabs-only
                        // list in ScribeKeytermsRaw. Long specialty lists belong in the
                        // latter: the shared list reaches every provider, most of which
                        // cap it at 100 terms and some of which splice it into a prompt.
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

                if (eleven)
                {
                    // A dictation is one voice. Without these Scribe may split
                    // one speaker into several or chase a voice in the room;
                    // elevenlabs-web's desktop surface sends exactly this.
                    content.Add(new StringContent("false"), "diarize");
                    content.Add(new StringContent("1"), "num_speakers");
                    // Word timestamps on every take: the incomplete-transcript
                    // check compares where the last word ends with where the
                    // mic last heard speech (see TranscriptCoverage).
                    content.Add(new StringContent("word"), "timestamps_granularity");
                    // Always emitted because the API defaults are wrong for
                    // clinical dictation; our config values must win.
                    content.Add(new StringContent(_provider.TagAudioEvents ? "true" : "false"), "tag_audio_events");
                    content.Add(new StringContent(_provider.NoVerbatim ? "true" : "false"), "no_verbatim");
                    _log($"[scribe] language_code={(string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase) ? "(auto)" : language)} temperature={temperature?.ToString(CultureInfo.InvariantCulture)} diarize=false num_speakers=1 timestamps=word tag_audio_events={_provider.TagAudioEvents} no_verbatim={_provider.NoVerbatim}");
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
                if (!doc.RootElement.TryGetProperty("text", out var textEl))
                    return null;
                string? text = textEl.GetString();
                if (!eleven) return text;

                ReadWordTiming(doc.RootElement);
                return CleanElevenLabsText(text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The per-take deadline (MainWindow logs it as the error).
                _log($"HttpTranscriber({_provider.Id}): stopped at the take's deadline");
                return null;
            }
            catch (Exception ex)
            {
                _log($"HttpTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Where the transcript's last word ends, and how much audio
        /// the service says it decoded, from an ElevenLabs response. Only
        /// "word" entries count: "spacing" entries carry timestamps too, but
        /// only a word says how far the transcript actually got. Anything
        /// missing stays null, which makes the coverage check a no-op rather
        /// than a false alarm.</summary>
        private void ReadWordTiming(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("audio_duration_secs", out var dur) && dur.ValueKind == JsonValueKind.Number)
                    DecodedAudioSeconds = dur.GetDouble();

                if (root.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
                {
                    double last = 0;
                    foreach (var w in words.EnumerateArray())
                    {
                        if (w.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                            && !string.Equals(type.GetString(), "word", StringComparison.Ordinal))
                            continue;
                        if (w.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.Number)
                        {
                            double e = end.GetDouble();
                            if (double.IsFinite(e) && e > last) last = e;
                        }
                    }
                    if (last > 0) LastWordEndSeconds = last;
                }
                _log($"[scribe] last word ends at {Fmt(LastWordEndSeconds)} s; decoded {Fmt(DecodedAudioSeconds)} s of audio");
            }
            catch (Exception ex) { _log($"[scribe] word timing unreadable: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static string Fmt(double? s) => s?.ToString("F1", CultureInfo.InvariantCulture) ?? "?";

        private static readonly Regex Ellipsis = new(@"\.{3,}", RegexOptions.Compiled);
        private static readonly Regex LineBreaks = new(@"[\r\n]+", RegexOptions.Compiled);
        private static readonly Regex Spaces = new(@" {2,}", RegexOptions.Compiled);
        private static readonly Regex SpaceBeforePunctuation = new(@" ([,.;:!?])", RegexOptions.Compiled);

        /// <summary>The cleanup elevenlabs-web applies to every transcript
        /// before it is pasted (its cleanTranscript). Scribe renders a
        /// dictation pause as an ellipsis — "…" or "..." — and can break a
        /// long take into lines; pasted into a chart those arrive as stray
        /// dots and line breaks. Pauses and line breaks become single spaces,
        /// runs of spaces collapse, and a space left in front of punctuation
        /// is dropped. The paste path adds its own leading space.</summary>
        internal static string CleanElevenLabsText(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string t = raw.Replace('…', ' ');
            t = Ellipsis.Replace(t, " ");
            t = LineBreaks.Replace(t, " ");
            t = Spaces.Replace(t, " ").Trim();
            return SpaceBeforePunctuation.Replace(t, "$1");
        }

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
