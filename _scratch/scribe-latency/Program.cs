// ElevenLabs Scribe latency probe — the live API, interleaved rounds. Every
// number in CLAUDE.md's "Cloud connections & latency (2026-09)" came from here.
//
//   dotnet run -c Release -- <mode> [rounds|gapSeconds] [iterations]
//
//   variants : the shipped request (HttpTranscriber: WAV + the real keyterms)
//              vs the same with NO keyterms
//              vs file_format=pcm_s16le_16 (raw PCM, keyterms kept)
//   conn     : warm pooled connection vs cold (fresh TCP+TLS)
//              vs cold-but-prewarmed (a throwaway GET while "speaking")
//   idle     : does a pooled connection survive an N-second gap with .NET's
//              default idle timeout vs a 10 min one?  (idle 300 1)
//   stream   : today's upload-after-release vs streaming the audio up while
//              "speaking" (chunked, raw PCM), after a pause and warm
//   medical  : scribe_v2 vs scribe_v2_medical latency, then both (with and
//              without keyterms) on the user's own clips in _scratch\biasing\clips
//   medcheck : does scribe_v2_medical return the word timing TranscriptCoverage needs?
//   preset   : the shipped elevenlabs-medical preset, keyed the way LoadConfig
//              keys it (InheritFromSibling over the real config), one real request
//   shipstream : the shipping StreamedTranscription (audio fed like MicCapture
//              feeds it) vs pre-warm + POST at release, then whether a
//              cancelled stream is billed (the account's usage counter)
//
// Audio: CrispASR's public-domain jfk.wav (sibling clone), the harness's TTS
// speech.wav, and — medical mode only — the clinical test clips. Every run
// bills real audio; with >100 keyterms each request bills at least 20 s.
// The API key is read from %APPDATA%\.WhisperInk\config.json, never printed.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WhisperInk;

string mode = args.Length > 0 ? args[0] : "variants";
int rounds = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 6;

// ── Config ──────────────────────────────────────────────────────────────
string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk", "config.json");
using var cfgDoc = JsonDocument.Parse(File.ReadAllText(cfgPath));
var root = cfgDoc.RootElement;
var shared = root.GetProperty("ContextBiasTerms").EnumerateArray()
    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
var pe = root.GetProperty("Providers").EnumerateArray().First(e => e.GetProperty("Id").GetString() == "elevenlabs");
string S(string n) => pe.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
bool Bo(string n, bool d) => pe.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
ApiProvider Provider(bool rawKeyterms, string? model = null) => new()
{
    Id = "elevenlabs", Name = "ElevenLabs Scribe",
    BaseUrl = S("BaseUrl"), ApiKey = S("ApiKey"), TranscriptionEndpoint = S("TranscriptionEndpoint"),
    AuthHeaderName = S("AuthHeaderName"), ModelFieldName = S("ModelFieldName"),
    TranscriptionModel = model ?? S("TranscriptionModel"), Language = S("Language"),
    BiasMechanism = S("BiasMechanism"),
    ScribeKeytermsRaw = rawKeyterms ? S("ScribeKeytermsRaw") : "",
    TagAudioEvents = Bo("TagAudioEvents", false), NoVerbatim = Bo("NoVerbatim", true),
};
var pKt = Provider(true);
var pNoKt = Provider(false);
if (string.IsNullOrEmpty(pKt.ApiKey)) { Console.WriteLine("no ElevenLabs key in config.json"); return 1; }
var mergedKt = new List<string>(shared);
mergedKt.AddRange(pKt.ScribeKeytermsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
int ktCount = ApiProvider.ValidateKeyterms(mergedKt, out _).Count;
Console.WriteLine($"model={pKt.TranscriptionModel} lang={pKt.Language} keyterms={ktCount} no_verbatim={pKt.NoVerbatim} tag_audio_events={pKt.TagAudioEvents}");

// ── Audio ───────────────────────────────────────────────────────────────
static byte[] PcmOf(byte[] wav)
{
    int i = 12;
    while (i + 8 <= wav.Length)
    {
        string id = Encoding.ASCII.GetString(wav, i, 4);
        int len = BitConverter.ToInt32(wav, i + 4);
        if (id == "data") return wav.AsSpan(i + 8, Math.Min(len, wav.Length - i - 8)).ToArray();
        i += 8 + len + (len & 1);
    }
    throw new InvalidDataException("no data chunk");
}
static byte[] WavOf(byte[] pcm)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
    w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
    w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
    w.Flush();
    return ms.ToArray();
}
// The repo root is the first folder above the binary that holds WhisperInk.csproj.
string repoRoot = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(repoRoot, "WhisperInk.csproj")))
    repoRoot = Path.GetDirectoryName(repoRoot.TrimEnd(Path.DirectorySeparatorChar))
               ?? throw new DirectoryNotFoundException("run from inside the whisperinc repo");
var jfkPcm = PcmOf(File.ReadAllBytes(Path.Combine(repoRoot, "..", "CrispASR", "samples", "jfk.wav")));
var speechPcm = PcmOf(File.ReadAllBytes(Path.Combine(repoRoot, "_scratch", "crisp-harness", "speech.wav")));
var gap = new byte[16000]; // 0.5 s of silence
var jfk3Pcm = jfkPcm.Concat(gap).Concat(jfkPcm).Concat(gap).Concat(jfkPcm).ToArray();
var clips = new (string Name, byte[] Wav, byte[] Pcm)[]
{
    ($"speech {speechPcm.Length / 32000.0:F1}s", WavOf(speechPcm), speechPcm),
    ($"jfk {jfkPcm.Length / 32000.0:F1}s", WavOf(jfkPcm), jfkPcm),
    ($"jfk x3 {jfk3Pcm.Length / 32000.0:F1}s", WavOf(jfk3Pcm), jfk3Pcm),
};

// ── HTTP plumbing: count every new TCP connection ──────────────────────
int connects = 0;
HttpClient NewClient(TimeSpan? idle = null)
{
    var h = new SocketsHttpHandler
    {
        ConnectCallback = async (ctx, ct) =>
        {
            Interlocked.Increment(ref connects);
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await s.ConnectAsync(ctx.DnsEndPoint, ct); return new NetworkStream(s, ownsSocket: true); }
            catch { s.Dispose(); throw; }
        },
    };
    if (idle is { } t) h.PooledConnectionIdleTimeout = t;
    return new HttpClient(h) { Timeout = TimeSpan.FromMinutes(3) };
}

// The shipping code path, verbatim.
async Task<(long Ms, string Text, bool NewConn)> Shipped(HttpClient http, ApiProvider p, IReadOnlyList<string> bias, byte[] wav)
{
    var logs = new List<string>();
    var t = new HttpTranscriber(p, http, s => logs.Add(s));
    int c0 = Volatile.Read(ref connects);
    var sw = Stopwatch.StartNew();
    string? text = await t.TranscribeAsync(wav, bias, CancellationToken.None);
    sw.Stop();
    if (text == null)
        Console.WriteLine("   ! shipped request failed: " + string.Join(" | ", logs.Where(l => l.Contains("HTTP ") || l.Contains("error"))));
    return (sw.ElapsedMilliseconds, text ?? "<null>", Volatile.Read(ref connects) != c0);
}

// Identical fields + file_format=pcm_s16le_16 and the raw PCM (no header).
bool printedHeaders = false;
async Task<(long Ms, string Text, bool NewConn)> Pcm(HttpClient http, ApiProvider p, IReadOnlyList<string> bias, byte[] pcm)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, p.ResolvedTranscriptionUrl);
    req.Headers.Add(p.AuthHeaderName, p.ApiKey);
    using var content = new MultipartFormDataContent();
    content.Add(new StringContent(p.TranscriptionModel), p.ResolvedModelField);
    content.Add(new StringContent(p.Language), "language_code");
    content.Add(new StringContent("0"), "temperature");
    var merged = new List<string>(bias);
    if (!string.IsNullOrWhiteSpace(p.ScribeKeytermsRaw))
        merged.AddRange(p.ScribeKeytermsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    foreach (var term in ApiProvider.ValidateKeyterms(merged, out _)) content.Add(new StringContent(term), "keyterms");
    content.Add(new StringContent("false"), "diarize");
    content.Add(new StringContent("1"), "num_speakers");
    content.Add(new StringContent("word"), "timestamps_granularity");
    content.Add(new StringContent(p.TagAudioEvents ? "true" : "false"), "tag_audio_events");
    content.Add(new StringContent(p.NoVerbatim ? "true" : "false"), "no_verbatim");
    content.Add(new StringContent("pcm_s16le_16"), "file_format");
    var file = new ByteArrayContent(pcm);
    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    content.Add(file, "file", "audio.pcm");
    req.Content = content;

    int c0 = Volatile.Read(ref connects);
    var sw = Stopwatch.StartNew();
    using var resp = await http.SendAsync(req);
    string body = await resp.Content.ReadAsStringAsync();
    sw.Stop();
    if (!printedHeaders)
    {
        printedHeaders = true;
        Console.WriteLine("   response headers: " + string.Join(", ",
            resp.Headers.Concat(resp.Content.Headers)
                .Where(h => !h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                .Select(h => $"{h.Key}={string.Join(";", h.Value)}")));
    }
    string text;
    if (resp.IsSuccessStatusCode)
    {
        using var d = JsonDocument.Parse(body);
        text = HttpTranscriber.CleanElevenLabsText(d.RootElement.GetProperty("text").GetString());
    }
    else
    {
        text = "<HTTP " + (int)resp.StatusCode + "> " + body[..Math.Min(300, body.Length)];
        Console.WriteLine("   ! pcm request failed: " + text);
    }
    return (sw.ElapsedMilliseconds, text, Volatile.Read(ref connects) != c0);
}

// Streamed upload: the request starts at key-PRESS and the audio is fed in
// as the mic would deliver it (400 ms pre-roll at once, then 50 ms buffers
// in real time), chunked, raw PCM + file_format=pcm_s16le_16 (no WAV header
// length to know up front). Timed from "release" — the last buffer handed
// over — to the parsed response, which is what the user waits for.
async Task<(long Ms, string Text, bool NewConn)> Streamed(HttpClient http, ApiProvider p, IReadOnlyList<string> bias, byte[] pcm)
{
    var pipe = new System.IO.Pipelines.Pipe();
    using var req = new HttpRequestMessage(HttpMethod.Post, p.ResolvedTranscriptionUrl);
    req.Headers.Add(p.AuthHeaderName, p.ApiKey);
    using var content = new MultipartFormDataContent();
    content.Add(new StringContent(p.TranscriptionModel), p.ResolvedModelField);
    content.Add(new StringContent(p.Language), "language_code");
    content.Add(new StringContent("0"), "temperature");
    var merged = new List<string>(bias);
    if (!string.IsNullOrWhiteSpace(p.ScribeKeytermsRaw))
        merged.AddRange(p.ScribeKeytermsRaw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    foreach (var term in ApiProvider.ValidateKeyterms(merged, out _)) content.Add(new StringContent(term), "keyterms");
    content.Add(new StringContent("false"), "diarize");
    content.Add(new StringContent("1"), "num_speakers");
    content.Add(new StringContent("word"), "timestamps_granularity");
    content.Add(new StringContent(p.TagAudioEvents ? "true" : "false"), "tag_audio_events");
    content.Add(new StringContent(p.NoVerbatim ? "true" : "false"), "no_verbatim");
    content.Add(new StringContent("pcm_s16le_16"), "file_format");
    var file = new StreamContent(pipe.Reader.AsStream());
    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    content.Add(file, "file", "audio.pcm");
    req.Content = content;

    int c0 = Volatile.Read(ref connects);
    var send = http.SendAsync(req);          // connects + sends the fields while "speaking"
    const int bytesPerMs = 32;               // 16 kHz mono 16-bit
    int preRoll = Math.Min(pcm.Length, 400 * bytesPerMs);
    await pipe.Writer.WriteAsync(pcm.AsMemory(0, preRoll));
    var clock = Stopwatch.StartNew();
    int pos = preRoll;
    while (pos < pcm.Length)
    {
        int n = Math.Min(50 * bytesPerMs, pcm.Length - pos);
        long due = (pos - preRoll + n) / bytesPerMs;   // when this buffer exists
        long wait = due - clock.ElapsedMilliseconds;
        if (wait > 0) await Task.Delay((int)wait);
        await pipe.Writer.WriteAsync(pcm.AsMemory(pos, n));
        pos += n;
    }
    await pipe.Writer.CompleteAsync();
    var sw = Stopwatch.StartNew();           // key released, last buffer handed over
    string text;
    try
    {
        using var resp = await send;
        string body = await resp.Content.ReadAsStringAsync();
        sw.Stop();
        if (resp.IsSuccessStatusCode)
        {
            using var d = JsonDocument.Parse(body);
            text = HttpTranscriber.CleanElevenLabsText(d.RootElement.GetProperty("text").GetString());
        }
        else
        {
            text = "<HTTP " + (int)resp.StatusCode + "> " + body[..Math.Min(300, body.Length)];
            Console.WriteLine("   ! streamed request failed: " + text);
        }
    }
    catch (Exception ex)
    {
        sw.Stop();
        text = "<" + ex.GetType().Name + "> " + ex.Message;
        Console.WriteLine("   ! streamed request threw: " + text);
    }
    return (sw.ElapsedMilliseconds, text, Volatile.Read(ref connects) != c0);
}

var samples = new Dictionary<string, List<long>>();
var textsSeen = new Dictionary<string, Dictionary<string, int>>();
void Record(string key, (long Ms, string Text, bool NewConn) res)
{
    if (!samples.TryGetValue(key, out var l)) samples[key] = l = new();
    l.Add(res.Ms);
    if (!textsSeen.TryGetValue(key, out var t)) textsSeen[key] = t = new();
    t[res.Text] = t.GetValueOrDefault(res.Text) + 1;
    Console.WriteLine($"   {key,-34} {res.Ms,6} ms{(res.NewConn ? "  (NEW connection)" : "")}");
}
void Report(string title)
{
    Console.WriteLine($"\n== {title} ==");
    Console.WriteLine($"{"condition",-34} {"n",3} {"median",7} {"mean",7} {"min",6} {"max",6}");
    foreach (var (k, l) in samples)
    {
        var s = l.OrderBy(x => x).ToList();
        double med = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
        Console.WriteLine($"{k,-34} {s.Count,3} {med,7:F0} {s.Average(),7:F0} {s.First(),6} {s.Last(),6}");
    }
    Console.WriteLine("\ntranscripts per condition (count x text):");
    foreach (var (k, t) in textsSeen)
        foreach (var (text, n) in t)
            Console.WriteLine($"  {k,-34} {n}x  {text[..Math.Min(110, text.Length)]}");
}

if (mode == "variants")
{
    using var http = NewClient();
    Console.WriteLine("warm-up (not recorded)…");
    await Shipped(http, pNoKt, Array.Empty<string>(), clips[0].Wav);
    var variants = new (string Name, Func<(string Name, byte[] Wav, byte[] Pcm), Task<(long, string, bool)>> Run)[]
    {
        ("wav+keyterms (shipped)", c => Shipped(http, pKt, shared, c.Wav)),
        ("wav, no keyterms",       c => Shipped(http, pNoKt, Array.Empty<string>(), c.Wav)),
        ("pcm_s16le_16+keyterms",  c => Pcm(http, pKt, shared, c.Pcm)),
    };
    for (int round = 0; round < rounds; round++)
    {
        Console.WriteLine($"-- round {round + 1}/{rounds}");
        foreach (var clip in clips)
            for (int v = 0; v < variants.Length; v++)
            {
                var variant = variants[(v + round) % variants.Length]; // rotate the order each round
                Record($"{clip.Name} | {variant.Name}", await variant.Run(clip));
                await Task.Delay(600);
            }
    }
    Report("variants (warm connection)");
}
else if (mode == "conn")
{
    var clip = clips[0];
    using var warmHttp = NewClient();
    Console.WriteLine("warm-up (not recorded)…");
    await Shipped(warmHttp, pKt, shared, clip.Wav);
    for (int round = 0; round < rounds; round++)
    {
        Console.WriteLine($"-- round {round + 1}/{rounds}");
        await Task.Delay(1500);
        Record("warm (pooled)", await Shipped(warmHttp, pKt, shared, clip.Wav));

        using (var cold = NewClient())
            Record("cold (fresh TCP+TLS)", await Shipped(cold, pKt, shared, clip.Wav));

        using (var pre = NewClient())
        {
            // What a pre-warm at key-press would do: a throwaway request
            // while the user is still talking, then the real POST.
            var swPre = Stopwatch.StartNew();
            using (var resp = await pre.GetAsync(pKt.BaseUrl.TrimEnd('/') + "/")) { }
            Console.WriteLine($"   (pre-warm GET took {swPre.ElapsedMilliseconds} ms)");
            await Task.Delay(1500);
            Record("cold + pre-warmed during speech", await Shipped(pre, pKt, shared, clip.Wav));
        }
    }
    Report("connection state (short clip, shipped request)");
}
else if (mode == "stream")
{
    // Today's path vs a streamed upload, each both after a pause (fresh
    // connection) and back-to-back (pooled connection).
    using var warmHttp = NewClient();
    Console.WriteLine("warm-up (not recorded)…");
    await Shipped(warmHttp, pKt, shared, clips[0].Wav);
    foreach (var clip in clips.Take(2))
        for (int round = 0; round < rounds; round++)
        {
            Console.WriteLine($"-- {clip.Name} round {round + 1}/{rounds}");
            using (var c = NewClient()) Record($"{clip.Name} | today, after a pause", await Shipped(c, pKt, shared, clip.Wav));
            using (var c = NewClient()) Record($"{clip.Name} | streamed, after a pause", await Streamed(c, pKt, shared, clip.Pcm));
            await Task.Delay(800);
            Record($"{clip.Name} | today, warm", await Shipped(warmHttp, pKt, shared, clip.Wav));
            await Task.Delay(800);
            Record($"{clip.Name} | streamed, warm", await Streamed(warmHttp, pKt, shared, clip.Pcm));
        }
    // Long takes: does a real-time-paced upload survive the load balancer?
    var longPcm = Enumerable.Range(0, 9).SelectMany(_ => jfkPcm.Concat(gap)).ToArray();
    foreach (var (name, pcm) in new[] { ("jfk x3 34s", jfk3Pcm), ($"jfk x9 {longPcm.Length / 32000.0:F0}s", longPcm) })
    {
        Console.WriteLine($"-- long take {name}");
        using (var c = NewClient()) Record($"{name} | today, after a pause", await Shipped(c, pKt, shared, WavOf(pcm)));
        using (var c = NewClient()) Record($"{name} | streamed, after a pause", await Streamed(c, pKt, shared, pcm));
    }
    Report("streamed upload vs today (ms from release to text)");
}
else if (mode == "medical")
{
    var pMed = Provider(true, "scribe_v2_medical");
    var pMedNoKt = Provider(false, "scribe_v2_medical");
    using var http = NewClient();
    Console.WriteLine("warm-up (not recorded)…");
    await Shipped(http, pKt, shared, clips[0].Wav);
    await Shipped(http, pMed, shared, clips[0].Wav);
    for (int round = 0; round < rounds; round++)
    {
        Console.WriteLine($"-- round {round + 1}/{rounds}");
        foreach (var clip in clips.Take(2))
        {
            bool medFirst = round % 2 == 1;
            for (int k = 0; k < 2; k++)
            {
                bool med = (k == 0) == medFirst;
                Record($"{clip.Name} | {(med ? "scribe_v2_medical" : "scribe_v2")} +kt",
                       await Shipped(http, med ? pMed : pKt, shared, clip.Wav));
                await Task.Delay(600);
            }
        }
    }
    Report("scribe_v2 vs scribe_v2_medical latency (warm, keyterms on)");

    // The user's own test recordings of the hard terms (_scratch\biasing).
    samples.Clear(); textsSeen.Clear();
    string clipDir = Path.Combine(repoRoot, "_scratch", "biasing", "clips");
    Console.WriteLine("\n== clinical clips ==");
    foreach (var f in Directory.GetFiles(clipDir, "*.wav").OrderBy(x => x))
    {
        var wav = File.ReadAllBytes(f);
        string name = Path.GetFileNameWithoutExtension(f);
        foreach (var (label, p, bias) in new (string, ApiProvider, IReadOnlyList<string>)[]
                 {
                     ("scribe_v2 +kt (today)", pKt, shared),
                     ("scribe_v2 no kt", pNoKt, Array.Empty<string>()),
                     ("medical +kt", pMed, shared),
                     ("medical no kt", pMedNoKt, Array.Empty<string>()),
                 })
        {
            var res = await Shipped(http, p, bias, wav);
            Console.WriteLine($"  {name,-17} {label,-22} {res.Ms,5} ms  {res.Text}");
            await Task.Delay(400);
        }
    }
}
else if (mode == "preset")
{
    // The shipped elevenlabs-medical preset, given its key the way LoadConfig
    // does (InheritFromSibling over the real config), then a real request.
    var existing = new List<ApiProvider>();
    foreach (var e in root.GetProperty("Providers").EnumerateArray())
    {
        string F(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B2(string n, bool d) => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : d;
        existing.Add(new ApiProvider
        {
            Id = F("Id"), Name = F("Name"), BaseUrl = F("BaseUrl"), ApiKey = F("ApiKey"),
            TranscriptionEndpoint = F("TranscriptionEndpoint"), AuthHeaderName = F("AuthHeaderName"),
            ModelFieldName = F("ModelFieldName"), TranscriptionModel = F("TranscriptionModel"),
            BiasMechanism = F("BiasMechanism"), ScribeKeytermsRaw = F("ScribeKeytermsRaw"), Language = F("Language") is { Length: > 0 } l ? l : "en",
            TagAudioEvents = B2("TagAudioEvents", false), NoVerbatim = B2("NoVerbatim", true),
            TranscriberKind = Enum.TryParse<TranscriberKind>(F("TranscriberKind"), true, out var k) ? k : TranscriberKind.Http,
        });
    }
    var medPreset = ApiProvider.CreateDefaults().Single(p => p.Id == "elevenlabs-medical");
    var sib = ApiProvider.InheritFromSibling(medPreset, existing.Where(p => p.Id != medPreset.Id));
    int rawLines = medPreset.ScribeKeytermsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    Console.WriteLine($"preset inherited from: {sib?.Id ?? "(nothing)"}; key set: {medPreset.ApiKey.Length > 0}; Scribe keyterm lines: {rawLines}; auth={medPreset.ResolvedAuthHeaderName} field={medPreset.ResolvedModelField} bias={medPreset.ResolvedBiasMechanism}");

    using var http = NewClient();
    foreach (var (label, p) in new[] { ("elevenlabs-medical preset", medPreset) })
    {
        var logs = new List<string>();
        var t = new HttpTranscriber(p, http, s => logs.Add(s));
        var sw = Stopwatch.StartNew();
        var text = await t.TranscribeAsync(clips[1].Wav, shared, CancellationToken.None);
        Console.WriteLine($"{label}: auth={p.ResolvedAuthHeaderName} bias={p.ResolvedBiasMechanism} -> {(text == null ? "FAILED" : "OK")} in {sw.ElapsedMilliseconds} ms: {text}");
        foreach (var l in logs.Where(l => l.StartsWith("[keyterms] sending") || l.StartsWith("[scribe] last") || (l.Contains("HTTP ") && !l.Contains("HTTP 200"))))
            Console.WriteLine("   " + l);
    }
}
else if (mode == "shipstream")
{
    // The SHIPPING StreamedTranscription against the live API, on the
    // provider the app uses (elevenlabs-medical, keyed like LoadConfig keys
    // it). Audio is fed the way MicCapture feeds it — the 400 ms pre-roll at
    // once, then 50 ms buffers in real time — and timed from the release.
    // Compared with what the app does when streaming is off: open the
    // connection at the press (PrewarmConnection), POST the WAV at release.
    var med = ApiProvider.CreateDefaults().Single(p => p.Id == "elevenlabs-medical");
    ApiProvider.InheritFromSibling(med, new[] { Provider(true) });
    Console.WriteLine($"provider: {med.Id} model={med.TranscriptionModel} key={(med.ApiKey.Length > 0)}");

    async Task<(long Ms, string Text, bool NewConn)> Streamed2(HttpClient c, byte[] pcm)
    {
        var t = new HttpTranscriber(med, c, _ => { });
        var s = t.BeginStreamedTranscription(shared);
        int pre = Math.Min(pcm.Length, 400 * 32);
        s.Append(pcm, 0, pre);
        var clock = Stopwatch.StartNew();
        for (int pos = pre; pos < pcm.Length; pos += 1600)
        {
            int n = Math.Min(1600, pcm.Length - pos);
            long wait = (pos - pre + n) / 32 - clock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay((int)wait);
            s.Append(pcm, pos, n);
        }
        await Task.Delay(40);                       // the post-roll wait
        int c0 = Volatile.Read(ref connects);
        var sw = Stopwatch.StartNew();              // release
        var o = await s.FinishAsync(pcm.Length, CancellationToken.None);
        sw.Stop();
        s.Dispose();
        return (sw.ElapsedMilliseconds, o.FallBack ? "<FALLBACK: " + o.Reason + ">" : o.Text ?? "<null>", Volatile.Read(ref connects) != c0);
    }
    async Task<(long Ms, string Text, bool NewConn)> PrewarmedPost(HttpClient c, byte[] pcm)
    {
        var origin = new Uri(new Uri(med.ResolvedTranscriptionUrl).GetLeftPart(UriPartial.Authority) + "/");
        _ = Task.Run(async () => { try { using var r = await c.SendAsync(new HttpRequestMessage(HttpMethod.Head, origin)); } catch { } });
        await Task.Delay(pcm.Length / 32 + 40);    // the take being spoken
        var t = new HttpTranscriber(med, c, _ => { });
        int c0 = Volatile.Read(ref connects);
        var sw = Stopwatch.StartNew();              // release
        var text = await t.TranscribeAsync(WavOf(pcm), shared, CancellationToken.None);
        sw.Stop();
        return (sw.ElapsedMilliseconds, text ?? "<null>", Volatile.Read(ref connects) != c0);
    }

    using var warm = NewClient(TimeSpan.FromMinutes(9));
    await PrewarmedPost(warm, clips[0].Pcm);   // warm-up, not recorded
    for (int round = 0; round < rounds; round++)
    {
        Console.WriteLine($"-- round {round + 1}/{rounds}");
        foreach (var clip in clips.Take(2))
        {
            using (var c = NewClient(TimeSpan.FromMinutes(9))) Record($"{clip.Name} | prewarm+POST, fresh", await PrewarmedPost(c, clip.Pcm));
            using (var c = NewClient(TimeSpan.FromMinutes(9))) Record($"{clip.Name} | streamed, fresh", await Streamed2(c, clip.Pcm));
            Record($"{clip.Name} | prewarm+POST, warm", await PrewarmedPost(warm, clip.Pcm));
            Record($"{clip.Name} | streamed, warm", await Streamed2(warm, clip.Pcm));
        }
    }
    Report("shipping stream vs prewarm+POST (ms from release to text, scribe_v2_medical)");

    // Is a cancelled stream billed? The account's usage counter, before and
    // after: a normal request as the positive control, then cancelled streams.
    async Task<long?> Usage()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/user/subscription");
        req.Headers.Add("xi-api-key", med.ApiKey);
        using var resp = await warm.SendAsync(req);
        if (!resp.IsSuccessStatusCode) { Console.WriteLine($"   usage endpoint: HTTP {(int)resp.StatusCode}"); return null; }
        using var d = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return d.RootElement.TryGetProperty("character_count", out var cc) ? cc.GetInt64() : null;
    }
    Console.WriteLine("\n== is a cancelled stream billed? ==");
    var u0 = await Usage();
    await new HttpTranscriber(med, warm, _ => { }).TranscribeAsync(clips[0].Wav, shared, CancellationToken.None);
    await Task.Delay(4000);
    var u1 = await Usage();
    for (int i = 0; i < 3; i++)
    {
        var s = new HttpTranscriber(med, warm, _ => { }).BeginStreamedTranscription(shared);
        s.Append(clips[0].Pcm, 0, Math.Min(clips[0].Pcm.Length, 2 * 32000));
        await Task.Delay(1500);
        s.Dispose();                          // a discarded take
    }
    await Task.Delay(6000);
    var u2 = await Usage();
    Console.WriteLine($"   character_count: {u0} -> {u1} after one normal request ({(u1 - u0)?.ToString() ?? "?"}), -> {u2} after three cancelled streams ({(u2 - u1)?.ToString() ?? "?"})");
}
else if (mode == "medcheck")
{
    // Does scribe_v2_medical return what the rest of the pipeline needs
    // (word timing for TranscriptCoverage, audio_duration_secs)?
    using var http = NewClient();
    foreach (var model in new[] { "scribe_v2", "scribe_v2_medical" })
    {
        var logs = new List<string>();
        var t = new HttpTranscriber(Provider(true, model), http, s => logs.Add(s));
        var text = await t.TranscribeAsync(clips[1].Wav, shared, CancellationToken.None);
        Console.WriteLine($"{model}: lastWordEnd={t.LastWordEndSeconds} decoded={t.DecodedAudioSeconds} text={text}");
        foreach (var l in logs.Where(l => l.StartsWith("[scribe]") || l.StartsWith("[keyterms]"))) Console.WriteLine("   " + l);
    }
}
else if (mode == "idle")
{
    var clip = clips[0];
    using var dflt = NewClient();                               // what the app uses today
    using var longIdle = NewClient(TimeSpan.FromMinutes(10));   // candidate setting
    await Shipped(dflt, pKt, shared, clip.Wav);
    await Shipped(longIdle, pKt, shared, clip.Wav);
    int gapS = rounds; // reuse the numeric arg as the gap in seconds
    int iters = args.Length > 2 && int.TryParse(args[2], out var it) ? it : 3;
    for (int i = 0; i < iters; i++)
    {
        Console.WriteLine($"-- idle {gapS}s ({i + 1}/{iters})…");
        await Task.Delay(TimeSpan.FromSeconds(gapS));
        Record($"after {gapS}s idle, default idle timeout", await Shipped(dflt, pKt, shared, clip.Wav));
        Record($"after {gapS}s idle, 10 min idle timeout", await Shipped(longIdle, pKt, shared, clip.Wav));
    }
    Report($"idle survival ({gapS}s gaps)");
}
return 0;
