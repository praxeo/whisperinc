// Wire-format probe for Reson8Transcriber.
//
// Stands up a local HttpListener that records exactly what the transcriber
// sends (method, path, query, Authorization header, Content-Type, body size)
// and replies with a canned response. The transcriber is pointed at it via
// ApiProvider.TranscriptionEndpoint, so this exercises the SHIPPING code path
// rather than a reimplementation of it.
//
// No API key and no network access required.

using System.Net;
using System.Text;
using WhisperInk;

const string Prefix = "http://127.0.0.1:8899/";

// HttpListenerRequest is disposed once the response closes, so snapshot the
// fields under test rather than holding the live object.
var captured = new List<Req>();
string nextBody = "{\"text\":\"the patient presented with hematochezia\"}";
int nextStatus = 200;

var listener = new HttpListener();
listener.Prefixes.Add(Prefix);
listener.Start();

var pump = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); }
        catch { return; }

        long bodyLen;
        using (var ms = new MemoryStream())
        {
            ctx.Request.InputStream.CopyTo(ms);
            bodyLen = ms.Length;
        }
        captured.Add(new Req(
            ctx.Request.HttpMethod,
            ctx.Request.Url!.AbsolutePath,
            ctx.Request.Url.Query,
            ctx.Request.Headers["Authorization"],
            ctx.Request.ContentType,
            bodyLen));

        byte[] buf = Encoding.UTF8.GetBytes(nextBody);
        ctx.Response.StatusCode = nextStatus;
        ctx.Response.ContentType = nextStatus == 200 ? "application/json" : "application/problem+json";
        ctx.Response.ContentLength64 = buf.Length;
        ctx.Response.OutputStream.Write(buf, 0, buf.Length);
        ctx.Response.Close();
    }
});

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var logLines = new List<string>();
void Log(string s) => logLines.Add(s);

// A minimal but real WAV: 44-byte RIFF header + a little silence.
byte[] wav = MakeWav(320);

int failures = 0;

ApiProvider MakeProvider(string language, Dictionary<string, string>? extra = null) => new()
{
    Id = "reson8",
    Name = "Reson8",
    ApiKey = "test-key-not-a-real-secret",
    BaseUrl = Reson8Transcriber.DefaultBaseUrl,
    TranscriptionEndpoint = Prefix.TrimEnd('/') + "/v1/speech-to-text/prerecorded",
    Language = language,
    TranscriberKind = TranscriberKind.Reson8,
    BiasMechanism = "reson8_phrases",
    Reson8ExtraParams = extra ?? new Dictionary<string, string>(),
};

async Task<(string query, string? result)> Run(
    string language,
    IReadOnlyList<string> bias,
    Dictionary<string, string>? extra = null)
{
    captured.Clear();
    logLines.Clear();
    var t = new Reson8Transcriber(MakeProvider(language, extra), http, Log);
    string? res = await t.TranscribeAsync(wav, bias);
    var req = captured.Single();
    return (req.Query, res);
}

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
    if (!ok) { Console.WriteLine($"         -> {detail}"); failures++; }
}

Console.WriteLine("=== 1. Headers + body + baseline query ===");
{
    captured.Clear(); logLines.Clear();
    var t = new Reson8Transcriber(MakeProvider("en"), http, Log);
    string? res = await t.TranscribeAsync(wav, new List<string>());
    var req = captured.Single();

    Check("method is POST", req.Method == "POST", req.Method);
    Check("path is /v1/speech-to-text/prerecorded",
        req.Path == "/v1/speech-to-text/prerecorded", req.Path);
    Check("Authorization is 'ApiKey <key>' (not Bearer/Token)",
        req.Auth == "ApiKey test-key-not-a-real-secret", req.Auth ?? "<none>");
    Check("Content-Type is application/octet-stream",
        req.ContentType == "application/octet-stream", req.ContentType ?? "<none>");
    Check("body is the raw WAV (not multipart)",
        req.BodyLength == wav.Length, $"{req.BodyLength} vs {wav.Length}");
    Check("encoding=auto sent", req.Query.Contains("encoding=auto"), req.Query);
    Check("language=en sent", req.Query.Contains("language=en"), req.Query);
    Check("no phrases param when bias list empty",
        !req.Query.Contains("phrases"), req.Query);
    Check("transcript parsed from top-level text",
        res == "the patient presented with hematochezia", res ?? "<null>");
}

Console.WriteLine();
Console.WriteLine("=== 2. language handling (the 400-every-press trap) ===");
{
    var (q, _) = await Run("auto", new List<string>());
    Check("'auto' OMITS language (auto-detect has no sentinel value)",
        !q.Contains("language"), q);

    (q, _) = await Run("", new List<string>());
    Check("blank OMITS language", !q.Contains("language"), q);

    (q, _) = await Run("ja", new List<string>());
    Check("unsupported 'ja' dropped, not forwarded (would 400)",
        !q.Contains("language"), q);
    Check("unsupported code is logged loudly",
        logLines.Any(l => l.Contains("not supported") && l.Contains("auto-detection")),
        string.Join(" | ", logLines));

    (q, _) = await Run("nl,ja", new List<string>());
    Check("mixed list keeps supported code only (nl)",
        q.Contains("language=nl") && !q.Contains("ja"), q);

    (q, _) = await Run("nl,en", new List<string>());
    Check("all-supported comma list passes through",
        q.Contains("language=nl%2Cen") || q.Contains("language=nl,en"), q);

    (q, _) = await Run("EN", new List<string>());
    Check("code match is case-insensitive", q.Contains("language=EN"), q);
}

Console.WriteLine();
Console.WriteLine("=== 3. phrases biasing ===");
{
    var (q, _) = await Run("en", new List<string> { "hematochezia", "ureterolithiasis" });
    Check("bias terms -> comma-joined phrases",
        q.Contains("phrases=hematochezia%2Cureterolithiasis"), q);
    Check("phrase count logged",
        logLines.Any(l => l.Contains("phrases: 2 term(s)")), string.Join(" | ", logLines));

    (q, _) = await Run("en", new List<string> { "Smith, John", "biliary colic" });
    Check("embedded comma sanitized to space (delimiter collision)",
        q.Contains("phrases=Smith%20John%2Cbiliary%20colic"), q);
    Check("sanitization logged",
        logLines.Any(l => l.Contains("commas replaced with spaces")), string.Join(" | ", logLines));

    (q, _) = await Run("en", new List<string> { "  ", "", "valid" });
    Check("blank terms skipped", q.Contains("phrases=valid"), q);

    var many = Enumerable.Range(0, 400).Select(i => $"t{i:D3}").ToList();
    (q, _) = await Run("en", many);
    int count = Uri.UnescapeDataString(q.Split("phrases=")[1].Split('&')[0]).Split(',').Length;
    Check("clamped to the 250-phrase API ceiling", count == 250, $"got {count}");
    Check("truncation logged",
        logLines.Any(l => l.Contains("truncated to 250")), string.Join(" | ", logLines));

    var longTerms = Enumerable.Range(0, 250).Select(i => new string('x', 60) + i).ToList();
    (q, _) = await Run("en", longTerms);
    string phrasesVal = Uri.UnescapeDataString(q.Split("phrases=")[1].Split('&')[0]);
    Check("char budget keeps the URL sane (<=4000 chars raw)",
        phrasesVal.Length <= 4000, $"{phrasesVal.Length} chars");
    Check("char-budget truncation logged",
        logLines.Any(l => l.Contains("char query budget")), string.Join(" | ", logLines));
}

Console.WriteLine();
Console.WriteLine("=== 4. Reson8ExtraParams passthrough + reserved keys ===");
{
    var (q, _) = await Run("en", new List<string>(), new Dictionary<string, string>
    {
        ["custom_model_id"] = "cm_abc123",
        ["filler_mode"] = "clean",
    });
    Check("custom_model_id passed through", q.Contains("custom_model_id=cm_abc123"), q);
    Check("filler_mode passed through", q.Contains("filler_mode=clean"), q);

    (q, _) = await Run("en", new List<string> { "termA" }, new Dictionary<string, string>
    {
        ["encoding"] = "pcm_s16le",
        ["language"] = "zz",
        ["phrases"] = "hijacked",
        ["sample_rate"] = "8000",
    });
    Check("reserved 'encoding' cannot be overridden (would misparse RIFF header)",
        q.Contains("encoding=auto") && !q.Contains("pcm_s16le"), q);
    Check("reserved 'language' cannot be overridden", !q.Contains("zz"), q);
    Check("reserved 'phrases' cannot be overridden",
        q.Contains("phrases=termA") && !q.Contains("hijacked"), q);
    Check("reserved 'sample_rate' skipped", !q.Contains("sample_rate"), q);
    Check("each reserved override is logged",
        logLines.Count(l => l.Contains("reserved (set by the transcriber)")) == 4,
        string.Join(" | ", logLines));
}

Console.WriteLine();
Console.WriteLine("=== 5. response parsing ===");
{
    nextBody = "{\"text\":\"\"}";
    var (_, r) = await Run("en", new List<string>());
    Check("empty text -> null (quiet Dismissed, not the error buzz)", r == null, r ?? "<null>");

    nextBody = "{\"text\":\"  spaced  \"}";
    (_, r) = await Run("en", new List<string>());
    Check("transcript trimmed", r == "spaced", r ?? "<null>");

    nextBody = "{\"segments\":[{\"text\":\"where does it hurt\",\"speaker_id\":0},{\"text\":\"my chest\",\"speaker_id\":1}]}";
    (_, r) = await Run("en", new List<string>());
    Check("segments fallback when top-level text absent",
        r == "where does it hurt my chest", r ?? "<null>");

    nextBody = "{\"unexpected\":true}";
    (_, r) = await Run("en", new List<string>());
    Check("unknown shape -> null + logged", r == null &&
        logLines.Any(l => l.Contains("no transcript")), r ?? "<null>");
    nextBody = "{\"text\":\"ok\"}";
}

Console.WriteLine();
Console.WriteLine("=== 6. RFC 7807 error surfacing ===");
{
    nextStatus = 400;
    nextBody = "{\"type\":\"about:blank\",\"title\":\"Bad Request\",\"status\":400,\"code\":\"invalid_query_parameter\",\"detail\":\"unsupported language 'zz'\"}";
    var (_, r) = await Run("en", new List<string>());
    Check("400 -> null", r == null, r ?? "<null>");
    Check("code + detail both surfaced",
        logLines.Any(l => l.Contains("[invalid_query_parameter]") && l.Contains("unsupported language")),
        string.Join(" | ", logLines));

    nextStatus = 401;
    nextBody = "{\"title\":\"Unauthorized\",\"code\":\"unauthorized\"}";
    await Run("en", new List<string>());
    Check("401 hints at the API key",
        logLines.Any(l => l.Contains("check the Reson8 API key")), string.Join(" | ", logLines));

    nextStatus = 402;
    nextBody = "{\"code\":\"session_rejected\",\"detail\":\"credit limit exceeded\"}";
    await Run("en", new List<string>());
    Check("402 hints at credits (not mislabeled as auth)",
        logLines.Any(l => l.Contains("credit limit exceeded") && l.Contains("console.reson8.dev")),
        string.Join(" | ", logLines));

    nextStatus = 429;
    nextBody = "{\"code\":\"session_rejected\"}";
    await Run("en", new List<string>());
    Check("429 hints at concurrency",
        logLines.Any(l => l.Contains("concurrent-connection limit")), string.Join(" | ", logLines));

    nextStatus = 413;
    nextBody = "not json at all";
    await Run("en", new List<string>());
    Check("413 with non-JSON body degrades gracefully",
        logLines.Any(l => l.Contains("HTTP 413") && l.Contains("audio too large")),
        string.Join(" | ", logLines));

    nextStatus = 200;
    nextBody = "{\"text\":\"ok\"}";
}

Console.WriteLine();
Console.WriteLine("=== 7. IsReady ===");
{
    var noKey = MakeProvider("en");
    noKey.ApiKey = "";
    var t = new Reson8Transcriber(noKey, http, Log);
    bool ready = t.IsReady(out var diag);
    Check("missing key reported, not silently attempted",
        !ready && diag == "Reson8 API key not set", diag ?? "<null>");
}

Console.WriteLine();
Console.WriteLine("=== 8. CreateDefaults preset + factory dispatch ===");
{
    var defaults = ApiProvider.CreateDefaults();
    var p = defaults.SingleOrDefault(x => x.Id == "reson8");

    Check("exactly one 'reson8' preset ships", p != null, "not found / duplicated");
    Check("no duplicate provider ids across all defaults",
        defaults.Select(x => x.Id).Distinct().Count() == defaults.Count,
        string.Join(",", defaults.GroupBy(x => x.Id).Where(g => g.Count() > 1).Select(g => g.Key)));

    if (p != null)
    {
        Check("preset URL composed from the transcriber's own consts",
            p.TranscriptionEndpoint == "https://api.reson8.dev/v1/speech-to-text/prerecorded",
            p.TranscriptionEndpoint);
        Check("TranscriberKind routes to the Reson8 branch",
            p.TranscriberKind == TranscriberKind.Reson8, p.TranscriberKind.ToString());
        Check("BiasMechanism advertises real phrases biasing",
            p.ResolvedBiasMechanism == "reson8_phrases", p.ResolvedBiasMechanism);
        Check("Language pinned to en (auto-detect is weak on short clips)",
            p.Language == "en", p.Language);
        Check("TranscriptionModel unused (no model param on this API)",
            string.IsNullOrEmpty(p.TranscriptionModel), $"'{p.TranscriptionModel}'");
        Check("Reson8ExtraParams starts empty (filler_mode stays opt-in)",
            p.Reson8ExtraParams.Count == 0, $"{p.Reson8ExtraParams.Count} entries");
        Check("RequiresApiKey true (cloud provider -> health banner works)",
            p.RequiresApiKey, "false");
    }

    Check("legacy-id fallback maps 'reson8'",
        ApiProvider.InferKindFromLegacyId("reson8") == TranscriberKind.Reson8,
        ApiProvider.InferKindFromLegacyId("reson8").ToString());
}

listener.Stop();
Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

static byte[] MakeWav(int pcmBytes)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    int dataLen = pcmBytes;
    w.Write("RIFF"u8.ToArray());
    w.Write(36 + dataLen);
    w.Write("WAVE"u8.ToArray());
    w.Write("fmt "u8.ToArray());
    w.Write(16);
    w.Write((short)1);      // PCM
    w.Write((short)1);      // mono
    w.Write(16000);         // sample rate
    w.Write(32000);         // byte rate
    w.Write((short)2);      // block align
    w.Write((short)16);     // bits
    w.Write("data"u8.ToArray());
    w.Write(dataLen);
    w.Write(new byte[dataLen]);
    w.Flush();
    return ms.ToArray();
}

record Req(string Method, string Path, string Query, string? Auth, string? ContentType, long BodyLength);
