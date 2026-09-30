// The real-dictation corpus through the cloud providers, for _score_corpus.ps1.
//
// Sends the owner's REAL clinical dictations (%APPDATA%\.WhisperInk\ab-corpus\wav)
// to each provider in the roster below, through the SHIPPING transcriber classes,
// so each request is what WhisperInk itself would send. Writes a CSV in the format
// _local_bias_ab.ps1 writes (Run, Model, Backend, Clip, Bias, Rep, Ms, Recited,
// Text, plus Warning), so `_score_corpus.ps1 -Csv local.csv,cloud.csv` ranks them
// together.
//
//   dotnet run -c Release -- --dry                  what would be sent, and to whom; sends nothing
//   dotnet run -c Release -- [id,id,...] [--reps N] [--out file.csv]     (--reps overrides every provider's own count)
//                            [--wav <folder>] [--eleven-conds none,shared,BIAS]
//   (--wav points at another set of WAVs; --eleven-conds sets the two ElevenLabs runs' conditions, where "shared" sends
//    only the shared list, without the provider's own keyterms)
//
// EVERY REAL RUN UPLOADS CLINICAL AUDIO to those providers and bills it. Keys come
// from %APPDATA%\.WhisperInk\config.json and are never printed. The console shows no
// transcript text; the CSV does, so it goes under %APPDATA%, not into the repo (which
// is in OneDrive). A provider that fails three requests in a row is abandoned.
//
// Conditions: "none" sends no list; "BIAS" sends the shared Context Bias list, which
// each provider routes to its own field. ElevenLabs is the exception, because the app
// always sends its own long Scribe list too: "BIAS" is the app's real request (the
// shared list plus the provider's keyterms) and "none" sends no keyterms at all.
// Omi's Edge model takes no list, so it runs "none" only. Timing is one POST per take
// (the app streams ElevenLabs uploads from the key press, so its real latency is lower).
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WhisperInk;

bool dry = args.Contains("--dry");
string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk");
string outCsv = ArgStr("--out") ?? Path.Combine(root, "ab-corpus", "results", $"corpus-cloud-{DateTime.Now:yyyy-MM-dd}.csv");
string logPath = Path.ChangeExtension(outCsv, ".log");
// Provider ids are the bare arguments: not a flag, and not the value that follows a flag.
var flagsWithValue = new HashSet<string> { "--reps", "--out", "--wav", "--eleven-conds", "--terms", "--terms-name" };
var only = args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && flagsWithValue.Contains(args[i - 1])))
               .SelectMany(a => a.Split(',')).Where(a => a.Length > 0).ToHashSet();

// Live candidates only. Left out on purpose: Modulate and Smallest.ai (rejected), Deepgram's
// general model (Medical is strictly better), Reson8 (its list hurts), and the providers with no key.
// Reps: one upload per condition is enough for a hosted model; the two ElevenLabs runs send two, because the
// reference IS Scribe Medical's text, so a rerun says how far it reproduces (the metric's noise floor).
var roster = new (string Id, string Run, string[] Conditions, int Reps)[]
{
    ("omi-medical",        "omi flagship",                 new[] { "none", "BIAS" }, 1),
    ("omi-medical-edge",   "omi edge",                     new[] { "none" },         1),
    ("deepgram-medical",   "deepgram nova-3 medical",      new[] { "none", "BIAS" }, 1),
    ("soniox",             "soniox async v5",              new[] { "none", "BIAS" }, 1),
    ("google-chirp3",      "google chirp 3",               new[] { "none", "BIAS" }, 1),
    ("elevenlabs",         "elevenlabs scribe v2",         new[] { "none", "BIAS" }, 2),
    ("elevenlabs-medical", "elevenlabs scribe v2 medical", new[] { "none", "BIAS" }, 2),
    // Added 2026-09-29. Mistral keeps API inputs 30 days and offers no BAA: run it on the scripted sets
    // (--wav <script2 wav>), not on real dictations, unless the owner says otherwise.
    ("mistral",            "mistral voxtral mini transcribe 2", new[] { "none", "BIAS" }, 1),
};
if (ArgInt("--reps", 0) > 0) roster = roster.Select(r => (r.Id, r.Run, r.Conditions, ArgInt("--reps", 1))).ToArray();
if (only.Count > 0) roster = roster.Where(r => only.Contains(r.Id)).ToArray();
// --eleven-conds none,shared,BIAS: the conditions for the two ElevenLabs runs. "shared" sends only the shared list,
// without the provider's own keyterms, so "none", "shared" and "BIAS" show what each list adds.
string? elevenConds = ArgStr("--eleven-conds");
if (elevenConds != null)
    roster = roster.Select(r => r.Id.StartsWith("elevenlabs") ? (r.Id, r.Run, elevenConds.Split(','), r.Reps) : r).ToArray();
// --terms <file> [--terms-name x]: a keyterm list to try on the ElevenLabs and Mistral runs, one term per line, sent INSTEAD of both the
// shared list and the provider's own keyterms. Only those runs are made, as condition "custom", written to the CSV as custom-<name>.
string? termsFile = ArgStr("--terms");
string termsName = ArgStr("--terms-name") ?? "custom";
var customTerms = termsFile == null ? new List<string>()
    : File.ReadAllLines(termsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
if (termsFile != null)
    roster = roster.Where(r => r.Id.StartsWith("elevenlabs") || r.Id == "mistral").Select(r => (r.Id, r.Run, new[] { "custom" }, r.Reps)).ToArray();

using var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json")));
var shared = cfg.RootElement.GetProperty("ContextBiasTerms").EnumerateArray()
                .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
var entries = cfg.RootElement.GetProperty("Providers").EnumerateArray().ToList();

// --wav <folder>: another set of WAVs (default: the first real-dictation corpus).
string wavDir = ArgStr("--wav") ?? Path.Combine(root, "ab-corpus", "wav");
var wavs = Directory.GetFiles(wavDir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToList();
// A WAV another process is uploading at this moment can be locked for a second (the local A/B reads the same folder).
static byte[] ReadRetry(string path)
{
    for (int i = 0; ; i++)
    {
        try { return File.ReadAllBytes(path); }
        catch (IOException) when (i < 40) { Thread.Sleep(250); }
    }
}
var clips = wavs.Select(f => (Name: Path.GetFileNameWithoutExtension(f), Bytes: ReadRetry(f))).ToList();
double corpusSeconds = clips.Sum(c => WavSeconds(c.Bytes));

// A shipped preset, with what the machine's config.json changes about it (the key, the Scribe list, ...).
ApiProvider Build(string id, bool keytermsOn)
{
    var p = ApiProvider.CreateDefaults().Single(x => x.Id == id);
    var e = entries.FirstOrDefault(x => x.TryGetProperty("Id", out var i) && i.GetString() == id);
    if (e.ValueKind != JsonValueKind.Object) return p;
    string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    void Set(string n, Action<string> f) { var s = S(n); if (s.Length > 0) f(s); }
    Set("ApiKey", s => p.ApiKey = s);
    Set("BaseUrl", s => p.BaseUrl = s);
    Set("TranscriptionEndpoint", s => p.TranscriptionEndpoint = s);
    Set("AuthHeaderName", s => p.AuthHeaderName = s);
    Set("ModelFieldName", s => p.ModelFieldName = s);
    Set("TranscriptionModel", s => p.TranscriptionModel = s);
    Set("Language", s => p.Language = s);
    p.ScribeKeytermsRaw = keytermsOn ? S("ScribeKeytermsRaw") : "";
    if (e.TryGetProperty("TagAudioEvents", out var t) && t.ValueKind is JsonValueKind.True or JsonValueKind.False) p.TagAudioEvents = t.GetBoolean();
    if (e.TryGetProperty("NoVerbatim", out var nv) && nv.ValueKind is JsonValueKind.True or JsonValueKind.False) p.NoVerbatim = nv.GetBoolean();
    if (e.TryGetProperty("DeepgramExtraParams", out var dx) && dx.ValueKind == JsonValueKind.Object)
        foreach (var kv in dx.EnumerateObject())
            if (kv.Value.ValueKind == JsonValueKind.String) p.DeepgramExtraParams[kv.Name] = kv.Value.GetString() ?? "";
    return p;
}

string Host(ApiProvider p)
{
    foreach (var u in new[] { p.ResolvedTranscriptionUrl, p.BaseUrl })
        if (Uri.TryCreate(u, UriKind.Absolute, out var uri)) return uri.Host;
    return "?";
}

// ── What would be sent ──────────────────────────────────────────────────
Console.WriteLine($"corpus: {clips.Count} clips, {corpusSeconds:F0} s of real clinical dictation; shared list {shared.Count} terms");
Console.WriteLine($"{"run",-30} {"host",-28} {"model",-22} {"key",-4} {"terms sent",-11} {"conditions",-11} {"reps",-5} {"audio sent"}");
foreach (var (id, run, conds, nReps) in roster)
{
    var p = Build(id, true);
    int scribe = p.ScribeKeytermsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    string terms = p.IsElevenLabs ? string.Join("/", conds.Select(c => c == "BIAS" ? $"{shared.Count}+{scribe}" : c == "shared" ? $"{shared.Count}" : c == "custom" ? $"{customTerms.Count} (custom)" : "0"))
                                  : (conds.Contains("BIAS") ? $"{shared.Count}" : "0");
    Console.WriteLine($"{run,-30} {Host(p),-28} {p.TranscriptionModel,-22} {(p.ApiKey.Length > 0 ? "yes" : "NO"),-4} {terms,-11} {string.Join("+", conds),-11} {nReps,-5} {corpusSeconds * conds.Length * nReps:F0} s");
}
Console.WriteLine($"total audio: {roster.Sum(r => corpusSeconds * r.Conditions.Length * r.Reps):F0} s to {roster.Select(r => Host(Build(r.Id, true))).Distinct().Count()} hosts");
if (dry) { Console.WriteLine("--dry: nothing sent."); return 0; }

// ── Run ─────────────────────────────────────────────────────────────────
Directory.CreateDirectory(Path.GetDirectoryName(outCsv)!);
var logLock = new object();
void Log(string s) { lock (logLock) File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); }
var http = new HttpClient { Timeout = TranscriptionDeadline.HttpBackstop };

ITranscriber Make(ApiProvider p) => p.TranscriberKind switch
{
    TranscriberKind.Omi => new OmiTranscriber(p, http, Log),
    TranscriberKind.Deepgram => new DeepgramTranscriber(p, http, Log),
    TranscriberKind.Soniox => new SonioxTranscriber(p, http, Log),
    TranscriberKind.GoogleChirp3 => new GoogleChirp3Transcriber(p, Log),
    _ => new HttpTranscriber(p, http, Log),
};

static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
using var w = new StreamWriter(outCsv, append: false, new UTF8Encoding(false)) { AutoFlush = true };
w.WriteLine("Run,Model,Backend,Clip,Bias,Rep,Ms,Recited,Text,Warning");
var summary = new List<string>();

foreach (var (id, run, conds, reps) in roster)
{
    foreach (string cond in conds)
    {
        var prov = Build(id, cond == "BIAS");
        if (prov.ApiKey.Length == 0) { Console.WriteLine($"\n== {run} [{cond}]: no key in config.json, skipped"); continue; }
        ITranscriber t;
        try { t = Make(prov); }
        catch (Exception ex) { Console.WriteLine($"\n== {run} [{cond}]: could not start ({ex.GetType().Name}: {ex.Message}), skipped"); continue; }
        using (t)
        {
            IReadOnlyList<string> terms = cond == "none" ? Array.Empty<string>() : cond == "custom" ? customTerms : shared;
            string biasLabel = cond == "custom" ? "custom-" + termsName : cond;   // what the CSV's Bias column says
            Console.WriteLine($"\n== {run} [{cond}] ({prov.TranscriptionModel}, {Host(prov)})");
            var times = new List<long>(); int fails = 0, warned = 0, streak = 0;
            foreach (var (name, wav) in clips)
            {
                if (streak >= 3) break;
                foreach (int rep in Enumerable.Range(1, reps))
                {
                    if (streak >= 3) break;
                    double secs = WavSeconds(wav);
                    string text; string warning = "";
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        using var cts = new CancellationTokenSource(TranscriptionDeadline.For(prov, secs));
                        string? r = await t.TranscribeAsync(wav, terms, cts.Token);
                        text = r ?? "<error: no result, see the .log>";
                        if (r != null && t is ITranscriptWarning tw && tw.LastWarning is { } lw) warning = lw;
                    }
                    catch (Exception ex) { text = $"<error: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}>"; }
                    sw.Stop();
                    bool failed = text.StartsWith("<error");
                    streak = failed ? streak + 1 : 0;
                    if (failed) fails++; else times.Add(sw.ElapsedMilliseconds);
                    if (warning.Length > 0) warned++;
                    bool recited = !failed && shared.Count(x => Regex.IsMatch(text, $@"(?i)(?<!\w){Regex.Escape(x)}(?!\w)")) >= 4;
                    w.WriteLine(string.Join(",", new[] { Csv(run), Csv(prov.TranscriptionModel), "cloud", Csv(name), Csv(biasLabel), rep.ToString(),
                                                          sw.ElapsedMilliseconds.ToString(), recited ? "True" : "False", Csv(text), Csv(warning) }));
                    Console.WriteLine($"   {name,-26} rep{rep} {sw.ElapsedMilliseconds,6} ms  {(failed ? "FAILED" : text.Length + " chars")}{(warning.Length > 0 ? "  [warning]" : "")}");
                }
            }
            if (streak >= 3) Console.WriteLine($"   three failures in a row: {run} [{cond}] abandoned (see {Path.GetFileName(logPath)})");
            times.Sort();
            summary.Add($"{run,-30} {cond,-4} {times.Count,3} ok {fails,2} failed {warned,2} warned  median {(times.Count > 0 ? times[times.Count / 2] : 0),5} ms");
        }
    }
}
Console.WriteLine("\n== summary");
summary.ForEach(Console.WriteLine);
Console.WriteLine($"\nwrote {outCsv}");
return 0;

// ── helpers ─────────────────────────────────────────────────────────────
int ArgInt(string name, int fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) && v > 0 ? v : fallback;
}
string? ArgStr(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
static double WavSeconds(byte[] w)
{
    if (w.Length < 44) return 0;
    int pos = 12, sr = 16000, ch = 1, bits = 16;
    while (pos + 8 <= w.Length)
    {
        string id = Encoding.ASCII.GetString(w, pos, 4);
        int size = BitConverter.ToInt32(w, pos + 4);
        if (id == "fmt ") { ch = BitConverter.ToInt16(w, pos + 10); sr = BitConverter.ToInt32(w, pos + 12); bits = BitConverter.ToInt16(w, pos + 22); }
        else if (id == "data") return Math.Min(size, w.Length - pos - 8) / (double)(sr * ch * bits / 8);
        pos += 8 + size + (size & 1);
    }
    return 0;
}
