// ZoomScribeTranscriber.cs
// Cloud transcription via Zoom's Scribe API (Zoom AI Services, the Build
// platform), Fast mode: one synchronous request per take, the transcript in
// the answer. Not to be confused with ElevenLabs Scribe.
// Endpoint: https://api.zoom.us/v2/aiservices/scribe/transcribe
// Docs: developers.zoom.us/docs/ai-services/scribe/
//
// ── The request ───────────────────────────────────────────────────────────
// Documented: a JSON body {"file": <URL or data URL>, "config": {…}}, with the
// body capped at 2 MB when the audio is a data URL. Measured live on
// 2026-10-30 (_scratch\zoom\zoom-probe.ps1):
//   - a multipart upload works too: a `config` field holding the same JSON,
//     then the WAV as `file`. That's what this class sends: no base64, a
//     quarter smaller. It's undocumented, so if Zoom ever refuses that shape
//     (a 415, or a 400 UNSUPPORTED_CONTENT_TYPE / CODEC / UNSUPPORTED_MEDIA)
//     the take is sent once more the documented way, as a data URL;
//   - the 2 MB cap isn't enforced: a 111 s take went as a 4.7 MB data URL;
//   - a raw WAV body is refused (400 CODEC), and so is base64 with no `data:`
//     prefix;
//   - `language` must be a locale: "en" is a 400 UNSUPPORTED_LANGUAGE, so the
//     house two-letter codes are mapped (en → en-US);
//   - Bearer and x-api-key both take the Platform Studio API key; Bearer also
//     takes a JWT, so that's what is sent.
//
// ── Zoom's voice-activity detection is OFF, so takes over 29 s are cut here ─
// With segmentation_mode at its default ("auto"), Zoom's own voice-activity
// detection decides where speech is, and it clipped a take's first word:
// "Naloxone 0.4 mg IV …" came back as "0.4 milligram IV …", with or without
// the list, and right with "none" (the read-aloud set, 2026-10-30). So every
// request says "none", and an empty answer is asked for again with it on:
// "none" returned nothing for three 2.7–3.1 s takes that "auto" got right
// (both deterministic). Off-then-on scored 3.4 % on the real dictations (on
// alone 5.1 %) and 2.5 % on the read-aloud set (off alone 3.7 %), with the
// first word kept. Zoom refuses "none" for 30.0 s of audio or more (29.96 s
// accepted, 30.16 s refused: "Raw data size exceeds max audio segment
// duration after disabling VAD"), so a longer take is cut at its own pauses
// by LocalTakeSplitter (the cutter the Qwen3 preset uses), into pieces of at
// most 29 s that each hold speech. The pieces go out at once and their texts
// are joined. One failed piece fails the take, which stays journaled. A long
// take that can't be cut (no speech found, or not 16-bit mono) goes out whole
// with Zoom's detection on, since "none" would only be refused.
//
// ── Models ────────────────────────────────────────────────────────────────
// zoom-scribe-en (standard) and zoom-scribe-en-pro ("Scribe API Pro
// High-Accuracy", premium access). On the owner's six clips both got every
// hard term, even with no list, but the standard model deleted "rectum" from
// "bright red blood per rectum" (leaving "per ,") and wrote "ureterocolic" for
// "ureteral colic"; Pro got both right every time, so the preset ships Pro. A
// non-English language with an English model would be a 400, so the model is
// then left to Zoom.
//
// ── Also left out, deliberately ───────────────────────────────────────────
//   Text Polishing is never asked for: it's an LLM rewrite ("condense",
//     "reorganize spoken thoughts"), the post-processing ruled out for
//     clinical text.
//   diarization, channel_separation and profanity_filter stay off.
// word_time_offsets is on: the last word's end and the audio sent feed the
// incomplete-transcript check (ITranscriptCoverage).
//
// ── Vocabulary ────────────────────────────────────────────────────────────
// The shared Context Bias list goes as config.vocabulary.phrases: at most 100
// (Zoom's cap for an inline list), deduplicated.
//
// ── Errors ────────────────────────────────────────────────────────────────
// {"code": 400, "reason": "UNSUPPORTED_LANGUAGE", "message": …}, logged with
// the reason and a hint. Any failure fails the take, which stays journaled for
// ↻ retry.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperInk
{
    public sealed class ZoomScribeTranscriber : ITranscriber, ITranscriptCoverage
    {
        // Public so AppConfig.CreateDefaults builds its preset from the same
        // strings this class posts to.
        public const string DefaultBaseUrl = "https://api.zoom.us";
        public const string PathTranscribe = "/v2/aiservices/scribe/transcribe";
        public const string ModelStandard = "zoom-scribe-en";
        public const string ModelPro = "zoom-scribe-en-pro";

        /// <summary>The longest audio sent in one request. Zoom refuses 30.0 s
        /// or more with its voice-activity detection off; a second's margin.</summary>
        public static readonly TimeSpan MaxPiece = TimeSpan.FromSeconds(29);

        /// <summary>Zoom's ceiling for an inline vocabulary.</summary>
        private const int MaxPhrases = 100;

        /// <summary>The settings dialog's two-letter codes as the locales Zoom
        /// wants. A code with a region ("en-GB") is sent as it is.</summary>
        private static readonly Dictionary<string, string> Locales = new(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "en-US", ["es"] = "es-ES", ["fr"] = "fr-FR", ["de"] = "de-DE", ["it"] = "it-IT",
            ["pt"] = "pt-BR", ["nl"] = "nl-NL", ["ja"] = "ja-JP", ["ko"] = "ko-KR", ["zh"] = "zh-CN",
            ["ru"] = "ru-RU", ["ar"] = "ar-SA", ["hi"] = "hi-IN",
        };

        /// <summary>400 reasons that mean "not this upload shape", for which the
        /// documented data URL is worth one more try.</summary>
        private static readonly HashSet<string> UploadRefusals = new(StringComparer.OrdinalIgnoreCase)
        {
            "UNSUPPORTED_CONTENT_TYPE", "CODEC", "UNSUPPORTED_MEDIA",
        };

        /// <summary>One request's answer: the text, its last real word's end and
        /// the seconds Zoom says it decoded, both from the start of the audio
        /// that request carried.</summary>
        private sealed record Answer(string Text, double? LastWordEnd, double? Duration);

        private readonly ApiProvider _provider;
        private readonly HttpClient _http;
        private readonly Action<string> _log;

        public string DisplayName => _provider.Name;

        public double? LastWordEndSeconds { get; private set; }
        public double? DecodedAudioSeconds { get; private set; }

        public ZoomScribeTranscriber(ApiProvider provider, HttpClient http, Action<string> log)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (string.IsNullOrWhiteSpace(_provider.ApiKey))
            {
                diagnostic = "Zoom API key not set";
                return false;
            }
            diagnostic = null;
            return true;
        }

        private string Endpoint =>
            string.IsNullOrWhiteSpace(_provider.TranscriptionEndpoint)
                ? (string.IsNullOrWhiteSpace(_provider.BaseUrl) ? DefaultBaseUrl : _provider.BaseUrl.TrimEnd('/')) + PathTranscribe
                : _provider.TranscriptionEndpoint;

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            LastWordEndSeconds = null;
            DecodedAudioSeconds = null;
            if (wavBytes == null || wavBytes.Length == 0) return null;

            try
            {
                var (locale, model) = ResolveLanguageAndModel();
                var phrases = BuildPhrases(biasTerms);

                var split = LocalTakeSplitter.Cut(wavBytes, MaxPiece);
                if (split == null)
                {
                    double seconds = WavSeconds(wavBytes);
                    bool fits = seconds <= MaxPiece.TotalSeconds;
                    if (!fits)
                        _log($"[zoom] {seconds:F1} s take with no pause to cut at: sent whole, with Zoom's own voice-activity detection");
                    var one = await SendPieceAsync(wavBytes, locale, model, phrases, vadOff: fits, ct).ConfigureAwait(false);
                    if (one == null) return null;
                    LastWordEndSeconds = one.LastWordEnd;
                    DecodedAudioSeconds = one.Duration;
                    return one.Text;
                }

                var lengths = split.Pieces.Select(p => $"{split.Seconds(p.Length):F1}").ToList();
                string left = (split.TrailingSilence > 0 ? $"; {split.Seconds(split.TrailingSilence):F1} s of silence after the last speech left out" : "")
                            + (split.SilentStretches > 0 ? $"; {split.SilentStretches} stretch(es) with no speech left out" : "");
                _log(split.Pieces.Count == 1
                    ? $"[zoom] {split.Seconds(split.TotalSamples):F1} s take sent as one {lengths[0]} s piece{left}"
                    : $"[zoom] {split.Seconds(split.TotalSamples):F1} s take sent in {split.Pieces.Count} pieces cut at pauses ({string.Join(" + ", lengths)} s){left}");

                var answers = await Task.WhenAll(split.Wavs.Select(w => SendPieceAsync(w, locale, model, phrases, vadOff: true, ct))).ConfigureAwait(false);
                if (answers.Any(a => a == null))
                {
                    _log($"[zoom] {answers.Count(a => a == null)} of {answers.Length} pieces failed, so the take did");
                    return null;
                }

                // Times are per piece; the coverage check wants them in the take's own time.
                for (int i = 0; i < answers.Length; i++)
                    if (answers[i]!.LastWordEnd is double end)
                        LastWordEndSeconds = split.Seconds(split.Pieces[i].Start) + end;
                if (answers[^1]!.Duration is double decoded)
                    DecodedAudioSeconds = split.Seconds(split.Pieces[^1].Start) + decoded;
                return string.Join(" ", answers.Select(a => a!.Text).Where(t => t.Length > 0));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The per-take deadline (MainWindow logs it as the error).
                _log($"ZoomScribeTranscriber({_provider.Id}): stopped at the take's deadline");
                return null;
            }
            catch (OperationCanceledException ex)
            {
                // Not our token: the HttpClient's own timeout.
                _log($"ZoomScribeTranscriber({_provider.Id}): HTTP request timed out: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _log($"ZoomScribeTranscriber({_provider.Id}) error: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>One piece of audio (or a whole take). With Zoom's detection
        /// off, an empty answer is asked for again with it on: "none" returned
        /// nothing, every time, for three 2.7–3.1 s takes that "auto" got right,
        /// while "auto" deleted the first word of another. The two fail on
        /// different takes, so off-then-on matched the better of the two on both
        /// corpora (2026-10-30). A cough costs one more request.</summary>
        private async Task<Answer?> SendPieceAsync(byte[] wav, string? locale, string? model, List<string> phrases, bool vadOff, CancellationToken ct)
        {
            var answer = await SendAsync(wav, ConfigJson(locale, model, phrases, vadOff), ct).ConfigureAwait(false);
            if (!vadOff || answer is not { Text.Length: 0 }) return answer;

            _log("[zoom] nothing came back with Zoom's voice-activity detection off; asking again with it on");
            return await SendAsync(wav, ConfigJson(locale, model, phrases, vadOff: false), ct).ConfigureAwait(false) ?? answer;
        }

        /// <summary>One request: the multipart upload, or the documented data
        /// URL if Zoom refuses that shape. Null on any failure, logged.</summary>
        private async Task<Answer?> SendAsync(byte[] wav, string config, CancellationToken ct)
        {
            var (status, body) = await PostAsync(BuildUpload(wav, config), ct).ConfigureAwait(false);
            if (IsUploadRefusal(status, body))
            {
                _log($"[zoom] the file upload was refused ({DescribeError(status, body)}); sending the take as a data URL instead");
                (status, body) = await PostAsync(BuildDataUrl(wav, config), ct).ConfigureAwait(false);
            }

            if (status is < 200 or > 299)
            {
                _log($"[zoom] {DescribeError(status, body)}");
                return null;
            }
            return ReadAnswer(body);
        }

        /// <summary>The `config` object: language (or one-time detection),
        /// model, voice-activity detection off, word timing and the list.</summary>
        private static string ConfigJson(string? locale, string? model, List<string> phrases, bool vadOff)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                if (locale != null)
                    w.WriteString("language", locale);
                else
                {
                    w.WriteStartObject("language_detection");
                    w.WriteString("mode", "once");
                    w.WriteEndObject();
                }
                if (model != null) w.WriteString("model", model);
                if (vadOff) w.WriteString("segmentation_mode", "none");
                w.WriteBoolean("word_time_offsets", true);
                if (phrases.Count > 0)
                {
                    w.WriteStartObject("vocabulary");
                    w.WriteStartArray("phrases");
                    foreach (var p in phrases) w.WriteStringValue(p);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>The locale to send (null: let Zoom detect it once) and the
        /// model (null: let Zoom choose). Detection picks its own model, and an
        /// English model can't take another language.</summary>
        private (string? Locale, string? Model) ResolveLanguageAndModel()
        {
            string lang = (_provider.Language ?? "").Trim();
            string model = string.IsNullOrWhiteSpace(_provider.TranscriptionModel) ? ModelPro : _provider.TranscriptionModel.Trim();

            if (lang.Length == 0 || lang.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                _log($"[zoom] language auto: Zoom detects it and picks the model, so {model} isn't sent");
                return (null, null);
            }

            // An unknown code goes as it is: Zoom's 400 lists the locales it takes.
            string locale = lang.Contains('-') ? lang : Locales.TryGetValue(lang, out var l) ? l : lang;
            if (!locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                && model.StartsWith(ModelStandard, StringComparison.OrdinalIgnoreCase))
            {
                _log($"[zoom] {model} is English only; for {locale} Zoom picks the model");
                return (locale, null);
            }
            return (locale, model);
        }

        private List<string> BuildPhrases(IReadOnlyList<string> biasTerms)
        {
            var kept = new List<string>();
            if (biasTerms is not { Count: > 0 }) return kept;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in biasTerms)
            {
                string term = (raw ?? "").Trim();
                if (term.Length == 0 || !seen.Add(term)) continue;
                if (kept.Count >= MaxPhrases)
                {
                    _log($"[zoom] vocabulary truncated to {MaxPhrases} phrases (had {biasTerms.Count})");
                    break;
                }
                kept.Add(term);
            }
            return kept;
        }

        private HttpRequestMessage BuildUpload(byte[] wavBytes, string config)
        {
            var request = NewRequest();
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(config, Encoding.UTF8, "application/json"), "config");
            var file = new ByteArrayContent(wavBytes);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
            form.Add(file, "file", "audio.wav");
            request.Content = form;
            return request;
        }

        /// <summary>The documented shape: the WAV inline as a data URL.</summary>
        private HttpRequestMessage BuildDataUrl(byte[] wavBytes, string config)
        {
            var request = NewRequest();
            string body = "{\"file\":\"data:audio/wav;base64," + Convert.ToBase64String(wavBytes) + "\",\"config\":" + config + "}";
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        private HttpRequestMessage NewRequest()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _provider.ApiKey);
            return request;
        }

        private async Task<(int Status, string Body)> PostAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using (request)
            using (var resp = await _http.SendAsync(request, ct).ConfigureAwait(false))
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        private static bool IsUploadRefusal(int status, string body) =>
            status == 415 || (status == 400 && Reason(body) is string r && UploadRefusals.Contains(r));

        /// <summary>result.text_display, trimmed; "" when Zoom heard nothing (a
        /// real answer, not a failure), with the last real word's end.</summary>
        private Answer? ReadAnswer(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("text_display", out var text) || text.ValueKind != JsonValueKind.String)
            {
                _log($"[zoom] the response had no transcript: {Preview(json)}");
                return null;
            }

            // Punctuation comes back as its own zero-length "word"; only real words count.
            double? last = null;
            if (result.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array)
                foreach (var seg in segments.EnumerateArray())
                {
                    if (!seg.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array) continue;
                    foreach (var w in words.EnumerateArray())
                    {
                        if (!w.TryGetProperty("end", out var end) || end.ValueKind != JsonValueKind.Number) continue;
                        if (!w.TryGetProperty("word", out var word) || word.ValueKind != JsonValueKind.String) continue;
                        if ((word.GetString() ?? "").Any(char.IsLetterOrDigit) && (last == null || end.GetDouble() > last))
                            last = end.GetDouble();
                    }
                }

            double? duration = root.TryGetProperty("duration_sec", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
            return new Answer((text.GetString() ?? "").Trim(), last, duration);
        }

        /// <summary>Seconds of audio in a WAV, from its header; 0 if it can't be read.</summary>
        private static double WavSeconds(byte[] wav)
        {
            int at = 12, rate = 0, block = 0;
            while (at + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                if (size < 0) break;
                if (id == "fmt " && at + 22 <= wav.Length)
                {
                    rate = BitConverter.ToInt32(wav, at + 12);
                    block = BitConverter.ToInt16(wav, at + 20);
                }
                else if (id == "data")
                    return rate > 0 && block > 0 ? Math.Min(size, wav.Length - at - 8) / (double)(rate * block) : 0;
                at += 8 + size + (size & 1);
            }
            return 0;
        }

        /// <summary>"HTTP 400: [UNSUPPORTED_LANGUAGE] message (hint)".</summary>
        private static string DescribeError(int status, string body)
        {
            string? reason = Reason(body);
            string message = Field(body, "message") ?? Preview(body);
            string hint = status switch
            {
                401 => " (check the Zoom API key in Provider Settings)",
                403 => " (this key lacks the Scribe permission, or Pro access for zoom-scribe-en-pro; see platform.zoom.us)",
                413 => " (audio too large for Zoom)",
                429 => " (Zoom's rate limit; the take is kept for ↻ retry)",
                _ => "",
            };
            return reason == null ? $"HTTP {status}: {message}{hint}" : $"HTTP {status}: [{reason}] {message}{hint}";
        }

        private static string? Reason(string body) => Field(body, "reason");

        private static string? Field(string body, string name)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() : null;
            }
            catch (JsonException) { return null; }
        }

        private static string Preview(string body) =>
            body.Length <= 500 ? body : body[..500];

        public void Dispose() { /* HttpClient owned by caller */ }
    }
}
