// GoogleChirp3Transcriber.cs
// Cloud transcription via Google Cloud Speech-to-Text v2 (Chirp 3 model).
//
// Required NuGet:
//   dotnet add package Google.Apis.Auth   (for OAuth bearer-token exchange)
//
// Auth: service-account JSON. The ApiKey field of the WhisperInk provider
// holds *either* the absolute path to a .json key file *or* the raw JSON
// contents pasted in. Project ID is parsed out of the JSON automatically.
//
// Endpoint (regional only — there is no global endpoint):
//   POST https://{REGION}-speech.googleapis.com/v2/projects/{PROJECT_ID}/locations/{REGION}/recognizers/_:recognize
//
// Body (JSON) with base64-inlined audio. Adaptation phrase hints flow in
// when callers pass non-null phraseHints — this is how WhisperInk's global
// _contextBiasTerms get real biasing on Google, on par with Cohere cloud
// and ElevenLabs Scribe v2.
//
// Sync recognize is capped at ~60 seconds of audio per Google docs. Anything
// longer needs the BatchRecognize endpoint, which is out of scope for v1.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;

namespace WhisperInk
{
    public class GoogleChirp3Transcriber : ITranscriber
    {
        private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
        private const double SyncWarnSeconds = 55.0;  // warn before Google's 60s sync cap

        private readonly HttpClient _http;
        private readonly string _saJsonOrPath;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _region;
        private readonly string? _projectId;
        private readonly GoogleCredential? _credential;
        private readonly string? _initError;
        private readonly ApiProvider? _provider;
        private readonly Action<string> _log;

        /// <summary>True if credentials parsed cleanly and the transcriber is ready to call.</summary>
        public bool IsCredentialReady => _initError == null && _credential != null && !string.IsNullOrEmpty(_projectId);

        /// <summary>Reason the transcriber failed to initialize, if any.</summary>
        public string? LastError => _initError;

        public string DisplayName => _provider?.Name ?? "Google Chirp 3";

        /// <summary>Factory-friendly constructor used by <see cref="TranscriberFactory"/>.</summary>
        public GoogleChirp3Transcriber(ApiProvider provider, Action<string> log)
            : this(provider?.ApiKey ?? "", provider?.BaseUrl ?? "", provider?.TranscriptionModel ?? "chirp_3")
        {
            _provider = provider;
            _log = log ?? (_ => { });
        }

        public bool IsReady(out string? diagnostic)
        {
            if (!IsCredentialReady)
            {
                diagnostic = _initError ?? "credentials not loaded";
                return false;
            }
            diagnostic = null;
            return true;
        }

        public async Task<string?> TranscribeAsync(byte[] wavBytes, IReadOnlyList<string> biasTerms, CancellationToken ct = default)
        {
            try
            {
                var phraseHints = biasTerms is { Count: > 0 } ? new List<string>(biasTerms) : null;
                return await TranscribeAsync(wavBytes, _provider?.Language ?? "en", phraseHints).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log($"GoogleChirp3 transcribe failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        public GoogleChirp3Transcriber(string saJsonOrPath, string baseUrl, string model)
        {
            _log = _ => { };
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _saJsonOrPath = saJsonOrPath ?? "";
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "chirp_3" : model;
            _region = ExtractRegion(_baseUrl);

            try
            {
                string json = LoadSaJson(_saJsonOrPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _initError = "service account JSON is empty";
                    return;
                }

                using (var doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("project_id", out var pidEl))
                    {
                        _initError = "service account JSON missing project_id";
                        return;
                    }
                    _projectId = pidEl.GetString();
                }

                _credential = GoogleCredential.FromJson(json).CreateScoped(Scope);
            }
            catch (JsonException jex) { _initError = $"invalid JSON: {jex.Message}"; }
            catch (Exception ex)      { _initError = $"{ex.GetType().Name}: {ex.Message}"; }
        }

        /// <summary>True if the credentials, base URL, or model changed since construction.</summary>
        public bool NeedsReinit(string saJsonOrPath, string baseUrl, string model)
            => !string.Equals(_saJsonOrPath, saJsonOrPath ?? "", StringComparison.Ordinal)
            || !string.Equals(_baseUrl, (baseUrl ?? "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_model, string.IsNullOrWhiteSpace(model) ? "chirp_3" : model, StringComparison.Ordinal);

        public async Task<string?> TranscribeAsync(byte[] wavBytes, string languageShortCode, System.Collections.Generic.List<string>? phraseHints)
        {
            if (!IsCredentialReady) throw new InvalidOperationException($"Google Chirp 3 not ready: {_initError}");
            if (wavBytes == null || wavBytes.Length == 0) return null;

            string bcp47 = MapToBcp47(languageShortCode);

            // Sync recognize is capped at ~60s. We don't reject — Google returns
            // a clear 400 if exceeded — but we surface a warning so the user
            // can see it in debug.log before the error fires.
            double durSec = TryGetWavDurationSeconds(wavBytes);
            if (durSec > SyncWarnSeconds)
                _log($"GoogleChirp3 WARNING: audio is {durSec:F1}s; sync recognize caps at ~60s and may 400.");

            // Build JSON body
            var configNode = new JsonObject
            {
                ["auto_decoding_config"] = new JsonObject(),
                ["model"] = _model,
                ["language_codes"] = new JsonArray(bcp47),
            };

            if (phraseHints != null && phraseHints.Count > 0)
            {
                var phrases = new JsonArray();
                foreach (var p in phraseHints)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    phrases.Add(new JsonObject { ["value"] = p });
                }
                if (phrases.Count > 0)
                {
                    configNode["adaptation"] = new JsonObject
                    {
                        ["phraseSets"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["inlinePhraseSet"] = new JsonObject { ["phrases"] = phrases }
                            }
                        }
                    };
                }
            }

            var body = new JsonObject
            {
                ["config"] = configNode,
                ["content"] = Convert.ToBase64String(wavBytes),
            };

            string url = $"{_baseUrl}/v2/projects/{_projectId}/locations/{_region}/recognizers/_:recognize";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            string accessToken = await _credential!.UnderlyingCredential.GetAccessTokenForRequestAsync();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _http.SendAsync(request);
            string responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _log($"GoogleChirp3 HTTP {(int)response.StatusCode}: {responseString[..Math.Min(500, responseString.Length)]}");
                return null;
            }

            return ParseTranscript(responseString);
        }

        private static string? ParseTranscript(string responseString)
        {
            using var doc = JsonDocument.Parse(responseString);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;

            var sb = new StringBuilder();
            foreach (var r in results.EnumerateArray())
            {
                if (!r.TryGetProperty("alternatives", out var alts) || alts.ValueKind != JsonValueKind.Array || alts.GetArrayLength() == 0)
                    continue;
                if (alts[0].TryGetProperty("transcript", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }
            string s = sb.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static string LoadSaJson(string saJsonOrPath)
        {
            if (string.IsNullOrWhiteSpace(saJsonOrPath)) return "";
            string trimmed = saJsonOrPath.Trim();
            // Heuristic: a JSON SA key starts with '{'. Anything else, treat as a path.
            if (trimmed.StartsWith('{')) return trimmed;
            if (File.Exists(trimmed)) return File.ReadAllText(trimmed);
            return trimmed;  // let JSON parse fail with a clear message
        }

        // BaseUrl examples that we accept:
        //   https://us-speech.googleapis.com   → "us"
        //   https://eu-speech.googleapis.com   → "eu"
        // Falls back to "us" if the host doesn't match the regional pattern.
        private static string ExtractRegion(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return "us";
            try
            {
                var u = new Uri(baseUrl);
                string host = u.Host;  // e.g. us-speech.googleapis.com
                int dash = host.IndexOf("-speech.googleapis.com", StringComparison.OrdinalIgnoreCase);
                if (dash > 0) return host[..dash];
            }
            catch { }
            return "us";
        }

        // WhisperInk's language dropdown stores BCP-47 short codes ("en", "es").
        // Chirp 3 needs region-qualified codes ("en-US"). Map the common ones;
        // pass through values that already contain a hyphen (already BCP-47);
        // fall back to "auto" for anything unknown so we don't 400 on weird input.
        private static string MapToBcp47(string? shortCode)
        {
            if (string.IsNullOrWhiteSpace(shortCode)) return "en-US";
            string s = shortCode.Trim();
            if (s.Contains('-')) return s;
            return s.ToLowerInvariant() switch
            {
                "en" => "en-US",
                "es" => "es-US",
                "fr" => "fr-FR",
                "de" => "de-DE",
                "it" => "it-IT",
                "pt" => "pt-BR",
                "ja" => "ja-JP",
                "ko" => "ko-KR",
                "zh" => "cmn-Hans-CN",
                "auto" => "auto",
                _ => "auto",
            };
        }

        private static double TryGetWavDurationSeconds(byte[] wavBytes)
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var reader = new NAudio.Wave.WaveFileReader(ms);
                return reader.TotalTime.TotalSeconds;
            }
            catch { return 0; }
        }

        public void Dispose() => _http.Dispose();
    }
}
