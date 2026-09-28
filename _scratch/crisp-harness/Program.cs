// Exercises the shipping transcription code without the app, asserting on
// results and on what reaches the log:
//   0. pure logic: TranscriptionDeadline, the ElevenLabs text cleanup,
//      TranscriptCoverage, UnsentTakes, SpeechDetector, and the drop-in
//      model discovery (GGUF headers, LocalModels, LocalModelScanner)
//   1. HttpTranscriber against a local fake server: the ElevenLabs request
//      shape, response parsing, and the per-take deadline; then the streamed
//      upload (1b) and OmiTranscriber's request, job polling and errors (1c)
//   2. CrispAsrServerTranscriber against the REAL crispasr.exe (CPU only,
//      unused ports)
// `dotnet run -- fast` skips section 2 and the slower-than-15s server check.
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
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

// `dotnet run -- qwen-live`: the shipped Qwen3 preset on the GPU, through
// CrispAsrServerTranscriber, over the takes `_join_clips.ps1 -Tails` builds
// (the clinical clips at 16 kHz with the owner's room tone: sentences with the
// key held after them, 30 s to 4.3 min, and takes released past the 60 s mark
// after the speech stopped), with the real bias list. None of them may come
// back with the list recited into it. Needs the owner's clips, the Qwen3 GGUF
// and a GPU, so it's in neither `fast` nor the full run.
if (args.Contains("qwen-live"))
{
    string tails = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "_scratch", "biasing", "tails"));
    string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk", "config.json");
    using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath));
    var terms = cfg.RootElement.GetProperty("ContextBiasTerms").EnumerateArray()
                   .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    var qwen = ApiProvider.CreateDefaults().Single(p => p.Id == "qwen3-asr-1.7b-local");
    qwen.LocalServerPort = 18993;
    Console.WriteLine($"Qwen3 preset (chunk_seconds={qwen.LocalExtraParams["chunk_seconds"]}), {terms.Count} list terms, takes in {tails}");
    using var tq = new CrispAsrServerTranscriber(qwen, () => "cuda", Log);
    foreach (string f in Directory.GetFiles(tails, "*.wav").OrderBy(f => f, StringComparer.Ordinal))
    {
        string name = Path.GetFileNameWithoutExtension(f);
        var sw = Stopwatch.StartNew();
        string text = await tq.TranscribeAsync(File.ReadAllBytes(f), terms) ?? "<null>";
        int Hits(string s) => Regex.Matches(text, Regex.Escape(s), RegexOptions.IgnoreCase).Count;
        var listed = terms.Where(t => Regex.IsMatch(text, $@"(?i)(?<!\w){Regex.Escape(t)}(?!\w)")).ToList();
        Console.WriteLine($"   {name,-30} {sw.ElapsedMilliseconds,6} ms  …{text[Math.Max(0, text.Length - 90)..]}");
        if (name == "room_tone_only") continue;   // the silence gate never sends a take like this
        Check(text != "<null>" && listed.All(t => t is "hematochezia" or "ureterolithiasis"), $"{name}: no list recited ({string.Join(", ", listed)})");
        var reps = Regex.Match(name, @"^long_(\d)x_six$");
        if (reps.Success)
        {
            int n = int.Parse(reps.Groups[1].Value);
            bool all = Hits("stable condition") == n && Hits("hematochezia") == 2 * n && Hits("ureterolithiasis") == n;
            Check(all, $"{name}: every sentence and term ({Hits("stable condition")}/{n} sentences, {Hits("hematochezia")}/{2 * n} hematochezia, {Hits("ureterolithiasis")}/{n} ureterolithiasis)");
            if (!all) Console.WriteLine($"      {text}");
        }
    }
    Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
    return failures == 0 ? 0 : 1;
}

// `dotnet run -- omi-live`: both Omi presets against the LIVE API, through
// OmiTranscriber, on the owner's six clips, the 32 s joined take and a 111 s
// take (over 60 s, so it goes through Omi's job path). The flagship runs with
// and without the shared list; Edge can't take one. Reads the key from the Omi
// presets in config.json. Sends real audio (the owner's own test recordings,
// no patient data) and uses Omi hours, so it's in neither `fast` nor the full run.
if (args.Contains("omi-live"))
{
    string bias = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "_scratch", "biasing"));
    string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk", "config.json");
    using var cfg = JsonDocument.Parse(File.ReadAllText(cfgPath));
    var terms = cfg.RootElement.GetProperty("ContextBiasTerms").EnumerateArray()
                   .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    string key = cfg.RootElement.GetProperty("Providers").EnumerateArray()
                    .Where(p => p.GetProperty("Id").GetString() is "omi-medical" or "omi-medical-edge")
                    .Select(p => p.TryGetProperty("ApiKey", out var k) ? k.GetString() ?? "" : "")
                    .FirstOrDefault(k => k.Length > 0) ?? "";
    if (key.Length == 0) { Console.WriteLine("No Omi key in config.json: paste it into an Omi preset in Configure Providers first."); return 2; }

    var defaults = ApiProvider.CreateDefaults();
    var flagship = defaults.Single(p => p.Id == "omi-medical");
    var edgeLive = defaults.Single(p => p.Id == "omi-medical-edge");
    flagship.ApiKey = edgeLive.ApiKey = key;
    var liveHttp = new HttpClient { Timeout = TranscriptionDeadline.HttpBackstop };
    var inputs = Directory.GetFiles(Path.Combine(bias, "clips"), "*.wav").OrderBy(f => f, StringComparer.Ordinal)
        .Append(Path.Combine(bias, "joined", "all_six_joined.wav"))
        .Append(Path.Combine(bias, "tails", "long_3x_six.wav"))
        .Where(File.Exists).ToList();
    // What each clip must contain to be right.
    var expect = new Dictionary<string, string>
    {
        ["hematochezia_1"] = "hematochezia", ["hematochezia_2"] = "hematochezia", ["ureterolithiasis"] = "ureterolithiasis",
        ["biliary_colic"] = "biliary colic", ["ureteral_colic"] = "ureteral colic", ["neutral"] = "stable condition",
    };
    Console.WriteLine($"{inputs.Count} takes, {terms.Count} list terms");
    var runs = new (ApiProvider Prov, IReadOnlyList<string> List, string Label)[]
    {
        (edgeLive, Array.Empty<string>(), "Edge"),
        (flagship, Array.Empty<string>(), "flagship, no list"),
        (flagship, terms, "flagship + list"),
    };
    foreach (var (prov, list, label) in runs)
    {
        Console.WriteLine($"\n== {prov.TranscriptionModel} ({label}) ==");
        var t = new OmiTranscriber(prov, liveHttp, Log);
        int right = 0, scored = 0;
        foreach (string f in inputs)
        {
            string name = Path.GetFileNameWithoutExtension(f);
            byte[] wav = File.ReadAllBytes(f);
            double seconds = (wav.Length - 44) / 32000.0;
            using var cts = new CancellationTokenSource(TranscriptionDeadline.For(prov, seconds));
            var sw = Stopwatch.StartNew();
            string text = await t.TranscribeAsync(wav, list, cts.Token) ?? "<null>";
            bool? ok = expect.TryGetValue(name, out var want) ? text.Contains(want, StringComparison.OrdinalIgnoreCase) : null;
            if (ok != null) { scored++; if (ok == true) right++; }
            Console.WriteLine($"   {(ok == null ? " " : ok == true ? "✓" : "✗")} {name,-18} {seconds,5:F1} s {sw.ElapsedMilliseconds,6} ms  {text}");
            Check(text != "<null>", $"{label}: {name} came back");
        }
        Console.WriteLine($"   {right}/{scored} clips right");
    }
    Check(Logged(@"\[omi\] over 60 s: transcribing as job"), "the 111 s take went through Omi's job path");
    Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
    return failures == 0 ? 0 : 1;
}

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

// Qwen3 in 60 s pieces: in the server's 30 s pieces a long take could end in
// a silent piece, which Qwen3 filled with the whole bias list.
var qwenDef = defs.Single(p => p.Id == "qwen3-asr-1.7b-local");
Check(qwenDef.LocalExtraParams.TryGetValue("chunk_seconds", out var qcs) && qcs == "60" && qwenDef.LocalExtraParams.Count == 1,
      "the Qwen3 preset asks for 60 s pieces, and nothing else");
var oldQwen = new ApiProvider { Id = "qwen3-asr-1.7b-local" };
Check(ApiProvider.RepairSupersededDefault(oldQwen) != null && oldQwen.LocalExtraParams.GetValueOrDefault("chunk_seconds") == "60",
      "a config still carrying the old Qwen3 preset (no extra params) is repaired");
Check(ApiProvider.RepairSupersededDefault(oldQwen) == null, "... once");
var handQwen = new ApiProvider { Id = "qwen3-asr-1.7b-local", LocalExtraParams = new() { ["seed"] = "42" } };
Check(ApiProvider.RepairSupersededDefault(handQwen) == null && handQwen.LocalExtraParams.Count == 1 && !handQwen.LocalExtraParams.ContainsKey("chunk_seconds"),
      "extra params the user set by hand are left alone");
Check(defs.Where(p => p.Id != "qwen3-asr-1.7b-local").All(p => ApiProvider.RepairSupersededDefault(p) == null),
      "no other shipped preset is touched by a repair");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0h. Drop-in local models: GGUF headers, providers, the folder scanner ==");
// Vocabularies as the real files have them (read 2026-09-24): Parakeet RNNT
// 1.1b's 1024 tokens hold no capital at all; TDT 0.6b v3's hold "▁De", "▁In"
// and 429 more; Granite Speech 5.0's byte-level vocabulary has "A".."Z" and
// "." as single characters but no capital in any word piece. Special and
// byte-fallback tokens say nothing about what a model writes.
string[] lowerVocab = { "<unk>", "<0x41>", "[INST]", "▁the", "▁a", "s", "ing", "'", "▁S", "A", ".", "▁." };
string[] casedVocab = { "<unk>", "▁the", "▁The", "." };
var lowerInfo = GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "parakeet"), ("parakeet.n_mels", 80u), ("tokenizer.ggml.tokens", lowerVocab))));
Check(lowerInfo is { Version: 3, Architecture: "parakeet", VocabHasCasedWords: false },
      $"no capital in any word piece (single letters, marks, special and byte tokens aside) -> writes lowercase ({lowerInfo})");
Check(GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "parakeet"), ("tokenizer.ggml.tokens", casedVocab)))).VocabHasCasedWords == true,
      "a vocabulary with \"▁The\" -> writes capitals");
Check(GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "x"), ("tokenizer.ggml.tokens", new[] { "Ġthe", "ĠThe" })))).VocabHasCasedWords == true,
      "byte-level BPE word pieces (Ġ) are read the same way");
Check(GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "x"), ("tokenizer.ggml.tokens", new[] { "Ġthe", "St" })))).VocabHasCasedWords == true,
      "... with or without a word-boundary marker");
Check(GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "granite_speech5_ctc"), ("tokenizer.ggml.tokens", new[] { "!", ",", "A", "B", "Z", "Ġthe", "ing" })))).VocabHasCasedWords == false,
      "Granite Speech 5.0's case: an alphabet with capitals, but none in a word piece -> lowercase");
var late = GgufHeader.Read(new MemoryStream(Gguf(2, ("tokenizer.ggml.tokens", casedVocab), ("general.name", "Late Arch"), ("general.architecture", "granite_nle"))));
Check(late is { Version: 2, Architecture: "granite_nle", Name: "Late Arch", VocabHasCasedWords: true },
      $"GGUF v2, with the architecture after the token list ({late})");
var mixed = GgufHeader.Read(new MemoryStream(Gguf(3, ("general.architecture", "voxtral4b"), ("voxtral4b.n", 3u), ("x.f", 1.5f), ("x.b", true),
                                                 ("x.ints", new[] { 1, 2, 3 }), ("x.nested", new[] { new[] { "a" }, new[] { "b", "c" } }))));
Check(mixed is { Architecture: "voxtral4b", VocabHasCasedWords: null }, "scalars, number arrays and nested arrays are stepped over; no token list -> unknown");
Check(Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(Gguf(1, ("general.architecture", "parakeet"))))), "GGUF v1 is refused (CrispASR no longer loads it)");
Check(Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream("RIFF\0\0\0\0WAVEfmt "u8.ToArray()))), "a file that isn't GGUF is refused");
var hugeLength = Gguf(3, ("general.architecture", "parakeet"));
BitConverter.GetBytes(1UL << 40).CopyTo(hugeLength, 24);   // the first key's length -> 1 TB
Check(Throws<InvalidDataException>(() => GgufHeader.Read(new MemoryStream(hugeLength))), "an absurd length is refused without allocating it");

string mdir = Path.Combine(dir, "model-scan-test");
if (Directory.Exists(mdir)) Directory.Delete(mdir, true);
Directory.CreateDirectory(mdir);
byte[] parakeetHeader = Gguf(3, ("general.architecture", "parakeet"), ("tokenizer.ggml.tokens", casedVocab));
string truncated = Path.Combine(mdir, "truncated.gguf");
File.WriteAllBytes(truncated, parakeetHeader[..40]);
Check(GgufHeader.TryRead(truncated, out var truncProblem) == null && truncProblem!.Contains("ends inside its header"),
      $"a file cut off inside its header is not offered ({truncProblem})");
File.Delete(truncated);
string busy = Path.Combine(mdir, "busy.gguf");
using (var writer = new FileStream(busy, FileMode.Create, FileAccess.Write, FileShare.Read))
{
    writer.Write(parakeetHeader);
    writer.Flush();
    Check(GgufHeader.TryRead(busy, out var busyProblem) == null && busyProblem == GgufHeader.StillWriting,
          "a file still open for writing (a copy in progress) -> \"still being written\"");
}
Check(GgufHeader.TryRead(busy, out _)?.Architecture == "parakeet", "... and readable once the writer closes it");
File.Delete(busy);

(string? Arch, ModelUse Want)[] uses =
{
    ("parakeet", ModelUse.SpeechToText), ("granite_nle", ModelUse.SpeechToText), ("qwen3asr", ModelUse.SpeechToText),
    ("gemma4e2b", ModelUse.SpeechToText), ("kokoro", ModelUse.NotSpeechToText), ("fireredpunc", ModelUse.NotSpeechToText),
    ("firered-lid", ModelUse.NotSpeechToText), ("qwen3-tts", ModelUse.NotSpeechToText), ("vibevoice-tts", ModelUse.NotSpeechToText),
    ("m2m100", ModelUse.NotSpeechToText), ("htdemucs", ModelUse.NotSpeechToText), ("xasr", ModelUse.Unrecognized), (null, ModelUse.Unrecognized),
};
foreach (var (arch, want) in uses)
    Check(LocalModels.UseOf(arch) == want, $"architecture {arch ?? "(none)"} -> {LocalModels.UseOf(arch)}");

(string File, string Want)[] displayNames =
{
    ("orukeet-q4_k.gguf", "Orukeet Q4_K"),
    ("granite-speech-4.1-2b-nar-q4_k.gguf", "Granite Speech 4.1 2B NAR Q4_K"),
    ("parakeet-tdt-0.6b-v3-q4_k.gguf", "Parakeet TDT 0.6B v3 Q4_K"),
    ("parakeet-tdt_ctc-110m-q8_0.gguf", "Parakeet TDT-CTC 110M Q8_0"),
    ("qwen3-asr-1.7b-q4_k.gguf", "Qwen3 ASR 1.7B Q4_K"),
    ("gemma4-e2b-it-q8_0.gguf", "Gemma4 E2B IT Q8_0"),
};
foreach (var (file, want) in displayNames)
    Check(LocalModels.DisplayName(file) == want, $"{file} is shown as \"{LocalModels.DisplayName(file)}\"");

LocalModelFile Fake(string name, string arch, bool? vocab) =>
    new(Path.Combine(mdir, name), 402_000_000, DateTime.UtcNow, new GgufInfo(3, arch, null, vocab), null);
var shipped = ApiProvider.CreateDefaults();
var noListeners = new HashSet<int>();
var oru = LocalModels.CreateProvider(Fake("orukeet-q4_k.gguf", "parakeet", true), shipped, noListeners);
Check(oru is { Id: "local-orukeet-q4_k", Name: "Orukeet Q4_K (local)", TranscriberKind: TranscriberKind.LocalCrispAsrServer,
               LocalServerPort: 8200, BaseUrl: "http://localhost:8200", LocalModelGlob: "orukeet-q4_k.gguf",
               LocalBackendHint: "", LocalPuncModel: "", BiasMechanism: "hotwords", Language: "en" },
      $"orukeet-q4_k.gguf -> a local provider for exactly that file, on 8200, CrispASR choosing the backend ({oru.Id}, \"{oru.Name}\", :{oru.LocalServerPort})");
Check(!oru.RequiresApiKey && oru.ResolvedTranscriptionUrl == "http://localhost:8200/v1/audio/transcriptions",
      "it needs no API key, and the URL agrees with the port");
Check(LocalModels.CreateProvider(Fake("orukeet-q4_k.gguf", "parakeet", true), shipped, new HashSet<int> { 8200 }).LocalServerPort == 8201,
      "a port something already listens on is skipped");
var oru2 = LocalModels.CreateProvider(Fake("orukeet-q4_k.gguf", "parakeet", true), shipped.Append(oru).ToList(), noListeners);
Check(oru2.Id == "local-orukeet-q4_k-2" && oru2.LocalServerPort == 8201, $"a second one gets its own id and port ({oru2.Id}, :{oru2.LocalServerPort})");
Check(LocalModels.CreateProvider(Fake("parakeet-rnnt-0.6b-q4_k.gguf", "parakeet", false), shipped, noListeners).LocalPuncModel == "fullstop",
      "a model whose vocabulary can't punctuate gets --punc-model fullstop");
Check(LocalModels.CreateProvider(Fake("cohere-transcribe-q4_k.gguf", "cohere-transcribe", true), shipped, noListeners).BiasMechanism == "none",
      "a family measured to ignore hotwords is labelled \"none\"");
Check(Throws<ArgumentException>(() => LocalModels.CreateProvider(new LocalModelFile(Path.Combine(mdir, "x.gguf"), 1, DateTime.UtcNow, null, "not a GGUF file"), shipped, noListeners)),
      "an unreadable file can't become a provider");
var jsonOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
using (var saved = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new { Providers = new[] { oru } }, jsonOptions)))
{
    var pj = saved.RootElement.GetProperty("Providers")[0];
    Check(pj.GetProperty("TranscriberKind").GetString() == "LocalCrispAsrServer"
          && pj.GetProperty("LocalServerPort").ValueKind == System.Text.Json.JsonValueKind.Number
          && pj.GetProperty("LocalModelGlob").GetString() == "orukeet-q4_k.gguf",
          "saved the way SaveConfig writes it, in the types LoadConfig reads back");
}

// A model folder like the desktop's: the shipped presets' files, two no
// preset loads (Orukeet, the 2b-plus Granite) and a voice.
(string Name, string Arch)[] folderFiles =
{
    ("parakeet-tdt-0.6b-v3-q4_k.gguf", "parakeet"), ("parakeet-rnnt-1.1b-q4_k.gguf", "parakeet"),
    ("qwen3-asr-1.7b-q4_k.gguf", "qwen3asr"), ("granite-speech-4.1-2b-q4_k.gguf", "granite_speech"),
    ("granite-speech-4.1-2b-plus-q4_k.gguf", "granite_speech"), ("cohere-transcribe-q6_k.gguf", "cohere-transcribe"),
    ("voxtral-mini-4b-realtime-q4_k.gguf", "voxtral4b"), ("orukeet-q4_k.gguf", "parakeet"), ("kokoro-82m-q8_0.gguf", "kokoro"),
};
foreach (var (name, arch) in folderFiles)
    File.WriteAllBytes(Path.Combine(mdir, name), Gguf(3, ("general.architecture", arch), ("tokenizer.ggml.tokens", casedVocab)));
using (var scanner = new LocalModelScanner(mdir, Log))
{
    var scanned = scanner.Scan();
    Check(scanned.Count == folderFiles.Length && scanned.All(f => f.Info != null), $"the scanner reads every file's header ({scanned.Count} files)");
    string TestFolder(ApiProvider _) => mdir;
    var offered = LocalModels.NewModels(scanned, shipped, TestFolder).Select(f => f.FileName).OrderBy(n => n).ToList();
    Check(offered.SequenceEqual(new[] { "granite-speech-4.1-2b-plus-q4_k.gguf", "orukeet-q4_k.gguf" }),
          $"offered: only the models no provider loads, and not the voice ({string.Join(", ", offered)})");
    var added = LocalModels.CreateProvider(scanned.Single(f => f.FileName == "orukeet-q4_k.gguf"), shipped, noListeners);
    var offeredAfter = LocalModels.NewModels(scanned, shipped.Append(added), TestFolder).Select(f => f.FileName).ToList();
    Check(offeredAfter.SequenceEqual(new[] { "granite-speech-4.1-2b-plus-q4_k.gguf" }), "once added, it is no longer offered");
    Check(!Logged(@"\[models\] new model file"), "the files already there at startup aren't announced in the log");

    string copying = Path.Combine(mdir, "zz-copying-q4_k.gguf");
    using (var w = new FileStream(copying, FileMode.Create, FileAccess.Write, FileShare.Read))
    {
        w.Write(parakeetHeader);
        w.Flush();
        var during = scanner.Scan().Single(f => f.FileName == "zz-copying-q4_k.gguf");
        Check(during.StillWriting && LocalModels.NewModels(new[] { during }, shipped, TestFolder).Count == 1,
              "a file mid-copy is listed as still copying, not offered to add");
    }
    Check(scanner.Scan().Single(f => f.FileName == "zz-copying-q4_k.gguf").Info?.Architecture == "parakeet",
          "... and becomes an offer when the copy finishes");

    File.WriteAllBytes(Path.Combine(mdir, "granite-speech-4.1-2b-nar-q4_k.gguf"),
                       Gguf(3, ("general.architecture", "granite_nle"), ("tokenizer.ggml.tokens", casedVocab)));
    Check(await WaitFor(() => scanner.Snapshot.Any(f => f.FileName == "granite-speech-4.1-2b-nar-q4_k.gguf"), 5000),
          "a model copied in while running is found with no menu open (the folder is watched)");
    Check(Logged(@"\[models\] new model file: granite-speech-4\.1-2b-nar-q4_k\.gguf .*Granite Speech NAR \(granite_nle\)"),
          "... and announced in the log, saying what it is");
}
Directory.Delete(mdir, true);

// The real model folder, where there is one: each header read the way the
// menu reads it, and fast.
string realModelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk", "cohere-gguf");
(string File, string Arch, bool? Vocab)[] realModels =
{
    ("parakeet-rnnt-1.1b-q4_k.gguf", "parakeet", false),
    ("parakeet-tdt-0.6b-v3-q4_k.gguf", "parakeet", true),
    ("qwen3-asr-1.7b-q4_k.gguf", "qwen3asr", true),
    ("granite-speech-4.1-2b-q4_k.gguf", "granite_speech", true),
    ("cohere-transcribe-q6_k.gguf", "cohere-transcribe", true),
    ("voxtral-mini-4b-realtime-q4_k.gguf", "voxtral4b", null),
};
foreach (var (file, arch, vocab) in realModels)
{
    string p = Path.Combine(realModelDir, file);
    if (!File.Exists(p)) { Console.WriteLine($"   ({file} is not on this machine; skipped)"); continue; }
    var swr = Stopwatch.StartNew();
    var ri = GgufHeader.TryRead(p, out var rp);
    swr.Stop();
    Check(ri?.Architecture == arch && ri.VocabHasCasedWords == vocab && swr.ElapsedMilliseconds < 500,
          $"the real {file}: {ri?.Architecture ?? rp}, cased words in its vocabulary: {ri?.VocabHasCasedWords?.ToString() ?? "no token list"}, read in {swr.ElapsedMilliseconds} ms");
}

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 0i. LocalTakeSplitter: long local takes cut at their own pauses ==");
// The server's own cut can leave a piece of pure silence, which Qwen3 fills
// with the whole bias list. Takes as the app records them: 16 kHz mono over
// a 0.0003 RMS noise floor, "speech" in 5 s bursts of a 0.02 tone.
var maxPiece = TimeSpan.FromSeconds(59);
int maxPieceSamples = 59 * 16000;
(byte[] Wav, List<(double From, double To)> Gaps) Bursts(int count, double pause, double tail)
{
    var segs = new List<(double Seconds, double Amp)>();
    var gaps = new List<(double From, double To)>();
    double t = 0;
    for (int i = 0; i < count; i++)
    {
        segs.Add((5.0, 0.02)); t += 5.0;
        double gap = i < count - 1 ? pause : tail;
        if (gap > 0) { segs.Add((gap, 0)); gaps.Add((t, t + gap)); t += gap; }
    }
    return (NoisyWav(0.0003, segs.ToArray()), gaps);
}
bool Sound(LocalTakeSplitter.Split sp, List<(double From, double To)>? gaps)
{
    var p = sp.Pieces;
    bool ok = p.Count == sp.Wavs.Count && p.All(x => x.Length <= maxPieceSamples && x.Length > 0);
    for (int i = 0; i < p.Count; i++)
    {
        var level = SpeechDetector.Measure(sp.Wavs[i]);
        ok &= level.Measured && level.SpeechMs > 0 && sp.Wavs[i].Length == 44 + 2 * p[i].Length;
        if (i + 1 < p.Count && gaps != null)
        {
            ok &= p[i].End == p[i + 1].Start;
            double cut = p[i].End / 16000.0;
            ok &= gaps.Any(g => cut > g.From && cut < g.To);
        }
    }
    return ok;
}
Check(LocalTakeSplitter.Cut(Bursts(8, 0.8, 1.5).Wav, maxPiece) == null, "a take that fits in one piece (47 s) goes out whole");
var (w105, g105) = Bursts(18, 0.8, 1.5);
var s105 = LocalTakeSplitter.Cut(w105, maxPiece);
Check(s105 != null && s105.Pieces.Count == 2 && s105.Pieces[0].Start == 0 && s105.Pieces[^1].End == s105.TotalSamples
      && s105.TrailingSilence == 0 && Sound(s105, g105),
      $"105 s: 2 pieces under 59 s, each with speech, cut inside a pause, nothing left out ({Describe(s105)})");
var (w256, g256) = Bursts(44, 0.8, 1.5);
var s256 = LocalTakeSplitter.Cut(w256, maxPiece);
Check(s256 != null && s256.Pieces.Count >= 5 && s256.Pieces[^1].End == s256.TotalSamples && Sound(s256, g256),
      $"256 s: every piece under 59 s, with speech, cut inside a pause ({Describe(s256)})");
// The take that still failed with the server's 60 s cut: the speech stops at
// 57 s and the key is held to 63.6 s.
var (wStraddle, gStraddle) = Bursts(10, 0.8, 6.4);
var sStraddle = LocalTakeSplitter.Cut(wStraddle, maxPiece);
Check(sStraddle != null && sStraddle.Pieces.Count == 1 && sStraddle.Pieces[0].Length > maxPieceSamples - 480
      && sStraddle.TrailingSilence == sStraddle.TotalSamples - sStraddle.Pieces[0].Length && Sound(sStraddle, gStraddle),
      $"speech to 57 s, released at 63.6 s: one 59 s piece, and the {sStraddle?.Seconds(sStraddle.TrailingSilence):F1} s of silence past it left out, never sent alone");
var sGap = LocalTakeSplitter.Cut(NoisyWav(0.0003, (20, 0.02), (70, 0), (20, 0.02)), maxPiece);
Check(sGap != null && sGap.Pieces.Count == 2 && sGap.SilentStretches == 1 && Sound(sGap, null),
      $"a 70 s silence mid-take: the stretch with no speech is left out, both speech pieces go ({Describe(sGap)})");
// 10 s of room first, so the take has a floor to measure speech against. (A
// piece of unbroken tone has no quiet moment of its own, so Sound's
// per-piece speech test can't be used on these.)
var sNoPause = LocalTakeSplitter.Cut(NoisyWav(0.0003, (10, 0), (100, 0.02)), maxPiece);
Check(sNoPause != null && sNoPause.Pieces.Count >= 2 && sNoPause.Pieces[0].Start == 0 && sNoPause.Pieces[^1].End == sNoPause.TotalSamples
      && sNoPause.Pieces.All(p => p.Length <= maxPieceSamples)
      && sNoPause.Pieces.Zip(sNoPause.Pieces.Skip(1), (a, b) => a.End == b.Start).All(x => x),
      $"100 s of speech with no pause at all is still cut under 59 s, nothing left out ({Describe(sNoPause)})");
Check(LocalTakeSplitter.Cut(NoisyWav(0.0025, (70, 0)), maxPiece) == null, "70 s of steady noise has no speech to anchor a cut on: sent whole, as before");
Check(LocalTakeSplitter.Cut(new byte[100], maxPiece) == null, "an unreadable take is sent whole");
var stereo = (byte[])w105.Clone();
BitConverter.GetBytes((short)2).CopyTo(stereo, 22); BitConverter.GetBytes(64000).CopyTo(stereo, 28); BitConverter.GetBytes((short)4).CopyTo(stereo, 32);
Check(LocalTakeSplitter.Cut(stereo, maxPiece) == null, "a stereo WAV (not what the app records) is sent whole");

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 1. HttpTranscriber vs a local fake server ==");
const string Prefix = "http://127.0.0.1:18999/";
var requests = new List<FakeRequest>();
string reply = "{}";
int replyDelayMs = 0;
int replyStatus = 200;
// When set, answers by request instead of `reply`/`replyStatus`: a flow with
// more than one step (Omi's 202 job and its polls). RetryAfter is seconds.
Func<FakeRequest, (int Status, string Body, int? RetryAfter)>? route = null;
var listener = new HttpListener();
listener.Prefixes.Add(Prefix);
listener.Prefixes.Add("http://localhost:18999/");   // a second host name, for "not Omi's host"
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
                    Chunked: ctx.Request.ContentLength64 < 0, Method: ctx.Request.HttpMethod,
                    Query: ctx.Request.Url.Query, Host: ctx.Request.Url.Host);
                lock (requests) requests.Add(req);
                var (status, body, retryAfter) = route?.Invoke(req) ?? (replyStatus, reply, null);
                if (replyDelayMs > 0) await Task.Delay(replyDelayMs);
                byte[] buf = Encoding.UTF8.GetBytes(body);
                if (retryAfter is int ra) ctx.Response.AddHeader("Retry-After", ra.ToString());
                ctx.Response.StatusCode = status;
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

// ════════════════════════════════════════════════════════════════════════
Console.WriteLine("\n== 1c. OmiTranscriber vs the fake server ==");
var omiDefaults = ApiProvider.CreateDefaults();
var omiFlag = omiDefaults.First(p => p.Id == "omi-medical");
var omiEdge = omiDefaults.First(p => p.Id == "omi-medical-edge");
Check(omiFlag is { TranscriberKind: TranscriberKind.Omi, Language: "en", TranscriptionModel: "omi-medical-1", BiasMechanism: "omi_vocabulary" }
      && omiEdge is { TranscriberKind: TranscriberKind.Omi, Language: "en", TranscriptionModel: "omi-medical-edge-1", BiasMechanism: "none" }
      && omiFlag.ResolvedTranscriptionUrl == "https://api.omi.health/v1/audio/transcriptions" && omiFlag.RequiresApiKey,
      "presets: omi-medical and omi-medical-edge, both English, one endpoint, a key required");
// Pointed at the fake server from here on.
foreach (var p in new[] { omiFlag, omiEdge })
{
    p.ApiKey = "omi-key";
    p.BaseUrl = Prefix.TrimEnd('/');
    p.TranscriptionEndpoint = Prefix + "v1/audio/transcriptions";
}
var omi = new OmiTranscriber(omiFlag, http, Log);
reply = "{\"text\":\"  Bright red blood per rectum, consistent with hematochezia.  \"}";
string? om1 = await omi.TranscribeAsync(speech, new[] { "hematochezia", "ureterolithiasis", "Hematochezia", new string('x', 100) });
var oq1 = LastRequest();
Console.WriteLine("   fields: " + string.Join(", ", oq1.Fields.Select(f => f.Name + "=" + f.Value)));
Check(om1 == "Bright red blood per rectum, consistent with hematochezia.", $"inline 200: the text comes back trimmed (\"{om1}\")");
Check(oq1.Path == "/v1/audio/transcriptions" && oq1.Authorization == "Bearer omi-key", "POST /v1/audio/transcriptions with Bearer auth");
Check(oq1.Get("model") == "omi-medical-1" && oq1.Get("response_format") == "json" && oq1.Get("language") == "en",
      "model=omi-medical-1, response_format=json (Omi's default is diarized_json), language=en");
var omiVocab = oq1.Get("vocabulary") is string vj ? JsonSerializer.Deserialize<string[]>(vj) : null;
Check(omiVocab != null && omiVocab.SequenceEqual(new[] { "hematochezia", "ureterolithiasis" }),
      $"vocabulary is a JSON array, deduped, with a term over 96 characters dropped ({oq1.Get("vocabulary")})");
Check(Logged(@"\[omi\] 1 bias term\(s\) over 96 characters dropped"), "the dropped term is logged");
Check(oq1.Fields.Count > 0 && oq1.Fields[^1].Name == "file", "the file part is last");

reply = "{\"text\":\"\"}";
Check(await omi.TranscribeAsync(speech, Array.Empty<string>()) == "", "empty text is \"\" (nothing heard), not a failed request");

omiFlag.Language = "auto";
reply = "{\"text\":\"ok\"}";
await omi.TranscribeAsync(speech, Array.Empty<string>());
Check(LastRequest().Get("language") == null && LastRequest().Get("vocabulary") == null,
      "Language=auto sends no language (dominant-language detection); an empty list sends no vocabulary");
omiFlag.Language = "en";

var edge = new OmiTranscriber(omiEdge, http, Log);
omiEdge.Language = "auto";
await edge.TranscribeAsync(speech, new[] { "hematochezia" });
var oq2 = LastRequest();
Check(oq2.Get("model") == "omi-medical-edge-1" && oq2.Get("language") == "en" && oq2.Get("vocabulary") == null,
      "Edge: always language=en (it can't detect), never vocabulary (it rejects the field)");
Check(Logged(@"\[omi\] omi-medical-edge-1 takes no vocabulary; 1 bias term\(s\) not sent"), "Edge logs the terms it couldn't send");
omiEdge.Language = "en";

// A take over 60 s: a 202 job, long-polled to the end.
int polls = 0;
route = r => r.Method switch
{
    "POST" => (202, $"{{\"id\":\"job_1\",\"object\":\"transcription.job\",\"status\":\"accepted\",\"poll_url\":\"{Prefix}v1/jobs/job_1\"}}", null),
    _ when r.Path == "/v1/jobs/job_1" => ++polls < 3
        ? (200, "{\"id\":\"job_1\",\"status\":\"running\"}", null)
        : (200, "{\"id\":\"job_1\",\"status\":\"succeeded\",\"result\":{\"content\":{\"text\":\"A long take.\"}}}", null),
    _ => (404, "{\"error\":{\"code\":\"not_found\",\"message\":\"no such job\"}}", null),
};
string? om3 = await omi.TranscribeAsync(speech, Array.Empty<string>());
var poll = LastRequest();
Check(om3 == "A long take." && polls == 3, $"202 -> the job is polled until it succeeds, and its text read from result.content (\"{om3}\", {polls} polls)");
Check(poll.Method == "GET" && poll.Query.Contains("wait=20") && poll.Query.Contains("include_result=true") && poll.Authorization == "Bearer omi-key",
      $"the polls are long polls asking for the result inline, with the key ({poll.Query})");

// The result as a download link: the key goes to Omi's own host only.
string resultHost = "";
string? resultAuth = null;
route = r => r switch
{
    { Method: "POST" } => (202, $"{{\"id\":\"job_2\",\"status\":\"accepted\",\"poll_url\":\"{Prefix}v1/jobs/job_2\"}}", null),
    { Path: "/v1/jobs/job_2" } => (200, $"{{\"status\":\"succeeded\",\"result\":{{\"download_url\":\"http://{resultHost}:18999/results/r2\"}}}}", null),
    { Path: "/results/r2" } => (200, (resultAuth = r.Authorization) == null ? "{\"text\":\"From storage.\"}" : "{\"text\":\"From Omi.\"}", null),
    _ => (404, "{}", null),
};
resultHost = "127.0.0.1";
string? om4 = await omi.TranscribeAsync(speech, Array.Empty<string>());
Check(om4 == "From Omi." && resultAuth == "Bearer omi-key", $"a result link on Omi's own host is fetched with the key (\"{om4}\")");
resultHost = "localhost";
string? om5 = await omi.TranscribeAsync(speech, Array.Empty<string>());
Check(om5 == "From storage." && resultAuth == null, $"a result link on any other host (signed storage) never gets the key (\"{om5}\")");

route = r => r.Method == "POST"
    ? (202, $"{{\"id\":\"job_3\",\"status\":\"accepted\",\"poll_url\":\"{Prefix}v1/jobs/job_3\"}}", null)
    : (200, "{\"id\":\"job_3\",\"status\":\"failed\",\"error\":{\"code\":\"audio_decode_failed\",\"message\":\"could not decode\"}}", null);
Check(await omi.TranscribeAsync(speech, Array.Empty<string>()) == null && Logged(@"\[omi\] job job_3 failed: \[audio_decode_failed\] could not decode"),
      "a failed job -> null, logged with Omi's error code");

// Errors, and the one retry on capacity.
route = _ => (402, "{\"error\":{\"code\":\"billing_blocked\",\"message\":\"Usage is paused.\"}}", null);
Check(await omi.TranscribeAsync(speech, Array.Empty<string>()) == null
      && Logged(@"\[omi\] HTTP 402: \[billing_blocked\] Usage is paused\. \(Omi usage is paused for billing"),
      "402 -> null, logged with Omi's code and what to do about it");
int posts = 0;
route = _ => ++posts == 1 ? (503, "{\"error\":{\"code\":\"capacity\",\"message\":\"busy\"}}", 1) : (200, "{\"text\":\"Second try.\"}", null);
string? om6 = await omi.TranscribeAsync(speech, Array.Empty<string>());
Check(om6 == "Second try." && posts == 2 && Logged(@"\[omi\] HTTP 503: \[capacity\] busy.*retrying once in 1 s"),
      $"503 with Retry-After: 1 -> one retry, logged ({posts} posts)");
posts = 0;
route = _ => { posts++; return (503, "{\"error\":{\"code\":\"capacity\",\"message\":\"busy\"}}", 30); };
Check(await omi.TranscribeAsync(speech, Array.Empty<string>()) == null && posts == 1,
      "a Retry-After too long to wait inside a dictation -> no retry; the take fails and stays journaled");
route = null;

reply = "{\"text\":\"late\"}";
replyDelayMs = 3000;
var swo = Stopwatch.StartNew();
using (var cto = new CancellationTokenSource(500))
{
    string? om7 = await omi.TranscribeAsync(speech, Array.Empty<string>(), cto.Token);
    swo.Stop();
    Check(om7 == null && swo.ElapsedMilliseconds < 2500 && Logged(@"OmiTranscriber\(omi-medical\): stopped at the take's deadline"),
          $"the take's deadline ends a slow request, logged as the deadline ({swo.ElapsedMilliseconds} ms)");
}
replyDelayMs = 0;

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

    // ── 2e. A provider made the drop-in way: exact file, no --backend ──
    Console.WriteLine("\n== 2e. crispasr: a provider added from the model folder ==");
    string v3Path = Path.Combine(realModelDir, "parakeet-tdt-0.6b-v3-q4_k.gguf");
    var v3 = new LocalModelFile(v3Path, new FileInfo(v3Path).Length, DateTime.UtcNow, GgufHeader.TryRead(v3Path, out _), null);
    var dropIn = LocalModels.CreateProvider(v3, ApiProvider.CreateDefaults(), LocalModels.ListeningPorts());
    Check(dropIn is { LocalModelGlob: "parakeet-tdt-0.6b-v3-q4_k.gguf", LocalBackendHint: "" } && dropIn.LocalServerPort >= LocalModels.FirstAutoPort,
          $"made for the real file, with no backend hint, on :{dropIn.LocalServerPort}");
    // Moved to a harness port so it can never meet a server the app runs.
    dropIn.Id = "harness-dropin";
    dropIn.LocalServerPort = 18996;
    dropIn.BaseUrl = "http://localhost:18996";
    dropIn.TranscriptionEndpoint = "http://localhost:18996/v1/audio/transcriptions";
    dropIn.LocalGpuBackend = "cpu";
    using (var td = new CrispAsrServerTranscriber(dropIn, () => "cpu", Log))
    {
        Check(await td.WarmUpAsync(), "WarmUpAsync starts the server before any dictation");
        Check(Logged(@"harness-dropin\): healthy on port 18996 \(backend parakeet\)"), "the log names the backend CrispASR chose by itself");
        string? rdrop = await td.TranscribeAsync(speech, Array.Empty<string>());
        Check(rdrop != null && rdrop.Contains("chest pain", StringComparison.OrdinalIgnoreCase), $"... and it transcribes (\"{rdrop}\")");
    }
    var gone = new ApiProvider { Id = "harness-gone", Name = "harness gone", TranscriberKind = TranscriberKind.LocalCrispAsrServer,
        LocalServerPort = 18995, LocalModelGlob = "no-such-model.gguf", LocalGpuBackend = "cpu" };
    using (var tg = new CrispAsrServerTranscriber(gone, () => "cpu", Log))
        Check(!await tg.WarmUpAsync() && Logged(@"harness-gone\): can't start: Model GGUF 'no-such-model\.gguf' not found"),
              "a provider whose file is gone fails the warm-up and names the file");

    // ── 2f. A preset that sets chunk_seconds: a long take goes out in pieces cut at its pauses ──
    Console.WriteLine("\n== 2f. crispasr: a long take cut at its own pauses ==");
    var cutProv = new ApiProvider { Id = "harness-pieces", Name = "harness pieces", TranscriberKind = TranscriberKind.LocalCrispAsrServer,
        LocalServerPort = 18994, LocalModelGlob = "parakeet-tdt-*.gguf", LocalGpuBackend = "cpu", Language = "en",
        LocalExtraParams = new() { ["chunk_seconds"] = "60" } };
    double speechSeconds = PcmOf(speech).Length / 32000.0;
    var takeParts = new List<byte[]>();
    int sentences = 0;
    while (sentences * (speechSeconds + 1.0) < 66) { takeParts.Add(speech); takeParts.Add(Wav((1.0, 0))); sentences++; }
    takeParts.Add(Wav((6.0, 0)));   // the key held after the last word
    byte[] longTake = Concat(takeParts.ToArray());
    using (var tc = new CrispAsrServerTranscriber(cutProv, () => "cpu", Log))
    {
        string? rl = await tc.TranscribeAsync(longTake, Array.Empty<string>());
        int found = rl == null ? 0 : Regex.Matches(rl, "chest pain", RegexOptions.IgnoreCase).Count;
        Console.WriteLine($"   {sentences} sentences in {longTake.Length / 32000.0:F1} s -> \"chest pain\" {found} times");
        Check(Logged(@"harness-pieces\): [\d.,]+ s take sent in [2-9] pieces cut at pauses"), "the take went out in pieces, and the log says so");
        Check(found == sentences, $"every sentence came back exactly once ({found}/{sentences})");
    }
}
else
{
    Console.WriteLine("\n(fast: skipped the real-crispasr section and the >15 s server check)");
}

Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// ── helpers ─────────────────────────────────────────────────────────────

static string Esc(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

static string Describe(LocalTakeSplitter.Split? sp) => sp == null ? "sent whole"
    : string.Join(" + ", sp.Pieces.Select(p => sp.Seconds(p.Length).ToString("F1"))) + " s"
      + (sp.TrailingSilence > 0 ? $", {sp.Seconds(sp.TrailingSilence):F1} s left out" : "")
      + (sp.SilentStretches > 0 ? $", {sp.SilentStretches} silent stretch left out" : "");

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
    catch { return false; }
}

// The start of a GGUF file: magic, version, no tensors, and the given
// metadata. Values: string, uint, float, bool, string[] and int[] arrays, and
// string[][] for an array of arrays.
static byte[] Gguf(uint version, params (string Key, object Value)[] kvs)
{
    var ms = new MemoryStream();
    using (var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
    {
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); bw.Write((ulong)b.Length); bw.Write(b); }
        bw.Write(0x46554747u); bw.Write(version); bw.Write(0UL); bw.Write((ulong)kvs.Length);
        foreach (var (key, value) in kvs)
        {
            Str(key);
            switch (value)
            {
                case string s: bw.Write(8u); Str(s); break;
                case uint u: bw.Write(4u); bw.Write(u); break;
                case float f: bw.Write(6u); bw.Write(f); break;
                case bool b: bw.Write(7u); bw.Write(b); break;
                case string[] arr: bw.Write(9u); bw.Write(8u); bw.Write((ulong)arr.Length); foreach (var a in arr) Str(a); break;
                case int[] ints: bw.Write(9u); bw.Write(5u); bw.Write((ulong)ints.Length); foreach (var i in ints) bw.Write(i); break;
                case string[][] nested:
                    bw.Write(9u); bw.Write(9u); bw.Write((ulong)nested.Length);
                    foreach (var inner in nested) { bw.Write(8u); bw.Write((ulong)inner.Length); foreach (var a in inner) Str(a); }
                    break;
                default: throw new ArgumentException($"no GGUF encoding for {value.GetType().Name}");
            }
        }
    }
    return ms.ToArray();
}

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
                   List<(string Name, string Value)> Fields, byte[]? File, bool Chunked,
                   string Method = "POST", string Query = "", string Host = "")
{
    public string? Get(string name) => Fields.FirstOrDefault(f => f.Name == name).Value;
}
