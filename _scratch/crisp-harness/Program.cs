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
Console.WriteLine("\n== 1. HttpTranscriber vs a local fake server ==");
const string Prefix = "http://127.0.0.1:18999/";
var requests = new List<FakeRequest>();
string reply = "{}";
int replyDelayMs = 0;
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
                await ctx.Request.InputStream.CopyToAsync(ms);
                var req = new FakeRequest(ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["xi-api-key"],
                    ctx.Request.Headers["X-API-Key"], ctx.Request.Headers["Authorization"],
                    ParseMultipart(ctx.Request.ContentType ?? "", ms.ToArray()));
                lock (requests) requests.Add(req);
                string body = reply;
                if (replyDelayMs > 0) await Task.Delay(replyDelayMs);
                byte[] buf = Encoding.UTF8.GetBytes(body);
                ctx.Response.StatusCode = 200;
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

static List<(string Name, string Value)> ParseMultipart(string contentType, byte[] body)
{
    var result = new List<(string Name, string Value)>();
    var m = Regex.Match(contentType, "boundary=\"?([^\";]+)\"?");
    if (!m.Success) return result;
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
        value = headers.Contains("filename")
            ? $"<{value.Length} bytes>"
            : Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(value));
        result.Add((nm.Groups[1].Value, value));
    }
    return result;
}

record FakeRequest(string Path, string? XiApiKey, string? XApiKey, string? Authorization, List<(string Name, string Value)> Fields)
{
    public string? Get(string name) => Fields.FirstOrDefault(f => f.Name == name).Value;
}
