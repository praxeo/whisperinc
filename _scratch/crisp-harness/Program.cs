// Exercises the shipping transcription code without the app, asserting on
// results and on what reaches the log:
//   0. pure logic: TranscriptionDeadline, the ElevenLabs text cleanup,
//      TranscriptCoverage, UnsentTakes
//   1. HttpTranscriber against a local fake server: the ElevenLabs request
//      shape, response parsing, and the per-take deadline
//   2. CrispAsrServerTranscriber against the REAL crispasr.exe (CPU only,
//      unused ports)
// `dotnet run -- fast` skips section 2 and the slower-than-15s server check.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WhisperInk;

bool fast = args.Contains("fast");
string dir = AppContext.BaseDirectory;
var log = new List<string>();
void Log(string s) { lock (log) log.Add(s); Console.WriteLine("  LOG| " + s.Replace("\n", "\n  LOG|   ")); }
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }
bool Logged(string pattern) { lock (log) return log.Any(l => Regex.IsMatch(l, pattern, RegexOptions.Singleline)); }

byte[] speech = File.ReadAllBytes(Path.Combine(dir, "speech.wav"));

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0a. TranscriptionDeadline ==");
var cloudProv = new ApiProvider { Id = "c", TranscriptionEndpoint = "https://api.elevenlabs.io/v1/speech-to-text" };
var localProv = new ApiProvider { Id = "l", TranscriberKind = TranscriberKind.LocalCrispAsrServer };
var loopProv = new ApiProvider { Id = "lb", BaseUrl = "http://127.0.0.1:8102" };
Check(TranscriptionDeadline.For(cloudProv, 0) == TimeSpan.FromSeconds(20), "cloud floor: 20 s for an empty take");
Check(TranscriptionDeadline.For(cloudProv, 60) == TimeSpan.FromSeconds(40), "cloud: 20 s + 20 s per minute (1 min of audio -> 40 s)");
Check(TranscriptionDeadline.For(cloudProv, 180) == TimeSpan.FromSeconds(80), "cloud: 3 min of audio -> 80 s (the old flat 15 s failed this)");
Check(TranscriptionDeadline.For(cloudProv, 3600) == TimeSpan.FromMinutes(15), "cloud: capped at 15 min");
Check(TranscriptionDeadline.For(localProv, 30) == TimeSpan.FromSeconds(210), "local: 180 s + 1x the audio");
Check(TranscriptionDeadline.For(localProv, 1e12) == TimeSpan.FromHours(2), "local: capped at 2 h, and an absurd length can't overflow");
Check(TranscriptionDeadline.For(loopProv, 60) == TimeSpan.FromSeconds(240), "an Http provider on loopback gets the LOCAL budget");
Check(TranscriptionDeadline.For(cloudProv, double.NaN) == TimeSpan.FromSeconds(20), "NaN audio length -> the floor");
Check(TranscriptionDeadline.HttpBackstop > TranscriptionDeadline.LocalCap, "the HttpClient backstop outlasts every deadline");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0b. ElevenLabs transcript cleanup ==");
(string raw, string want)[] cleanCases =
{
    ("The patient \u2026 denies chest pain.", "The patient denies chest pain."),
    ("Abdomen soft... non-tender.", "Abdomen soft non-tender."),
    ("Line one.\nLine two.\r\nLine three.", "Line one. Line two. Line three."),
    ("  extra   spaces  here ", "extra spaces here"),
    ("space before , and . and ?", "space before, and. and?"),
    ("Dr. Smith saw 2.5 mg", "Dr. Smith saw 2.5 mg"),
    ("", ""),
};
foreach (var (raw, want) in cleanCases)
{
    string got = HttpTranscriber.CleanElevenLabsText(raw);
    Check(got == want, $"clean \"{Esc(raw)}\" -> \"{Esc(got)}\"" + (got == want ? "" : $"  (want \"{Esc(want)}\")"));
}

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0c. TranscriptCoverage ==");
double? ls1 = TranscriptCoverage.LastSpeechSeconds(Wav((10, 0.1), (50, 0)));
Check(ls1 is double a1 && Math.Abs(a1 - 10) < 0.1, $"last speech found at the end of the talking, not the end of the hold ({ls1:F2}s, want ~10)");
double? ls2 = TranscriptCoverage.LastSpeechSeconds(Wav((5, 0), (0.01, 0.5), (5, 0)));
Check(ls2 == null, $"a single 10 ms click is not speech ({ls2})");
double? ls3 = TranscriptCoverage.LastSpeechSeconds(Wav((10, 0.002)));
Check(ls3 == null, $"a noise-floor hum is not speech ({ls3})");
double? ls4 = TranscriptCoverage.LastSpeechSeconds(Wav((2, 0), (3, 0.05), (1, 0), (2, 0.05), (4, 0)));
Check(ls4 is double a4 && Math.Abs(a4 - 8) < 0.1, $"the LAST run of speech counts ({ls4:F2}s, want ~8)");
Check(TranscriptCoverage.Check(60, 10, 9.6, null) == null, "complete: last word near the last speech, long silent tail -> no warning");
var sf1 = TranscriptCoverage.Check(60, 60, 20, null);
Check(sf1 is { CoveredSeconds: 20, ExpectedSeconds: 60 }, $"truncated: speech to 60 s, words stop at 20 s -> warning ({sf1?.Describe()})");
Check(TranscriptCoverage.Check(60, 10, 1, null) == null, "within the 20 s slack -> no warning");
Check(TranscriptCoverage.Check(10, 10, 0.5, null) == null, "a short take can never warn");
Check(TranscriptCoverage.Check(200, 200, 160, null) == null, "long take: the 25% fraction dominates the slack (40 s short of 200 s is fine)");
var sf2 = TranscriptCoverage.Check(60, 60, null, 30);
Check(sf2 is { Basis: "audio the service decoded" }, $"service decoded half the audio -> warning ({sf2?.Describe()})");
Check(TranscriptCoverage.Check(60, 60, null, null) == null, "no word timing at all -> no warning, never a false one");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0d. UnsentTakes ==");
string ufolder = Path.Combine(dir, "unsent-test");
if (Directory.Exists(ufolder)) Directory.Delete(ufolder, true);
var store = new UnsentTakes(ufolder, Log);
var elevenStub = new ApiProvider { Id = "elevenlabs", Name = "ElevenLabs Scribe" };

var t1 = store.Begin(speech, elevenStub, 4.3);
Check(store.List().Count == 0, "a take in flight is not listed");
Check(await WaitFor(() => File.Exists(t1.WavPath) && File.Exists(Path.ChangeExtension(t1.WavPath, ".json"))),
      "journaled before the send: WAV + sidecar on disk");
store.Delivered(t1);
Check(await WaitFor(() => !File.Exists(t1.WavPath) && !File.Exists(Path.ChangeExtension(t1.WavPath, ".json"))),
      "delivered -> audio and sidecar deleted");

await Task.Delay(5);
var t2 = store.Begin(speech, elevenStub, 4.3);
store.Keep(t2, UnsentTakes.Failed, "no answer within its 20s deadline");
Check(await WaitFor(() => store.List().Count == 1), "failed -> kept and listed");
var kept = store.List().FirstOrDefault();
Check(kept is { Status: UnsentTakes.Failed, Reason: "no answer within its 20s deadline", ProviderName: "ElevenLabs Scribe" },
      "the kept take carries its status, reason and provider");
Check(kept != null && store.ReadAudio(kept) is { } back && back.SequenceEqual(speech), "kept audio reads back byte-identical");

await Task.Delay(5);
var crashed = new UnsentTakes(ufolder, Log).Begin(speech, elevenStub, 4.3);   // never resolved: the app "crashed"
await WaitFor(() => File.Exists(Path.ChangeExtension(crashed.WavPath, ".json")));
string orphanId = "take-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-999";
File.WriteAllBytes(Path.Combine(ufolder, orphanId + ".wav"), speech);          // a crash between the two writes
string oldId = "take-20200101-000000-000";
File.WriteAllBytes(Path.Combine(ufolder, oldId + ".wav"), speech);
File.WriteAllText(Path.Combine(ufolder, oldId + ".json"),
    $"{{\"Id\":\"{oldId}\",\"RecordedAt\":\"2020-01-01T00:00:00\",\"Status\":\"failed\",\"Reason\":\"old\"}}");

var restarted = new UnsentTakes(ufolder, Log);   // next launch
int waiting = restarted.Recover();
var afterRestart = restarted.List();
Check(waiting == 3, $"startup recovery: the failed, the interrupted and the orphaned take are waiting (got {waiting})");
Check(afterRestart.Any(t => t.Id == crashed.Id && t.Status == UnsentTakes.Interrupted), "a take still pending at startup comes back as interrupted");
Check(afterRestart.Any(t => t.Id == orphanId && t.Status == UnsentTakes.Interrupted), "a WAV with no sidecar is still a take");
Check(!File.Exists(Path.Combine(ufolder, oldId + ".wav")), "retention: a take older than 14 days is removed");
Check(afterRestart.Select(t => t.RecordedAt).SequenceEqual(afterRestart.Select(t => t.RecordedAt).OrderByDescending(x => x)), "listed newest first");
foreach (var t in afterRestart) restarted.Remove(t);
Check(restarted.List().Count == 0 && !Directory.EnumerateFiles(ufolder).Any(), "a retried take is removed with its sidecar");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0f. SpeechDetector (the silence gate) ==");
// Levels from the 2026-09-23 log: a genuinely silent take measured RMS
// 0.00022 / peak 0.0015; August's silent room 0.0006-0.00124 with 0.012 peaks
// from single transients; the owner's quiet speech averaged 0.0017-0.0026 and
// was dropped by the old RMS-only gate at 0.003.
const double Gate = 0.003;
void Gated(string what, byte[] wav, bool wantSilent, Func<SpeechDetector.Level, bool>? extra = null, string extraWhat = "")
{
    var lv = SpeechDetector.Measure(wav);
    bool silent = SpeechDetector.IsSilent(lv, Gate);
    Check(silent == wantSilent && (extra?.Invoke(lv) ?? true),
          $"{what} -> {(silent ? "silent" : "sent")}{extraWhat} ({lv.Describe()})");
}
Gated("tonight's silent take (noise 0.00022 RMS)", NoisyWav(0.00022, (2.1, 0)), wantSilent: true);
Gated("a fan-noise room (0.00124 RMS)", NoisyWav(0.00124, (2.0, 0)), wantSilent: true);
Gated("clicks in a quiet room (three 10 ms transients)", NoisyWav(0.0006, (0.5, 0), (0.01, 0.012), (0.5, 0), (0.01, 0.012), (0.5, 0), (0.01, 0.012), (0.5, 0)), wantSilent: true);
var syllables = new List<(double, double)> { (0.4, 0) };                       // the pre-roll
for (int i = 0; i < 5; i++) { syllables.Add((0.2, 0.006)); syllables.Add((0.1, 0)); }
syllables.Add((0.3, 0));
var quietSpeech = NoisyWav(0.0003, syllables.ToArray());
Gated("quiet speech at tonight's level", quietSpeech, wantSilent: false,
      lv => lv.Rms < Gate && lv.SpeechMs >= 600, ", though its RMS is under 0.003 (the old gate dropped it)");
// A cold mic has no pre-roll: speech from the first frame to the last, with
// only the short gaps between syllables for the detector to find the floor in.
var coldMic = new List<(double, double)>();
for (int i = 0; i < 8; i++) { coldMic.Add((0.15, 0.004)); if (i < 7) coldMic.Add((0.06, 0)); }
Gated("a cold-mic take: quiet speech from the first frame, no pre-roll, released on the last word", NoisyWav(0.0003, coldMic.ToArray()), wantSilent: false,
      lv => lv.Rms < Gate, ", though its RMS is under 0.003");
var diluted = new List<(double, double)> { (15, 0) };
for (int i = 0; i < 2; i++) { diluted.Add((0.2, 0.006)); diluted.Add((0.1, 0)); }
diluted.Add((15, 0));
Gated("a short phrase in a 30 s hold", NoisyWav(0.0003, diluted.ToArray()), wantSilent: false,
      lv => lv.Rms < 0.001, ", though the whole take averages under 0.001");
Gated("loud steady noise (0.005 RMS), no speech", NoisyWav(0.005, (2.0, 0)), wantSilent: false,
      lv => lv.SpeechMs == 0, ": no speech found, but too loud to call silent");
Gated("normal speech", Wav((0.4, 0), (2.0, 0.05), (0.3, 0)), wantSilent: false, lv => lv.SpeechMs >= 1900, ", speech found");
var digitalZero = SpeechDetector.Measure(Wav((2, 0)));
Check(digitalZero.Peak == 0 && SpeechDetector.IsSilent(digitalZero, Gate), "digital zero: peak 0 (MainWindow's dead-input error) and silent");
var tone = SpeechDetector.Measure(Wav((1, 0.5)));
Check(Math.Abs(tone.Peak - 0.5) < 0.001 && Math.Abs(tone.Rms - 0.5 / Math.Sqrt(2)) < 0.001, $"peak and RMS are unchanged from the old meter ({tone.Peak:F4}, {tone.Rms:F4})");
var garbage = SpeechDetector.Measure(new byte[] { 1, 2, 3, 4, 5 });
Check(!garbage.Measured && garbage.Peak == 1.0 && !SpeechDetector.IsSilent(garbage, Gate), "unreadable audio fails open: full scale, never silent");
Check(!SpeechDetector.IsSilent(SpeechDetector.Measure(NoisyWav(0.00022, (2.1, 0))), 0), "threshold 0 (gate disabled) never calls a take silent");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0g. UnsentTakes: takes judged silent ==");
string qfolder = Path.Combine(dir, "unsent-quiet-test");
if (Directory.Exists(qfolder)) Directory.Delete(qfolder, true);
var qstore = new UnsentTakes(qfolder, Log);
var failedTake = qstore.Begin(speech, elevenStub, 4.3);
qstore.Keep(failedTake, UnsentTakes.Failed, "the request failed");
await WaitFor(() => qstore.List().Count == 1);
var quietIds = new List<string>();
for (int i = 0; i < UnsentTakes.MaxQuietCount + 2; i++)
{
    await Task.Delay(5);
    quietIds.Add(qstore.KeepQuiet(quietSpeech, elevenStub, 2.2, "judged silent (RMS 0.0029)").Id);
}
Check(await WaitFor(() => qstore.List().Count(t => t.Status == UnsentTakes.Quiet) == UnsentTakes.MaxQuietCount),
      $"only the newest {UnsentTakes.MaxQuietCount} takes judged silent are kept");
var qlist = qstore.List();
Check(quietIds.Skip(2).All(id => qlist.Any(t => t.Id == id)) && quietIds.Take(2).All(id => qlist.All(t => t.Id != id)),
      "the oldest ones are the ones dropped");
Check(qlist.Any(t => t.Id == failedTake.Id && t.Status == UnsentTakes.Failed), "a run of silent takes never pushes a real failure out");
Check(new UnsentTakes(qfolder, Log).Recover() == 1, "the startup count of unsent dictations leaves out the ones judged silent");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0e. Provider resolution, sibling inheritance, repaired defaults ==");
const string ElevenUrl = "https://api.elevenlabs.io/v1/speech-to-text";
// The entry the user added by hand on 2026-09-23: no auth header, no bias
// mechanism. It sent Bearer and got 401 on every take.
var handAdded = new ApiProvider { Id = "dd794bcb", TranscriptionEndpoint = ElevenUrl, ModelFieldName = "model_id", TranscriptionModel = "scribe_v2_medical", ApiKey = "k" };
Check(handAdded.IsElevenLabs && handAdded.UsesCustomAuthHeader && handAdded.ResolvedAuthHeaderName == "xi-api-key",
      $"a hand-added ElevenLabs entry with no auth header sends xi-api-key, not Bearer (got \"{handAdded.ResolvedAuthHeaderName}\")");
Check(new ApiProvider { TranscriptionEndpoint = ElevenUrl }.ResolvedModelField == "model_id", "... and model_id when the model field is blank");
Check(handAdded.ResolvedBiasMechanism == "elevenlabs_keyterms", "... and routes the shared list to keyterms (the legacy fallback sent nothing)");
Check(new ApiProvider { TranscriptionEndpoint = ElevenUrl, AuthHeaderName = "X-Custom" }.ResolvedAuthHeaderName == "X-Custom", "an explicit auth header still wins");
var openAi = new ApiProvider { BaseUrl = "https://api.openai.com" };
Check(!openAi.UsesCustomAuthHeader && openAi.ResolvedModelField == "model" && openAi.ResolvedBiasMechanism == "none",
      "a non-ElevenLabs entry keeps Bearer, \"model\" and the legacy bias fallback");
var lookalike = new ApiProvider { BaseUrl = "https://notelevenlabs.io" };
Check(!lookalike.IsElevenLabs && !lookalike.UsesCustomAuthHeader, "a look-alike host is not ElevenLabs, so the key never goes to it as xi-api-key");

var defs = ApiProvider.CreateDefaults();
var med = defs.Single(p => p.Id == "elevenlabs-medical");
var scribeDef = defs.Single(p => p.Id == "elevenlabs");
Check(med is { TranscriptionModel: "scribe_v2_medical", AuthHeaderName: "xi-api-key", ModelFieldName: "model_id", BiasMechanism: "elevenlabs_keyterms", TagAudioEvents: false, NoVerbatim: true, Language: "en" }
      && med.IsElevenLabs && med.ResolvedTranscriptionUrl == scribeDef.ResolvedTranscriptionUrl,
      "the Scribe Medical preset is the Scribe request with model_id scribe_v2_medical");
Check(defs.Select(p => p.Id).Distinct().Count() == defs.Count, "no duplicate preset ids");

// Sibling inheritance, as LoadConfig runs it for a preset new to a config.
var scribe = new ApiProvider { Id = "elevenlabs", Name = "ElevenLabs Scribe", TranscriptionEndpoint = ElevenUrl, AuthHeaderName = "xi-api-key",
                               ApiKey = "sk-eleven", ScribeKeytermsRaw = "afebrile\nMepilex", NoVerbatim = false, TagAudioEvents = true };
var handMed = new ApiProvider { Id = "dd794bcb", TranscriptionEndpoint = ElevenUrl, ApiKey = "sk-other" };
var dg = new ApiProvider { Id = "deepgram", BaseUrl = "https://api.deepgram.com", TranscriberKind = TranscriberKind.Deepgram, ApiKey = "dg-key" };
var newMed = ApiProvider.CreateDefaults().Single(p => p.Id == "elevenlabs-medical");
var from = ApiProvider.InheritFromSibling(newMed, new[] { handMed, dg, scribe });
Check(from == scribe && newMed.ApiKey == "sk-eleven", "Scribe Medical takes the key from \"elevenlabs\" (the id prefix wins over another ElevenLabs entry)");
Check(newMed.ScribeKeytermsRaw == "afebrile\nMepilex" && !newMed.NoVerbatim && newMed.TagAudioEvents, "... and its Scribe keyterms and output switches");
var newDgMed = ApiProvider.CreateDefaults().Single(p => p.Id == "deepgram-medical");
Check(ApiProvider.InheritFromSibling(newDgMed, new[] { scribe, dg }) == dg && newDgMed.ApiKey == "dg-key" && newDgMed.ScribeKeytermsRaw == "",
      "deepgram-medical takes deepgram's key and nothing ElevenLabs-only");
var soniox = ApiProvider.CreateDefaults().Single(p => p.Id == "soniox");
Check(ApiProvider.InheritFromSibling(soniox, new[] { scribe, dg }) == null && soniox.ApiKey == "", "no sibling on the same host -> nothing inherited");
var keyed = ApiProvider.CreateDefaults().Single(p => p.Id == "elevenlabs-medical");
keyed.ApiKey = "already";
Check(ApiProvider.InheritFromSibling(keyed, new[] { scribe }) == null && keyed.ApiKey == "already", "a preset that already has a key keeps it");
Check(ApiProvider.InheritFromSibling(ApiProvider.CreateDefaults().Single(p => p.Id == "qwen3-asr-1.7b-local"), new[] { scribe }) == null,
      "a local preset inherits nothing");

// The granite-local glob, checked the way the transcriber resolves it:
// Directory.EnumerateFiles over a folder holding both Granite GGUFs.
var granite = defs.Single(p => p.Id == "granite-local");
string gdir = Path.Combine(dir, "granite-glob-test");
if (Directory.Exists(gdir)) Directory.Delete(gdir, true);
Directory.CreateDirectory(gdir);
File.WriteAllBytes(Path.Combine(gdir, "granite-speech-4.1-2b-plus-q4_k.gguf"), Array.Empty<byte>());
File.WriteAllBytes(Path.Combine(gdir, "granite-speech-4.1-2b-q4_k.gguf"), Array.Empty<byte>());
var oldPick = Directory.EnumerateFiles(gdir, "granite-speech-*.gguf").Select(Path.GetFileName).ToList();
var newPick = Directory.EnumerateFiles(gdir, granite.LocalModelGlob).Select(Path.GetFileName).ToList();
Check(oldPick.FirstOrDefault() == "granite-speech-4.1-2b-plus-q4_k.gguf", $"(the bug) the old glob resolved to the 2b-plus GGUF first ({oldPick.FirstOrDefault()})");
Check(newPick.SequenceEqual(new[] { "granite-speech-4.1-2b-q4_k.gguf" }), $"the pinned glob ({granite.LocalModelGlob}) resolves to the plain 4.1 2B GGUF only ({string.Join(", ", newPick)})");
Directory.Delete(gdir, true);
var oldGranite = new ApiProvider { Id = "granite-local", LocalModelGlob = "granite-speech-*.gguf" };
Check(ApiProvider.RepairSupersededDefault(oldGranite) != null && oldGranite.LocalModelGlob == ApiProvider.GraniteLocalGlob,
      "a config still carrying the old shipped glob is repaired");
var handGranite = new ApiProvider { Id = "granite-local", LocalModelGlob = "granite-speech-4.1-2b-plus-q4_k.gguf" };
Check(ApiProvider.RepairSupersededDefault(handGranite) == null && handGranite.LocalModelGlob == "granite-speech-4.1-2b-plus-q4_k.gguf",
      "a glob the user set by hand is left alone");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 1. HttpTranscriber vs a local fake server ==");
const string Prefix = "http://127.0.0.1:18999/";
var requests = new List<FakeRequest>();
string reply = "{}";
int replyDelayMs = 0;
int replyStatus = 200;
var listener = new HttpListener();
listener.Prefixes.Add(Prefix);
listener.Start();
_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); } catch { return; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var ms = new MemoryStream();
                // A request cut off mid-body (a cancelled stream) throws here
                // and is never recorded: the server didn't get it.
                await ctx.Request.InputStream.CopyToAsync(ms);
                var (fields, file) = ParseMultipart(ctx.Request.ContentType ?? "", ms.ToArray());
                var req = new FakeRequest(ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["xi-api-key"],
                    ctx.Request.Headers["X-API-Key"], ctx.Request.Headers["Authorization"], fields, file,
                    Chunked: ctx.Request.ContentLength64 < 0);
                lock (requests) requests.Add(req);
                string body = reply;
                if (replyDelayMs > 0) await Task.Delay(replyDelayMs);
                byte[] buf = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = replyStatus;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = buf.Length;
                await ctx.Response.OutputStream.WriteAsync(buf);
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        });
    }
});
// Same shape as the app's shared client: the backstop, not a flat 15 s.
var http = new HttpClient { Timeout = TranscriptionDeadline.HttpBackstop };
FakeRequest LastRequest() { lock (requests) return requests[^1]; }

var eleven = new ApiProvider
{
    Id = "elevenlabs", Name = "ElevenLabs Scribe", ApiKey = "test-key",
    TranscriptionEndpoint = Prefix + "v1/speech-to-text", AuthHeaderName = "xi-api-key",
    ModelFieldName = "model_id", TranscriptionModel = "scribe_v2", BiasMechanism = "elevenlabs_keyterms",
    Language = "en", TagAudioEvents = false, NoVerbatim = true,
    ScribeKeytermsRaw = "troponin\nhematochezia\nbad{term}",
};
reply = "{\"language_code\":\"en\",\"text\":\"The patient \\u2026 denies chest pain...\\nNo fever .\","
      + "\"words\":[{\"text\":\"The\",\"start\":0.1,\"end\":0.3,\"type\":\"word\"},"
      + "{\"text\":\" \",\"start\":0.3,\"end\":0.35,\"type\":\"spacing\"},"
      + "{\"text\":\"fever\",\"start\":3.0,\"end\":3.4,\"type\":\"word\"},"
      + "{\"text\":\" \",\"start\":3.4,\"end\":9.0,\"type\":\"spacing\"}],"
      + "\"audio_duration_secs\":4.3}";
var te = new HttpTranscriber(eleven, http, Log);
string? r1 = await te.TranscribeAsync(speech, new[] { "ureterolithiasis", "troponin" });
var q1 = LastRequest();
string[] names = q1.Fields.Select(f => f.Name).ToArray();
Console.WriteLine("   fields: " + string.Join(", ", q1.Fields.Select(f => f.Name + "=" + f.Value)));
Check(r1 == "The patient denies chest pain No fever.", $"ElevenLabs text comes back cleaned (\"{r1}\")");
Check(q1.XiApiKey == "test-key" && q1.Authorization == null, "auth: xi-api-key header, no Bearer");
Check(q1.Get("model_id") == "scribe_v2", "model_id=scribe_v2");
Check(q1.Get("language_code") == "en" && !names.Contains("language"), "language_code=en, and no bare `language` field");
Check(q1.Get("temperature") == "0", "temperature=0 when none is configured");
Check(q1.Get("diarize") == "false" && q1.Get("num_speakers") == "1", "diarize=false + num_speakers=1");
Check(q1.Get("timestamps_granularity") == "word", "timestamps_granularity=word");
Check(q1.Get("tag_audio_events") == "false" && q1.Get("no_verbatim") == "true", "tag_audio_events=false, no_verbatim=true");
var keyterms = q1.Fields.Where(f => f.Name == "keyterms").Select(f => f.Value).ToList();
Check(keyterms.SequenceEqual(new[] { "ureterolithiasis", "troponin", "hematochezia" }),
      $"keyterms = shared list + ElevenLabs-only list, deduped, invalid dropped ({string.Join(" | ", keyterms)})");
Check(Logged(@"Dropped \(illegal char\): bad\{term\}"), "the invalid keyterm is named in the log");
Check(names.Length > 0 && names[^1] == "file", "the file part is last");
var cov = (ITranscriptCoverage)te;
Check(cov.LastWordEndSeconds == 3.4, $"last WORD end read from words[] — spacing ignored ({cov.LastWordEndSeconds})");
Check(cov.DecodedAudioSeconds == 4.3, $"audio_duration_secs read ({cov.DecodedAudioSeconds})");

eleven.Language = "auto";
await te.TranscribeAsync(speech, Array.Empty<string>());
Check(!LastRequest().Fields.Any(f => f.Name is "language_code" or "language"), "Language=auto sends no language field, so Scribe detects it");
eleven.Language = "en";
eleven.TranscriptionTemperature = 0.2;
await te.TranscribeAsync(speech, Array.Empty<string>());
Check(LastRequest().Get("temperature") == "0.2", "a configured temperature wins over the ElevenLabs default of 0");
eleven.TranscriptionTemperature = null;

var other = new ApiProvider
{
    Id = "other", Name = "Other", ApiKey = "k2", BaseUrl = Prefix.TrimEnd('/'),
    AuthHeaderName = "X-API-Key", TranscriptionModel = "m1", Language = "en", BiasMechanism = "none",
};
reply = "{\"text\":\"hello ... world\"}";
string? r2 = await new HttpTranscriber(other, http, Log).TranscribeAsync(speech, new[] { "x" });
var q2 = LastRequest();
Check(!other.IsElevenLabs && q2.XApiKey == "k2", "a provider with some other custom header is NOT ElevenLabs");
Check(q2.Get("language") == "en" && q2.Get("model") == "m1", "it gets the standard `language` and `model` fields");
Check(!q2.Fields.Any(f => f.Name is "language_code" or "diarize" or "num_speakers" or "timestamps_granularity"
                                or "tag_audio_events" or "no_verbatim" or "temperature" or "keyterms"),
      "and none of the ElevenLabs-only fields (it used to get tag_audio_events/no_verbatim and lose `language`)");
Check(r2 == "hello ... world", "its text is returned as-is (the cleanup is ElevenLabs-only)");

// Deadline: the per-take token ends a slow request, loudly.
reply = "{\"text\":\"late\",\"words\":[]}";
replyDelayMs = 3000;
var swd = Stopwatch.StartNew();
using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
{
    string? r3 = await te.TranscribeAsync(speech, Array.Empty<string>(), cts.Token);
    swd.Stop();
    Check(r3 == null && swd.ElapsedMilliseconds < 2500, $"a response slower than the deadline -> null at the deadline ({swd.ElapsedMilliseconds} ms)");
}
Check(Logged(@"HttpTranscriber\(elevenlabs\): stopped at the take's deadline"), "the deadline is logged as a deadline, not a generic error");

if (!fast)
{
    // The bug the deadlines fix: a healthy response slower than the old
    // flat 15 s client timeout now arrives, because the budget scales.
    replyDelayMs = 16000;
    // The CLOUD budget for a 30 s take (30 s). The fake server is on
    // loopback, so the provider itself would be given the local one.
    var budget = TranscriptionDeadline.For(cloudProv, 30);
    var sws = Stopwatch.StartNew();
    using var cts2 = new CancellationTokenSource(budget);
    string? r4 = await te.TranscribeAsync(speech, Array.Empty<string>(), cts2.Token);
    sws.Stop();
    Check(r4 == "late" && sws.ElapsedMilliseconds > 15000,
          $"a 16 s answer on a 30 s take is delivered (deadline {budget.TotalSeconds:F0}s, took {sws.ElapsedMilliseconds} ms) — the old 15 s client failed it");
}
replyDelayMs = 0;

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 1b. Streamed upload (StreamedTranscription) vs the fake server ==");
reply = "{\"language_code\":\"en\",\"text\":\"The patient \\u2026 denies chest pain...\\nNo fever .\","
      + "\"words\":[{\"text\":\"The\",\"start\":0.1,\"end\":0.3,\"type\":\"word\"},"
      + "{\"text\":\"fever\",\"start\":3.0,\"end\":3.4,\"type\":\"word\"}],"
      + "\"audio_duration_secs\":4.3}";
byte[] speechPcm = PcmOf(speech);
long speechPcmLen = StreamedTranscription.PcmLength(speech);
Check(speechPcmLen == speechPcm.Length && speechPcmLen == speech.Length - 46,
      $"PcmLength finds the data chunk behind an 18-byte fmt chunk ({speechPcmLen} bytes)");
int RequestCount() { lock (requests) return requests.Count; }
// The way MicCapture hands it over: the pre-roll first, then 50 ms buffers.
void Feed(StreamedTranscription s, byte[] pcm)
{
    int first = Math.Min(pcm.Length, 400 * 32);
    s.Append(pcm, 0, first);
    for (int i = first; i < pcm.Length; i += 1600) s.Append(pcm, i, Math.Min(1600, pcm.Length - i));
}
var ordinary = q1.Fields.Where(f => f.Name != "file").Select(f => f.Name + "=" + f.Value).ToList();

var s1 = te.BeginStreamedTranscription(new[] { "ureterolithiasis", "troponin" });
Feed(s1, speechPcm);
var o1 = await s1.FinishAsync(speechPcmLen, CancellationToken.None);
s1.Dispose();
var qs = LastRequest();
Check(o1 is { FallBack: false, Text: "The patient denies chest pain No fever." }, $"a streamed take comes back cleaned ({o1.Text ?? "null"}; {o1.Reason})");
Check(qs.Chunked, "sent chunked: the upload started before the take's length was known");
Check(qs.XiApiKey == "test-key" && qs.Authorization == null, "same auth: xi-api-key");
Check(qs.Get("file_format") == "pcm_s16le_16", "file_format=pcm_s16le_16");
Check(qs.File != null && qs.File.SequenceEqual(speechPcm), $"the audio part is the take's PCM, byte for byte ({qs.File?.Length} bytes)");
Check(qs.Fields.Where(f => f.Name is not ("file" or "file_format")).Select(f => f.Name + "=" + f.Value).SequenceEqual(ordinary),
      "every other field is the ordinary request's, in the same order");
Check(qs.Fields.Count > 0 && qs.Fields[^1].Name == "file", "the audio is still the last part");
Check(((ITranscriptCoverage)te).LastWordEndSeconds == 3.4, "the word timing is read from a streamed response too");

int before = RequestCount();
var s2 = te.BeginStreamedTranscription(Array.Empty<string>());
Feed(s2, speechPcm[..^1600]);   // one buffer short of the WAV
var o2 = await s2.FinishAsync(speechPcmLen, CancellationToken.None);
s2.Dispose();
await Task.Delay(300);
Check(o2.FallBack && o2.Reason.Contains("carried"), $"a stream missing audio -> send the file instead ({o2.Reason})");
Check(RequestCount() == before, "... and the short stream is cancelled before the server ever has a complete request");

before = RequestCount();
var s3 = te.BeginStreamedTranscription(Array.Empty<string>());
s3.Append(speechPcm, 0, 16000);
await Task.Delay(100);
s3.Dispose();                   // a tap or a silent take
await Task.Delay(300);
Check(RequestCount() == before, "a discarded take's stream never reaches the server as a complete request (nothing to bill)");
Check(Logged(@"\[stream\] elevenlabs: upload cancelled"), "the cancellation is logged");

replyStatus = 500;
var s4 = te.BeginStreamedTranscription(Array.Empty<string>());
Feed(s4, speechPcm);
var o4 = await s4.FinishAsync(speechPcmLen, CancellationToken.None);
s4.Dispose();
replyStatus = 200;
Check(o4.FallBack, $"an HTTP error on the stream -> send the file instead ({o4.Reason})");

replyDelayMs = 3000;
var s5 = te.BeginStreamedTranscription(Array.Empty<string>());
Feed(s5, speechPcm);
var sw5 = Stopwatch.StartNew();
using (var cts5 = new CancellationTokenSource(500))
{
    var o5 = await s5.FinishAsync(speechPcmLen, cts5.Token);
    Check(!o5.FallBack && o5.Text == null && sw5.ElapsedMilliseconds < 2500,
          $"the take's deadline ends a streamed take with no second attempt ({sw5.ElapsedMilliseconds} ms)");
}
s5.Dispose();
replyDelayMs = 0;

var s6 = te.BeginStreamedTranscription(Array.Empty<string>());
var minute = new byte[60 * 32000];
for (int i = 0; i < 6; i++) s6.Append(minute, 0, minute.Length);   // six minutes
var o6 = await s6.FinishAsync(s6.BytesStreamed, CancellationToken.None);
s6.Dispose();
Check(o6.FallBack && o6.Reason.Contains("min"), $"past {StreamedTranscription.MaxStreamedAudio.TotalMinutes} min the stream is given up for the file ({o6.Reason})");

listener.Stop();

// ════════════════════════════════════════════════════════════════════════
if (!fast)
{
    // ── 2a. A model file crispasr can't load: the startup failure must say WHY ──
    Console.WriteLine("\n== 2a. crispasr: unloadable model ==");
    string bad = Path.Combine(dir, "bad-model.gguf");
    File.WriteAllText(bad, "this is not a gguf file");
    var badProv = new ApiProvider { Id = "harness-bad", Name = "harness bad", TranscriberKind = TranscriberKind.LocalCrispAsrServer,
        LocalServerPort = 18997, LocalModelGlob = bad, LocalGpuBackend = "cpu", Language = "en" };
    using (var t = new CrispAsrServerTranscriber(badProv, () => "cpu", Log))
    {
        var sw = Stopwatch.StartNew();
        string? r = await t.TranscribeAsync(speech, Array.Empty<string>());
        Console.WriteLine($"   result={(r == null ? "<null>" : "\"" + r + "\"")} in {sw.ElapsedMilliseconds} ms");
        Check(r == null, "unloadable model -> null result (the loud Error path)");
        Check(Logged(@"harness-bad\): server exited with code -?\d+ \(0x[0-9A-F]{8}\).*during startup"), "startup exit is logged WITH its exit code");
        Check(Logged(@"harness-bad\): server exited.*Last output:\n    \S"), "startup exit is logged WITH the server's own output lines");
    }

    // ── 2b. Real model on CPU: normal transcription, then an EMPTY one ──
    Console.WriteLine("\n== 2b. crispasr: parakeet on CPU ==");
    var okProv = new ApiProvider { Id = "harness-parakeet", Name = "harness parakeet", TranscriberKind = TranscriberKind.LocalCrispAsrServer,
        LocalServerPort = 18998, LocalModelGlob = "parakeet-tdt-*.gguf", LocalGpuBackend = "cpu", Language = "en" };
    var t2p = new CrispAsrServerTranscriber(okProv, () => "cpu", Log);
    Check(t2p.IsReady(out var diag), "parakeet model + exe present (" + (diag ?? "ok") + ")");
    string? rs = await t2p.TranscribeAsync(speech, Array.Empty<string>());
    Console.WriteLine($"   speech result: \"{rs}\"");
    Check(rs != null && rs.Contains("chest pain", StringComparison.OrdinalIgnoreCase), "speech transcribes normally through the patched path");

    string? rq = await t2p.TranscribeAsync(Wav((1.5, 0)), Array.Empty<string>());
    Console.WriteLine($"   silence result: {(rq == null ? "<null>" : "\"" + rq + "\"")}");
    if (rq == "")
        Check(Logged(@"harness-parakeet\): server returned EMPTY text\. Last server output:"), "empty text is logged with the server's output");
    else
        Console.WriteLine("   (server produced text for silence; the empty-text log path was not exercised here)");

    // ── 2c. The deadline fires mid-inference: null, logged, fresh server next time ──
    Console.WriteLine("\n== 2c. crispasr: deadline mid-inference ==");
    byte[] longSpeech = Concat(speech, speech, speech, speech);
    using (var tiny = new CancellationTokenSource(TimeSpan.FromMilliseconds(40)))
    {
        string? rd = await t2p.TranscribeAsync(longSpeech, Array.Empty<string>(), tiny.Token);
        Check(rd == null, "a deadline that passes mid-inference -> null");
    }
    Check(Logged(@"harness-parakeet\): stopped at the take's deadline; the server restarts on the next dictation"), "logged as the take's deadline, with the server's output");
    string? ra = await t2p.TranscribeAsync(speech, Array.Empty<string>());
    int spawns;
    lock (log) spawns = log.Count(l => l.Contains("harness-parakeet): spawned PID"));
    Check(ra != null && ra.Contains("chest pain", StringComparison.OrdinalIgnoreCase) && spawns == 2,
          $"the next dictation gets a fresh server and transcribes (spawns: {spawns})");

    // ── 2d. Server killed between dictations: the restart must be announced ──
    Console.WriteLine("\n== 2d. crispasr: server dies between dictations ==");
    int pid;
    lock (log) pid = int.Parse(Regex.Matches(string.Join("\n", log.Where(l => l.Contains("harness-parakeet): spawned PID"))),
                                             @"spawned PID (\d+)")[^1].Groups[1].Value);
    var victim = Process.GetProcessById(pid);
    Check(victim.ProcessName.Equals("crispasr", StringComparison.OrdinalIgnoreCase), $"PID {pid} is the harness's own crispasr server");
    victim.Kill(); victim.WaitForExit(5000);
    string? r5 = await t2p.TranscribeAsync(speech, Array.Empty<string>());
    Check(Logged(@"harness-parakeet\): previous server exited with code .* restarting\. Last output:"), "a dead server is announced (exit code + output) before the respawn");
    Check(r5 != null && r5.Contains("chest pain", StringComparison.OrdinalIgnoreCase), "the respawned server transcribes again");
    int restartLines; lock (log) restartLines = log.Count(l => l.Contains("harness-parakeet): previous server"));
    Check(restartLines == 1, $"the exit is reported exactly once (got {restartLines})");
    t2p.Dispose();
}
else
{
    Console.WriteLine("\n(fast: skipped the real-crispasr section and the >15 s server check)");
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// ── helpers ─────────────────────────────────────────────────────────────

static string Esc(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

static async Task<bool> WaitFor(Func<bool> cond, int ms = 3000)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        if (cond()) return true;
        await Task.Delay(20);
    }
    return cond();
}

// 16 kHz mono PCM16: consecutive (seconds, amplitude) segments of a 220 Hz
// tone; amplitude 0 is digital silence.
static byte[] Wav(params (double Seconds, double Amp)[] segments)
{
    var samples = new List<short>();
    foreach (var (seconds, amp) in segments)
    {
        int n = (int)Math.Round(seconds * 16000);
        for (int i = 0; i < n; i++)
            samples.Add((short)(Math.Sin(2 * Math.PI * 220 * i / 16000.0) * amp * short.MaxValue));
    }
    var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
    {
        int bytes = samples.Count * 2;
        bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)1); bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(bytes);
        foreach (var s in samples) bw.Write(s);
    }
    return ms.ToArray();
}

// Like Wav, over a noise floor: seeded white noise at noiseRms under every
// segment, so each run of the same call is the same audio.
static byte[] NoisyWav(double noiseRms, params (double Seconds, double Amp)[] segments)
{
    var rng = new Random(20260923);
    var samples = new List<short>();
    foreach (var (seconds, amp) in segments)
    {
        int n = (int)Math.Round(seconds * 16000);
        for (int i = 0; i < n; i++)
        {
            double v = Math.Sin(2 * Math.PI * 220 * i / 16000.0) * amp + (rng.NextDouble() * 2 - 1) * noiseRms * Math.Sqrt(3);
            samples.Add((short)Math.Clamp(v * short.MaxValue, short.MinValue, short.MaxValue));
        }
    }
    var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
    {
        int bytes = samples.Count * 2;
        bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)1); bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(bytes);
        foreach (var s in samples) bw.Write(s);
    }
    return ms.ToArray();
}

// Joins 16 kHz mono PCM16 WAVs into one longer WAV. Finds each "data" chunk
// rather than assuming a 44-byte header (the TTS WAV's fmt chunk is 18 bytes).
static byte[] Concat(params byte[][] wavs)
{
    var pcm = new List<byte>();
    foreach (var w in wavs)
    {
        int at = 12;
        while (at + 8 <= w.Length)
        {
            string id = Encoding.ASCII.GetString(w, at, 4);
            int size = BitConverter.ToInt32(w, at + 4);
            if (id == "data") { pcm.AddRange(w.Skip(at + 8).Take(size)); break; }
            at += 8 + size + (size & 1);
        }
    }
    var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
    {
        bw.Write("RIFF"u8); bw.Write(36 + pcm.Count); bw.Write("WAVE"u8); bw.Write("fmt "u8); bw.Write(16);
        bw.Write((short)1); bw.Write((short)1); bw.Write(16000); bw.Write(32000); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(pcm.Count); bw.Write(pcm.ToArray());
    }
    return ms.ToArray();
}

// The PCM in a WAV's data chunk (the TTS WAV's fmt chunk is 18 bytes, so
// the header isn't always 44).
static byte[] PcmOf(byte[] wav)
{
    int at = 12;
    while (at + 8 <= wav.Length)
    {
        string id = Encoding.ASCII.GetString(wav, at, 4);
        int size = BitConverter.ToInt32(wav, at + 4);
        if (id == "data") return wav.Skip(at + 8).Take(size).ToArray();
        at += 8 + size + (size & 1);
    }
    return Array.Empty<byte>();
}

static (List<(string Name, string Value)> Fields, byte[]? File) ParseMultipart(string contentType, byte[] body)
{
    var result = new List<(string Name, string Value)>();
    byte[]? file = null;
    var m = Regex.Match(contentType, "boundary=\"?([^\";]+)\"?");
    if (!m.Success) return (result, file);
    string text = Encoding.Latin1.GetString(body);   // byte-preserving
    foreach (var part in text.Split("--" + m.Groups[1].Value))
    {
        int headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) continue;
        string headers = part[..headerEnd];
        var nm = Regex.Match(headers, "name=\"?([^\";\r\n]+)\"?");
        if (!nm.Success) continue;
        string value = part[(headerEnd + 4)..];
        if (value.EndsWith("\r\n")) value = value[..^2];
        if (headers.Contains("filename")) file = Encoding.Latin1.GetBytes(value);
        value = headers.Contains("filename")
            ? $"<{value.Length} bytes>"
            : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
        result.Add((nm.Groups[1].Value, value));
    }
    return (result, file);
}

record FakeRequest(string Path, string? XiApiKey, string? XApiKey, string? Authorization,
                   List<(string Name, string Value)> Fields, byte[]? File, bool Chunked)
{
    public string? Get(string name) => Fields.FirstOrDefault(f => f.Name == name).Value;
}
