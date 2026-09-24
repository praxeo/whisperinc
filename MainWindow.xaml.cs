using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;

namespace WhisperInk
{
    public partial class MainWindow : Window
    {
        // ── Win32 imports ──────────────────────────────────────────────
        // The keyboard hook lives in KeyboardHookService and all synthetic
        // input in TextInjector; the window itself only steers focus.
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        // Active-provider state is computed, never cached: a provider
        // switch mid-flight can therefore never leave stale values behind.
        private string ActiveApiKey => GetActiveProvider()?.ApiKey ?? "";

        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "config.json");

        // ── State ──────────────────────────────────────────────────────
        private KeyboardHookService? _hook;
        private readonly TextInjector _injector = new(Log);

        // Recording lifecycle: 0 = Idle, 1 = Recording, 2 = Stopping. The
        // hook thread reads it while the UI thread transitions it, so all
        // transitions go through Interlocked.CompareExchange — a double
        // Ctrl+Space can never double-start, a double release never
        // double-stops, and a thrown stop can't wedge the state machine.
        private int _recState;
        private bool IsRecording => Volatile.Read(ref _recState) == 1;
        private bool IsStopping  => Volatile.Read(ref _recState) == 2;

        private bool _isSoundEnabled = true;
        private int _selectedDeviceNumber;

        private List<string> _contextBiasTerms = new();

        private List<ApiProvider> _providers = new();
        private string _activeProviderId = "mistral";

        // ── Tray / health / UX state (Deliverables 3–6, 8) ──────────
        private TrayIconManager? _tray;
        private HealthProbe? _healthProbe;
        private HealthReport _lastHealth = new();
        private bool _quitOnClose = false;
        private bool _launchAtStartup = false;
        private bool _hasSeenFirstRun = false;
        private bool _exiting = false;
        private string _crispGpuBackend = "auto";

        private IntPtr _targetWindow = IntPtr.Zero;

        // Shared by every cloud transcriber. What bounds a request is the
        // per-take deadline token TranscribeTakeAsync passes in — scaled to
        // the recording (TranscriptionDeadline) — so this timeout is only a
        // backstop, longer than any deadline. It used to be a flat 15 s, which
        // was a length limit in disguise: any take whose upload + inference
        // ran past 15 s failed, however healthy the service.
        private readonly HttpClient _httpClient = CreateCloudHttpClient();

        /// <summary>The connection handling behind every cloud dictation.
        ///
        /// Idle connections are kept for 9 minutes instead of .NET's 1-minute
        /// default. Measured 2026-09-23 against ElevenLabs: a fresh connection
        /// (DNS + TCP + TLS + a cold congestion window) added a median 166 ms
        /// to a 4 s take, and with the default most real dictations paid it:
        /// anything more than a minute after the previous one (9 of 14 in
        /// that evening's log). ElevenLabs' side (a Google front end) still
        /// accepted a connection that had been idle for 5 and 9 minutes, so the
        /// client retires one before the server does. PrewarmConnection covers
        /// longer gaps.
        ///
        /// TCP keep-alive makes the long idle safe. A NAT or firewall that
        /// silently forgets an idle connection would otherwise leave the next
        /// request writing into nothing until the take's deadline; the probes
        /// keep the mapping alive and let a dead connection be noticed and
        /// dropped instead of reused. Each new connection is logged, which
        /// shows in debug.log whether a take reused one.</summary>
        private static HttpClient CreateCloudHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(9),
                ConnectCallback = async (context, ct) =>
                {
                    // Same as the default connect (dual-mode socket, Nagle
                    // off), plus keep-alive.
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        EnableTcpKeepAlive(socket);
                        var sw = Stopwatch.StartNew();
                        await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                        Log($"[net] new connection to {context.DnsEndPoint.Host}:{context.DnsEndPoint.Port} ({sw.ElapsedMilliseconds} ms to connect)");
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            return new HttpClient(handler) { Timeout = TranscriptionDeadline.HttpBackstop };
        }

        /// <summary>First probe after 45 s idle, then every 5 s; three misses
        /// and the OS fails the socket. Best effort: keep-alive is an
        /// optimization, so an OS that rejects an option still connects.</summary>
        private static void EnableTcpKeepAlive(Socket socket)
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 45);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
            }
            catch (Exception ex) { Log($"[net] TCP keep-alive not fully applied: {ex.GetType().Name}: {ex.Message}"); }
        }

        /// <summary>Opens, or re-checks, the pooled connection to the active
        /// cloud provider while the user is still talking, so the release
        /// doesn't wait on DNS + TCP + TLS. Measured 2026-09-23 against
        /// ElevenLabs: this recovered ~90 of the 166 ms a fresh connection
        /// costs. The rest is TCP slow start on the upload, which only a
        /// streamed upload removes. A HEAD to the host's root is enough to open
        /// the connection; the status doesn't matter. Fire-and-forget: if it
        /// fails, the dictation opens its own connection exactly as before.
        /// Skips local servers and Google Chirp 3, which keeps its own
        /// HttpClient.</summary>
        private void PrewarmConnection(ApiProvider? provider)
        {
            if (provider == null || provider.IsLocalProvider || provider.IsLocalHttp
                || provider.TranscriberKind == TranscriberKind.GoogleChirp3)
                return;
            if (!Uri.TryCreate(provider.ResolvedTranscriptionUrl, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps)
                return;

            var origin = new Uri(url.GetLeftPart(UriPartial.Authority) + "/");
            var client = _httpClient;
            _ = Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var request = new HttpRequestMessage(HttpMethod.Head, origin);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    Log($"[net] pre-warmed {origin.Host} ({(int)response.StatusCode}) in {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    Log($"[net] pre-warm of {origin.Host} failed after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name}); the dictation connects on its own");
                }
            });
        }

        // One transcriber instance per provider, lazily constructed by the
        // factory on first use. Replaces the old fan-out of 12 per-provider
        // fields plus their disposal boilerplate.
        private TranscriberFactory? _transcribers;

        // ── Audio capture ────────────────────────────────────────────
        // Owns the microphone: held open between dictations with a pre-roll
        // ring buffer, so pressing the hotkey starts a recording that already
        // contains the last few hundred ms. See MicCapture for why.
        private MicCapture? _mic;

        // Every take's audio, journaled before it is sent and deleted once its
        // text is delivered — failures keep theirs for a retry. See UnsentTakes.
        private UnsentTakes? _unsent;

        // A capture this much shorter than the hold means the mic stopped
        // delivering audio mid-take. Normally the capture is LONGER than the
        // hold (it starts with the pre-roll); a cold open trails it by
        // ~130-160 ms, so a full second is far outside either.
        private const int MicStallToleranceMs = 1000;

        // Persistent output device for the UI chirps — the old per-chirp
        // SoundPlayer cost 190-222 ms. See UiSoundPlayer.
        private UiSoundPlayer? _sounds;

        // Environment.TickCount64 at the moment the hotkey went down.
        // Press-to-release duration is the intent signal used to throw away
        // accidental taps without spending a transcription on them.
        private long _pressTicks;

        // Releases the warm mic after a spell of no dictation so the Windows
        // microphone-in-use indicator isn't lit forever.
        private DispatcherTimer? _micIdleTimer;

        // One-shot restore for transient status text, so showing a message
        // never blocks the recording state machine. See FlashStatus.
        private DispatcherTimer? _statusFlashTimer;

        private DispatcherTimer _animationTimer = null!;
        private readonly Random _rng = new();

        // ── Responsiveness tuning (all config.json-overridable) ───────
        // Keep the mic open between dictations. Off = the pre-v2 behaviour:
        // every press pays a ~130 ms device open and clips the onset.
        private bool _warmMicEnabled = true;
        // Close the warm mic after this long idle. 0 = hold it while running.
        private int _warmMicIdleSeconds = 180;
        // How much pre-press audio to prepend. 400 ms comfortably covers the
        // ~130 ms open plus dispatcher and hook latency.
        private int _preRollMs = 400;
        // Wait this long at release for the in-flight buffer, so fixing the
        // clipped start doesn't introduce a clipped end.
        private int _postRollMs = 80;
        // Presses shorter than this are treated as accidental: discarded with
        // no API call, no error tone and no lockout.
        private int _minHoldMs = 250;
        // Whole-take RMS (0..1) below which a take MAY be silent. It's only
        // dropped if SpeechDetector also finds no sustained speech in it, so
        // quiet speech gets through at any setting (on 2026-09-23 the owner's
        // quiet takes averaged 0.0017-0.0026, under this line, and were lost).
        // What the threshold still does is keep a steady-noise room
        // (0.0006-0.0012 measured) from being sent. 0 disables the gate.
        private double _silenceThreshold = DefaultSilenceThreshold;
        // Also the floor that separates "silent take" from "real sound that
        // came back as no text" when the gate itself is disabled (0).
        private const double DefaultSilenceThreshold = 0.003;
        // A hold this long that turns out silent gets the Warn tone instead of
        // the quiet blip: nobody holds the key for 1.5 s by accident, so it may
        // be speech the mic barely caught. Either way the take is kept.
        private const long LongSilentHoldMs = 1500;
        // How long after a paste the user's own clipboard is put back
        // (TextInjector.RestoreDelayMs).
        private int _clipboardRestoreMs = 1000;
        // Upload an ElevenLabs take while it is being spoken, so the release
        // waits only for the transcript (StreamedTranscription). Off = every
        // take is uploaded as a WAV after release, as before 2026-09.
        private bool _streamUpload = true;

        // The take currently being streamed, if any. UI thread only: set when
        // a take starts, taken over (finished or cancelled) when it stops.
        private StreamedTranscription? _streamedTake;

        private static readonly string LogFile = Path.Combine(ConfigFolder, "debug.log");
        // The previous session's log, kept across one restart (see MainWindow_Loaded).
        private static readonly string PreviousLogFile = Path.Combine(ConfigFolder, "debug.previous.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
        }

        /// <summary>Fire-and-forget for the async command methods: faults
        /// land in debug.log instead of vanishing (async void) or tearing
        /// down the process.</summary>
        private static void RunSafe(Func<Task> op, string name)
        {
            _ = op().ContinueWith(
                // Type + stack, not just the message: "Object reference not set
                // to an instance of an object" on its own names no culprit.
                t => Log($"[unhandled] {name}: {t.Exception?.GetBaseException()}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Read audio duration in milliseconds from a WAV file path.
        /// Used to compute real-time-factor (RTFx) for logging.
        /// </summary>
        private static double GetWavDurationMs(string path)
        {
            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(path);
                return reader.TotalTime.TotalMilliseconds;
            }
            catch { return 0; }
        }

        /// <summary>Read audio duration in ms from WAV bytes in memory.</summary>
        private static double GetWavDurationMs(byte[] wavBytes)
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var reader = new NAudio.Wave.WaveFileReader(ms);
                return reader.TotalTime.TotalMilliseconds;
            }
            catch { return 0; }
        }

        private static int TryParsePortFromUrl(string? url, int fallback)
        {
            if (string.IsNullOrWhiteSpace(url)) return fallback;
            if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Port > 0)
                return u.Port;
            return fallback;
        }

        public MainWindow()
        {
            InitializeComponent();
            Loaded  += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            // Hide to tray by default; only actually close when the user
            // chose "Quit on close" or invoked the tray's Quit item.
            if (!_exiting && !_quitOnClose)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            try { _micIdleTimer?.Stop(); } catch { }
            try { _mic?.Dispose(); } catch { }
            try { _sounds?.Dispose(); } catch { }
            try { _hook?.Dispose(); } catch { }
            try { _transcribers?.Dispose(); } catch { }
            try { _healthProbe?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Fresh log per session — must happen BEFORE LoadConfig, or the
            // truncation wipes the very startup diagnostics it should keep
            // (provider appends, active-provider line). The previous session's
            // log is kept as debug.previous.log first: the usual reaction to a
            // failure or a crash is to restart the app, and the plain
            // truncation used to destroy the one record of what went wrong.
            try
            {
                if (File.Exists(LogFile)) File.Move(LogFile, PreviousLogFile, overwrite: true);
            }
            catch
            {
                try { File.Copy(LogFile, PreviousLogFile, overwrite: true); } catch { }
            }
            try { File.WriteAllText(LogFile, $"=== WhisperInk started {DateTime.Now:yyyy-MM-dd HH:mm:ss} === (previous session: {Path.GetFileName(PreviousLogFile)})\n"); } catch { }

            Topmost = true;
            var screen = SystemParameters.WorkArea;
            Left = screen.Width - Width - 10;
            Top = screen.Height - Height - 10;

            // All hotkey decisions live in the service; these callbacks fire
            // on the hook thread and immediately marshal to the UI thread.
            _hook = new KeyboardHookService(
                isRecording: () => IsRecording,
                onDictationStart: target =>
                    Dispatcher.BeginInvoke(() => StartBatchDictation(target)),
                onDictationStop: () =>
                    Dispatcher.BeginInvoke(() => RunSafe(StopBatchDictationAsync, "StopBatchDictation")),
                log: Log);
            _hook.Install();

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _animationTimer.Tick += (_, _) => UpdateHistogram();

            LoadConfig();

            // Audio devices are created after LoadConfig so they see the
            // configured mic index and pre-roll length.
            _sounds = new UiSoundPlayer(Log);
            _mic = new MicCapture(() => _selectedDeviceNumber, () => _preRollMs, Log,
                onCaptureLost: () => Dispatcher.BeginInvoke(OnMicLostMidDictation));
            WarmMic();

            _unsent = new UnsentTakes(Path.Combine(ConfigFolder, "unsent"), Log);

            // Factory owns one ITranscriber per provider. The GPU-backend
            // delegate lets it pick up live edits from the settings dialog
            // without needing to drop and recreate every CrispASR server.
            _transcribers = new TranscriberFactory(_httpClient, () => _crispGpuBackend, Log);

            UpdateStatusLabel();

            InitializeTrayAndHealth();
            SyncLaunchAtStartupFromRegistry();
            RunFirstRunCheck();
            AnnounceUnsentTakes();
        }

        /// <summary>Startup half of "never lose a dictation": takes the last
        /// session failed — or never finished, because the app closed or
        /// crashed mid-take — are waiting in the unsent folder. Say so once,
        /// where it will be seen, instead of leaving them to be found.</summary>
        private void AnnounceUnsentTakes()
        {
            int waiting;
            try { waiting = _unsent?.Recover() ?? 0; }
            catch (Exception ex) { Log($"[unsent] recovery failed: {ex.GetType().Name}: {ex.Message}"); return; }
            if (waiting == 0) return;
            Log($"[unsent] {waiting} unsent dictation(s) waiting in {_unsent!.Folder}");
            _tray?.ShowBalloon(
                $"{waiting} unsent dictation{(waiting == 1 ? "" : "s")}",
                "Their audio is saved. Right-click the tray icon → ↻ Unsent dictations to retry.",
                warning: true);
        }

        // ── Provider helpers ────────────────────────────────────────────

        private ApiProvider? GetActiveProvider() =>
            _providers.FirstOrDefault(p => p.Id == _activeProviderId) ?? _providers.FirstOrDefault();

        private void ApplyActiveProvider()
        {
            var provider = GetActiveProvider();
            if (provider == null)
            {
                _providers = ApiProvider.CreateDefaults();
                _activeProviderId = "mistral";
                provider = _providers[0];
            }

            Log($"Active provider: {provider.Name} → STT={provider.ResolvedTranscriptionUrl}  (auth={(provider.UsesCustomAuthHeader ? provider.ResolvedAuthHeaderName : "Bearer")}, modelField={provider.ResolvedModelField})");
        }

        private void SwitchProvider(string providerId)
        {
            ApplyProviderSwitch(providerId);
            SaveConfig();

            UpdateStatusLabel();
            UpdateLocalModelBanner();
            _healthProbe?.RequestProbe();
        }

        /// <summary>
        /// Core of every active-provider transition — from tray menu, context
        /// menu, or dialog close. Logs the change, flips the id, disposes any
        /// local CrispASR server owned by the previous provider (so we don't
        /// leak ~2 GB of resident model per switch). Per-provider state
        /// (ActiveApiKey, …) is computed, so nothing to re-derive.
        /// Does not persist by itself — the caller owns SaveConfig().
        /// </summary>
        private void ApplyProviderSwitch(string newId)
        {
            string oldId = _activeProviderId;
            if (string.Equals(oldId, newId, StringComparison.Ordinal))
            {
                // Same provider — still refresh derived state because cloud-side
                // settings (URL, key) might have been edited in the dialog.
                ApplyActiveProvider();
                return;
            }

            _activeProviderId = newId;

            // The outgoing provider's local server is no longer needed — drop it
            // so we don't keep ~2 GB of resident model for a provider we're not
            // using. The incoming provider spawns its own server on first dictation.
            _transcribers?.Drop(oldId);

            ApplyActiveProvider();
            Log($"Active provider changed: {oldId} -> {newId}");
        }

        /// <summary>Applies a new CrispASR GPU backend. Drops all cached
        /// transcribers so the next dictation respawns with the new flag.</summary>
        private void ApplyGpuBackendChange(string? raw)
        {
            string normalized = NormalizeGpuBackend(raw);
            if (!string.Equals(_crispGpuBackend, normalized, StringComparison.OrdinalIgnoreCase))
            {
                string old = _crispGpuBackend;
                _crispGpuBackend = normalized;
                _transcribers?.DropAll();
                Log($"Crisp GPU backend changed: {old} -> {normalized}");
            }
            else
            {
                _crispGpuBackend = normalized;
            }
        }

        /// <summary>If the active provider is a local CrispASR one and its
        /// GGUF is missing, surface a non-modal banner telling the user where
        /// to drop the file. Otherwise let the normal health-probe banner
        /// drive the UI.</summary>
        private void UpdateLocalModelBanner()
        {
            try
            {
                var prov = GetActiveProvider();
                if (prov == null
                    || prov.TranscriberKind != TranscriberKind.LocalCrispAsrServer
                    || string.IsNullOrWhiteSpace(prov.LocalModelGlob))
                {
                    UpdateSetupBannerVisibility();
                    return;
                }

                string sub = string.IsNullOrWhiteSpace(prov.LocalModelFolder) ? "cohere-gguf" : prov.LocalModelFolder;
                string modelFolder = Path.Combine(ConfigFolder, sub);
                string? missing = MissingIfAbsent(modelFolder, prov.LocalModelGlob);

                if (missing != null)
                {
                    lblBanner.Text = $"Model file missing: {missing} — drop it into {modelFolder}";
                    SetupBanner.Visibility = Visibility.Visible;
                    Height = 68;
                }
                else
                {
                    UpdateSetupBannerVisibility();
                }
            }
            catch (Exception ex) { Log($"Local banner update failed: {ex.Message}"); }
        }

        private static string? MissingIfAbsent(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return pattern;
                // Literal name first (no glob) then the glob pattern.
                if (!pattern.Contains('*'))
                {
                    string full = Path.Combine(folder, pattern);
                    return File.Exists(full) ? null : pattern;
                }
                foreach (var _ in Directory.EnumerateFiles(folder, pattern))
                    return null;
                return pattern;
            }
            catch { return pattern; }
        }

        private void UpdateStatusLabel()
        {
            var provider = GetActiveProvider();
            lblStatus.Content = provider?.Name ?? "?";
            UpdateHealthDot();
        }

        private void UpdateHealthDot()
        {
            try { lblHealthDot.Text = _lastHealth.Dot; } catch { }
            try { lblHealthDot.ToolTip = _lastHealth.Summary; } catch { }
        }

        private bool IsLocalProvider => GetActiveProvider()?.IsLocalProvider == true;

        // Clamp a user-supplied GPU backend string onto the set crispasr understands.
        // Anything unrecognized collapses to "auto" so a typo in config.json can't
        // crash the server-spawn path.
        private static string NormalizeGpuBackend(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "auto";
            var v = raw.Trim().ToLowerInvariant();
            return v switch
            {
                "auto" or "cpu" or "vulkan" or "cuda" or "metal" => v,
                _ => "auto"
            };
        }

        // ── Config ──────────────────────────────────────────────────────

        private void LoadConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder)) Directory.CreateDirectory(ConfigFolder);
                if (File.Exists(ConfigFile))
                {
                    var json = File.ReadAllText(ConfigFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    // Legacy single-key config (pre provider-system) — only
                    // read so the migration below can seed the mistral provider.
                    string legacyMistralKey = "";
                    if (root.TryGetProperty("MistralApiKey", out var key)) legacyMistralKey = key.GetString() ?? "";
                    if (root.TryGetProperty("IsSoundEnabled", out var snd)) _isSoundEnabled = snd.GetBoolean();
                    if (root.TryGetProperty("SelectedDevice", out var dev)) _selectedDeviceNumber = dev.GetInt32();

                    // ── Responsiveness tuning ──
                    // Clamped, not trusted: a hand-edited 0 ms pre-roll or a
                    // 5 s min-hold would quietly break dictation.
                    if (root.TryGetProperty("WarmMicEnabled", out var wm)) _warmMicEnabled = wm.GetBoolean();
                    if (root.TryGetProperty("StreamUpload", out var su) && su.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        _streamUpload = su.GetBoolean();
                    if (root.TryGetProperty("WarmMicIdleSeconds", out var wmi)) _warmMicIdleSeconds = Math.Max(0, wmi.GetInt32());
                    if (root.TryGetProperty("PreRollMs", out var pr)) _preRollMs = Math.Clamp(pr.GetInt32(), 0, 3000);
                    if (root.TryGetProperty("PostRollMs", out var po)) _postRollMs = Math.Clamp(po.GetInt32(), 0, 1000);
                    if (root.TryGetProperty("MinHoldMs", out var mh)) _minHoldMs = Math.Clamp(mh.GetInt32(), 0, 2000);
                    if (root.TryGetProperty("SilenceThreshold", out var sil)) _silenceThreshold = Math.Clamp(sil.GetDouble(), 0.0, 0.5);
                    if (root.TryGetProperty("ClipboardRestoreMs", out var crm) && crm.ValueKind == JsonValueKind.Number && crm.TryGetInt32(out int restoreMs))
                        _clipboardRestoreMs = Math.Clamp(restoreMs, 250, 10000);
                    _injector.RestoreDelayMs = _clipboardRestoreMs;
                    if (root.TryGetProperty("ContextBiasTerms", out var cbt) && cbt.ValueKind == JsonValueKind.Array)
                    {
                        _contextBiasTerms = new List<string>();
                        foreach (var term in cbt.EnumerateArray())
                        {
                            var s = term.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) _contextBiasTerms.Add(s);
                        }
                    }
                    if (root.TryGetProperty("Providers", out var provArray) && provArray.ValueKind == JsonValueKind.Array)
                    {
                        _providers = new List<ApiProvider>();
                        foreach (var pEl in provArray.EnumerateArray())
                        {
                            var p = new ApiProvider();
                            if (pEl.TryGetProperty("Id", out var id)) p.Id = id.GetString() ?? p.Id;
                            if (pEl.TryGetProperty("Name", out var name)) p.Name = name.GetString() ?? p.Name;
                            if (pEl.TryGetProperty("BaseUrl", out var url)) p.BaseUrl = url.GetString() ?? "";
                            if (pEl.TryGetProperty("ApiKey", out var ak)) p.ApiKey = ak.GetString() ?? "";
                            if (pEl.TryGetProperty("TranscriptionModel", out var tm)) p.TranscriptionModel = tm.GetString() ?? "";
                            if (pEl.TryGetProperty("SupportsTranscription", out var st)) p.SupportsTranscription = st.GetBoolean();
                            if (pEl.TryGetProperty("TranscriptionEndpoint", out var te)) p.TranscriptionEndpoint = te.GetString() ?? "";
                            if (pEl.TryGetProperty("AuthHeaderName", out var ahn)) p.AuthHeaderName = ahn.GetString() ?? "";
                            if (pEl.TryGetProperty("ModelFieldName", out var mfn)) p.ModelFieldName = mfn.GetString() ?? "";
                            if (pEl.TryGetProperty("TranscriptionTemperature", out var tt) && tt.ValueKind != JsonValueKind.Null)
                                p.TranscriptionTemperature = tt.GetDouble();
                            if (pEl.TryGetProperty("ContextBiasMode", out var cbm))
                                p.ContextBiasMode = cbm.GetString() ?? "none";
                            if (pEl.TryGetProperty("BiasMechanism", out var bm))
                                p.BiasMechanism = bm.GetString() ?? "";
                            if (pEl.TryGetProperty("Language", out var lang))
                                p.Language = lang.GetString() ?? "en";
                            if (pEl.TryGetProperty("ScribeKeytermsRaw", out var skr))
                                p.ScribeKeytermsRaw = skr.GetString() ?? "";
                            if (pEl.TryGetProperty("TagAudioEvents", out var tae) && tae.ValueKind != JsonValueKind.Null)
                                p.TagAudioEvents = tae.GetBoolean();
                            if (pEl.TryGetProperty("NoVerbatim", out var nv) && nv.ValueKind != JsonValueKind.Null)
                                p.NoVerbatim = nv.GetBoolean();

                            // ── New schema fields (added when factory dispatch landed) ──
                            // Tolerate both encodings. Newer configs store the
                            // enum as a string ("LocalCrispAsrServer"); configs
                            // written before SaveConfig used a string converter
                            // store it as a bare number. Calling GetString() on a
                            // numeric token throws InvalidOperationException —
                            // which used to abort the ENTIRE load (the catch is
                            // outside this loop), silently resetting every
                            // provider to defaults and wiping saved API keys and
                            // the active-provider choice on each launch.
                            if (pEl.TryGetProperty("TranscriberKind", out var tk))
                            {
                                if (tk.ValueKind == JsonValueKind.String &&
                                    Enum.TryParse<TranscriberKind>(tk.GetString(), ignoreCase: true, out var parsedKind))
                                    p.TranscriberKind = parsedKind;
                                else if (tk.ValueKind == JsonValueKind.Number &&
                                         Enum.IsDefined(typeof(TranscriberKind), tk.GetInt32()))
                                    p.TranscriberKind = (TranscriberKind)tk.GetInt32();
                                else
                                    p.TranscriberKind = ApiProvider.InferKindFromLegacyId(p.Id);
                            }
                            else
                            {
                                p.TranscriberKind = ApiProvider.InferKindFromLegacyId(p.Id);
                            }

                            if (pEl.TryGetProperty("LocalServerPort", out var lsp) && lsp.ValueKind == JsonValueKind.Number)
                                p.LocalServerPort = lsp.GetInt32();
                            if (pEl.TryGetProperty("LocalModelGlob", out var lmg))
                                p.LocalModelGlob = lmg.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalBackendHint", out var lbh))
                                p.LocalBackendHint = lbh.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalGpuBackend", out var lgb))
                                p.LocalGpuBackend = lgb.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalModelFolder", out var lmf))
                                p.LocalModelFolder = lmf.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalBeamSize", out var lbs) && lbs.ValueKind == JsonValueKind.Number)
                                p.LocalBeamSize = lbs.GetInt32();
                            if (pEl.TryGetProperty("LocalPuncModel", out var lpm))
                                p.LocalPuncModel = lpm.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalTruecaseModel", out var ltm))
                                p.LocalTruecaseModel = ltm.GetString() ?? "";
                            if (pEl.TryGetProperty("LocalExtraParams", out var lep) && lep.ValueKind == JsonValueKind.Object)
                            {
                                var extra = new Dictionary<string, string>();
                                foreach (var prop in lep.EnumerateObject())
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                        extra[prop.Name] = prop.Value.GetString() ?? "";
                                p.LocalExtraParams = extra;
                            }
                            if (pEl.TryGetProperty("DeepgramExtraParams", out var dep) && dep.ValueKind == JsonValueKind.Object)
                            {
                                var extra = new Dictionary<string, string>();
                                foreach (var prop in dep.EnumerateObject())
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                        extra[prop.Name] = prop.Value.GetString() ?? "";
                                p.DeepgramExtraParams = extra;
                            }
                            if (pEl.TryGetProperty("Reson8ExtraParams", out var rep) && rep.ValueKind == JsonValueKind.Object)
                            {
                                var extra = new Dictionary<string, string>();
                                foreach (var prop in rep.EnumerateObject())
                                    if (prop.Value.ValueKind == JsonValueKind.String)
                                        extra[prop.Name] = prop.Value.GetString() ?? "";
                                p.Reson8ExtraParams = extra;
                            }
                            if (pEl.TryGetProperty("HotwordsBoost", out var hwb) && hwb.ValueKind == JsonValueKind.Number)
                                p.HotwordsBoost = hwb.GetDouble();

                            _providers.Add(p);
                        }
                    }
                    if (root.TryGetProperty("ActiveProviderId", out var apid))
                        _activeProviderId = apid.GetString() ?? "mistral";
                    if (root.TryGetProperty("QuitOnClose", out var qoc))       _quitOnClose      = qoc.GetBoolean();
                    if (root.TryGetProperty("LaunchAtStartup", out var las))   _launchAtStartup  = las.GetBoolean();
                    if (root.TryGetProperty("HasSeenFirstRun", out var hsfr))  _hasSeenFirstRun  = hsfr.GetBoolean();
                    if (root.TryGetProperty("CrispGpuBackend", out var cgb))
                    {
                        string raw = cgb.GetString() ?? "auto";
                        string norm = NormalizeGpuBackend(raw);
                        if (!string.Equals(raw.Trim(), norm, StringComparison.OrdinalIgnoreCase))
                            Log($"Unknown CrispGpuBackend '{raw}' in config → normalized to '{norm}'.");
                        _crispGpuBackend = norm;
                    }

                    if (_providers.Count == 0)
                    {
                        _providers = ApiProvider.CreateDefaults();
                        _activeProviderId = "mistral";
                        if (!string.IsNullOrWhiteSpace(legacyMistralKey))
                        {
                            var mistral = _providers.FirstOrDefault(p => p.Id == "mistral");
                            if (mistral != null) mistral.ApiKey = legacyMistralKey;
                        }
                        Log("Migrated legacy config → provider system");
                    }
                    else
                    {
                        // Append any new built-in defaults that the user's saved config doesn't
                        // have yet (matched by Id). Purely additive — never overwrites anything
                        // the user has edited, never removes anything.
                        var defaults = ApiProvider.CreateDefaults();
                        var existingIds = new HashSet<string>(_providers.Select(p => p.Id));
                        var configured = _providers.ToList();
                        foreach (var def in defaults)
                        {
                            if (!existingIds.Contains(def.Id))
                            {
                                // A new preset for a service already set up here
                                // (Scribe Medical beside Scribe) starts with its
                                // key instead of failing every take until the key
                                // is pasted in again.
                                var sibling = ApiProvider.InheritFromSibling(def, configured);
                                _providers.Add(def);
                                Log($"Added new default provider: {def.Name}"
                                    + (sibling == null ? "" : $" (API key{(def.IsElevenLabs ? ", Scribe keyterms and output switches" : "")} taken from {sibling.Name})"));
                            }
                        }
                        foreach (var p in _providers)
                        {
                            string? repaired = ApiProvider.RepairSupersededDefault(p);
                            if (repaired != null) Log($"Repaired {p.Id}: {repaired}");
                        }

                        // Backfill Local* fields for known-id providers whose
                        // config predates the factory schema. Cosmetic-only:
                        // skips anything the user has already filled in.
                        var defaultsById = defaults.ToDictionary(d => d.Id, StringComparer.Ordinal);
                        foreach (var p in _providers)
                        {
                            if (!defaultsById.TryGetValue(p.Id, out var def)) continue;
                            if (string.IsNullOrWhiteSpace(p.LocalModelGlob))   p.LocalModelGlob   = def.LocalModelGlob;
                            if (string.IsNullOrWhiteSpace(p.LocalBackendHint)) p.LocalBackendHint = def.LocalBackendHint;
                            if (string.IsNullOrWhiteSpace(p.LocalGpuBackend))  p.LocalGpuBackend  = def.LocalGpuBackend;
                            if (string.IsNullOrWhiteSpace(p.LocalModelFolder)) p.LocalModelFolder = def.LocalModelFolder;
                            if (p.LocalServerPort == null)                     p.LocalServerPort  = def.LocalServerPort;
                            if (string.IsNullOrWhiteSpace(p.LocalPuncModel))   p.LocalPuncModel   = def.LocalPuncModel;
                            if (string.IsNullOrWhiteSpace(p.LocalTruecaseModel)) p.LocalTruecaseModel = def.LocalTruecaseModel;
                            if (string.IsNullOrWhiteSpace(p.BiasMechanism))    p.BiasMechanism    = def.BiasMechanism;
                            if (p.HotwordsBoost == null)                       p.HotwordsBoost    = def.HotwordsBoost;
                        }
                    }
                }
                else
                {
                    _providers = ApiProvider.CreateDefaults();
                    _activeProviderId = "mistral";
                    SaveConfig();
                    MessageBox.Show($"Config created at:\n{ConfigFile}\n\nOpen provider settings (right-click → 🔌 API Providers) to configure your API key.", "WhisperInk");
                }
            }
            catch (Exception ex) { Log($"Config error: {ex.Message}"); }

            ApplyActiveProvider();
        }

        private void SaveConfig()
        {
            try
            {
                if (!Directory.Exists(ConfigFolder)) Directory.CreateDirectory(ConfigFolder);

                // MistralApiKey persists only for downgrade compatibility; the
                // provider entry is the source of truth.
                var mistralProvider = _providers.FirstOrDefault(p => p.Id == "mistral");
                string legacyKey = mistralProvider?.ApiKey ?? "";

                var config = new
                {
                    MistralApiKey = legacyKey,
                    IsSoundEnabled = _isSoundEnabled,
                    SelectedDevice = _selectedDeviceNumber,
                    ContextBiasTerms = _contextBiasTerms,
                    Providers = _providers,
                    ActiveProviderId = _activeProviderId,
                    QuitOnClose     = _quitOnClose,
                    LaunchAtStartup = _launchAtStartup,
                    HasSeenFirstRun = _hasSeenFirstRun,
                    CrispGpuBackend = _crispGpuBackend,
                    WarmMicEnabled     = _warmMicEnabled,
                    WarmMicIdleSeconds = _warmMicIdleSeconds,
                    PreRollMs          = _preRollMs,
                    PostRollMs         = _postRollMs,
                    MinHoldMs          = _minHoldMs,
                    SilenceThreshold   = _silenceThreshold,
                    ClipboardRestoreMs = _clipboardRestoreMs,
                    StreamUpload       = _streamUpload
                };
                // Serialize the TranscriberKind enum as a string so config.json
                // both stays human-readable AND round-trips through LoadConfig
                // (which reads the field as a string). Without this converter
                // System.Text.Json writes enums as bare numbers, which the loader
                // then choked on — wiping providers, keys, and the active id.
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, jsonOptions));
            }
            catch (Exception ex) { Log($"Config save error: {ex.Message}"); }
        }

        // Keyboard hook + hotkey logic: KeyboardHookService (wired in
        // MainWindow_Loaded). Synthetic typing/paste/release: TextInjector.

        // ════════════════════════════════════════════════════════════════
        // BATCH DICTATION MODE
        //
        // The press path does no device work: MicCapture is already streaming
        // into a pre-roll ring, so starting a dictation attaches a writer and
        // seeds it with audio from before the keypress. The release path does
        // no device teardown either. What used to be ~130 ms of clipped onset
        // and ~120 ms of teardown is now a couple of milliseconds each way.
        // ════════════════════════════════════════════════════════════════

        private void StartBatchDictation(IntPtr target)
        {
            var startProvider = GetActiveProvider();
            if (startProvider != null && startProvider.RequiresApiKey && string.IsNullOrWhiteSpace(startProvider.ApiKey))
            {
                lblStatus.Content = "No API key!";
                return;
            }

            if (Interlocked.CompareExchange(ref _recState, 1, 0) != 0)
            {
                // The previous take (or a retry) is still transcribing. Used to
                // be silent — and with deadlines that scale to long takes, the
                // wait can be long enough to start talking into a press that
                // was never recording. The Start chirp's absence was the only
                // clue; the Dismissed blip says "heard you, not now".
                if (IsStopping)
                {
                    Log("[skip] hotkey pressed while the previous take is still transcribing — not recording");
                    PlayUiSound(UiSound.Dismissed);
                }
                return;
            }
            _pressTicks = Environment.TickCount64;
            // Taken only once the take has really started. It used to be set
            // by the hook callback on every press — so a press made while the
            // previous take was still transcribing re-pointed THAT take's
            // paste at whatever window the second press happened in.
            _targetWindow = target;

            // FIRST, before any device or UI work: the chirp is the user's only
            // confirmation that the press registered, so nothing may queue ahead
            // of it. It costs ~0 ms to enqueue and plays on its own thread.
            PlayUiSound(UiSound.Start);

            _hook?.BeginSuppression();
            _injector.ReleaseAllModifierKeys();
            _micIdleTimer?.Stop();

            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            lblStatus.Content = "🎙 REC";
            lblStatus.Opacity = 1;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            // An ElevenLabs take starts uploading now, its audio following as it
            // is captured, so the release waits only for the transcript.
            var streamed = BeginStreamedTake(startProvider);

            // No device open, no file created: the mic is already streaming into
            // the pre-roll ring, so this just attaches a writer and seeds it with
            // audio from before the keypress. On a cold device it falls back to
            // opening one (the old ~130 ms path) and preRoll comes back 0.
            int preRoll = _mic?.BeginCapture(streamed == null ? null : streamed.Append) ?? -1;
            if (preRoll < 0)
            {
                streamed?.Dispose();
                Log("[mic] capture could not start — no usable input device");
                Volatile.Write(ref _recState, 0);
                ResetUi();
                PlayUiSound(UiSound.Error);
                FlashStatus("No mic!", 1500);
                return;
            }

            _streamedTake = streamed;

            Log($"[diag] StartBatchDictation: capturing (pre-roll {preRoll}ms, mic was {(preRoll > 0 ? "warm" : "cold")}{(streamed != null ? ", upload streaming" : "")})");

            // Otherwise the connection is at least opened while the user talks.
            if (streamed == null) PrewarmConnection(startProvider);
        }

        /// <summary>Opens the take's upload at the key-press when the active
        /// provider can take one (ElevenLabs) and StreamUpload is on. Null
        /// otherwise, or if starting it fails; the take then goes out as a WAV
        /// at release exactly as before.</summary>
        private StreamedTranscription? BeginStreamedTake(ApiProvider? provider)
        {
            if (!_streamUpload || _transcribers == null || provider == null
                || provider.TranscriberKind != TranscriberKind.Http || !provider.IsElevenLabs)
                return null;
            try
            {
                return _transcribers.GetOrCreate(provider) is HttpTranscriber { CanStream: true } http
                    ? http.BeginStreamedTranscription(_contextBiasTerms)
                    : null;
            }
            catch (Exception ex)
            {
                Log($"[stream] could not open the upload ({ex.GetType().Name}: {ex.Message}); the take will go out as a file");
                return null;
            }
        }

        private async Task StopBatchDictationAsync()
        {
            if (Interlocked.CompareExchange(ref _recState, 2, 1) != 1) return;

            // Taken over here so that every way out of this method either
            // finishes the take's upload stream or cancels it (the finally).
            var streamed = _streamedTake;
            _streamedTake = null;

            // Press-to-release duration is the intent signal. A brush against
            // the key is well under 250 ms; deliberate dictation never is.
            long holdMs = Environment.TickCount64 - _pressTicks;
            bool accidental = holdMs < _minHoldMs;
            Log($"[diag] StopBatchDictation: enter (held {holdMs}ms)");

            // Outside the try so the catch can still give it an outcome.
            UnsentTakes.Take? take = null;
            try
            {
                // ── Pipeline timing instrumentation ──
                var swBatch = System.Diagnostics.Stopwatch.StartNew();
                long tCapture, tTranscribe, tPaste;

                // Detach the writer and collect the WAV. The mic itself keeps
                // running, so there's no device teardown here any more — that
                // used to cost 112-134 ms on every dictation. An accidental tap
                // skips the post-roll drain entirely; there's nothing to save.
                var mic = _mic;
                byte[]? wav = mic == null
                    ? null
                    : await Task.Run(() => mic.EndCapture(accidental ? 0 : _postRollMs));
                bool micInterrupted = mic?.LastCaptureInterrupted == true;
                tCapture = swBatch.ElapsedMilliseconds;

                // ── Guard 1: accidental press ────────────────────────────
                // Discarded before it can cost an API call, an error tone, or
                // (worst case) a 120 s local-server spawn that pins the state
                // machine and locks the hotkey out.
                if (accidental)
                {
                    Log($"[skip] held {holdMs}ms < {_minHoldMs}ms — discarded, no transcription");
                    PlayUiSound(UiSound.Dismissed);
                    FlashStatus("(tap)");
                    return;
                }

                PlayUiSound(UiSound.Stop);

                if (wav is not { Length: > 0 })
                {
                    if (micInterrupted)
                    {
                        Log("[error] the microphone failed before any audio was captured — nothing to transcribe");
                        PlayUiSound(UiSound.Error);
                        FlashStatus("Mic lost!", 2500);
                        return;
                    }
                    Log("[skip] no audio captured");
                    PlayUiSound(UiSound.Dismissed);
                    FlashStatus("(no audio)");
                    return;
                }

                // Debug/replay copy only — transcription uses the bytes in
                // memory. Off the hot path because MyDocuments is OneDrive-
                // synced here and a sync stall would otherwise stall dictation.
                // Written BEFORE the silence gate on purpose: if that gate ever
                // misjudges real speech, the audio is still on disk to check.
                WriteDebugWav(wav);

                double audioMs = GetWavDurationMs(wav);
                var level = await Task.Run(() => SpeechDetector.Measure(wav));

                // ── The mic failed mid-take ──────────────────────────────
                // A device error (unplug, driver reset) is flagged by
                // MicCapture. A mic that just stopped delivering buffers shows
                // up as a capture far SHORTER than the hold — normally it's
                // longer, since it starts with the pre-roll. Either way the
                // take is missing its end; it still goes out (what was said
                // before the failure is real), but it's flagged on delivery
                // instead of passing for a complete note.
                bool micStalled = !micInterrupted && audioMs + MicStallToleranceMs < holdMs;
                bool micCutOut = micInterrupted || micStalled;
                if (micCutOut)
                {
                    Log($"[error] the microphone {(micInterrupted ? "failed" : "stopped delivering audio")} mid-dictation: captured {audioMs:F0}ms of a {holdMs}ms hold — the take is missing its end");
                    if (micStalled)
                    {
                        // A stalled device never recovers by itself, and its
                        // frozen pre-roll would seed the next take with stale
                        // audio. Reopen it now.
                        Log("[mic] reopening the capture device after the stall");
                        mic?.DeviceChanged();
                    }
                }

                // ── Guard 2a: the mic produced NO signal ─────────────────
                // Not the same as "said nothing". A real microphone always has
                // a noise floor (measured 0.0006-0.0012 RMS here); exact
                // digital zero on a deliberate hold means the input is muted
                // or dead — the Windows privacy switch, a hardware mute, a
                // driver handing back an empty stream. The user just dictated
                // into nothing, and the quiet "(silence)" blip below would let
                // them walk away believing it worked.
                if (level.Peak <= 0)
                {
                    Log($"[error] the microphone delivered pure digital silence ({audioMs:F0}ms, peak 0) — input muted or dead; nothing was recorded");
                    PlayUiSound(UiSound.Error);
                    FlashStatus("No mic signal!", 3000);
                    return;
                }

                // ── Guard 2: held but silent ─────────────────────────────
                // Covers "pressed and then thought about what to say". Deciding
                // it locally skips a round-trip, and keeps silence away from
                // local models, which can hallucinate text out of it.
                //
                // Silent means BOTH a quiet whole take (RMS under the
                // threshold; not peak, which a single fan or keyboard transient
                // pushes to 0.012 in a silent room) AND no sustained speech
                // anywhere in it (SpeechDetector). The RMS test alone threw
                // away real, quiet speech on 2026-09-23: those takes averaged
                // 0.0017-0.0026, and speech had been assumed to run 0.01-0.1.
                //
                // A take judged silent is still kept (the newest few, under
                // ↻ Unsent → Judged silent), and the levels are logged on
                // every capture so the thresholds can be re-checked.
                if (_silenceThreshold > 0 && SpeechDetector.IsSilent(level, _silenceThreshold))
                {
                    if (micCutOut)
                    {
                        // Silent because the mic died, not because nothing was
                        // said — the quiet blip would pass a lost dictation
                        // off as a pause. Kept like any take judged silent, in
                        // case what it did capture was quiet speech.
                        Log($"[error] the take is silent because the microphone cut out ({audioMs:F0}ms captured of a {holdMs}ms hold) — nothing to transcribe; kept under ↻ Unsent → Judged silent");
                        _unsent?.KeepQuiet(wav, GetActiveProvider(), audioMs / 1000.0, "silent after the mic cut out");
                        PlayUiSound(UiSound.Error);
                        FlashStatus("Mic lost!", 3000);
                        return;
                    }
                    Log($"[skip] {audioMs:F0}ms of audio, RMS {level.Rms:F5} < {_silenceThreshold:F5} and no sustained speech (peak {level.Peak:F4}, floor {level.NoiseFloor:F5}) — nothing said, no transcription; kept under ↻ Unsent → Judged silent");
                    _unsent?.KeepQuiet(wav, GetActiveProvider(), audioMs / 1000.0, $"judged silent (RMS {level.Rms:F4})");
                    CueQuietTake(holdMs, "(silence)", "Too quiet? Saved ↻");
                    return;
                }
                Log($"[diag] captured {audioMs:F0}ms, {level.Describe()}");

                lblStatus.Content = "Processing...";
                lblStatus.Opacity = 1;

                var provider = GetActiveProvider();
                if (provider == null)
                {
                    Log("[diag] Transcribe: no active provider");
                    PlayUiSound(UiSound.Error);
                    FlashStatus("Error", 1500);
                    return;
                }

                // Journaled BEFORE the send: from here on no failure — a
                // timeout, an outage, a crash mid-request — can cost this take.
                // Delivery deletes it; every other outcome keeps it for a retry.
                take = _unsent?.Begin(wav, provider, audioMs / 1000.0);

                Log("[diag] StopBatchDictation: pre-transcribe");
                var result = await TranscribeTakeAsync(provider, wav, audioMs, streamed);
                string? text = result.Text;
                tTranscribe = swBatch.ElapsedMilliseconds;
                Log($"[diag] StopBatchDictation: post-transcribe, text.Length={text?.Length ?? -1}");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var shortfall = CheckCoverage(wav, audioMs, result);

                    Log("[diag] StopBatchDictation: pre-paste");
                    bool pasted = await DeliverAsync(text);
                    tPaste = swBatch.ElapsedMilliseconds;
                    Log("[diag] StopBatchDictation: post-paste, pre-history");
                    HistoryService.Add(text);
                    Log("[diag] StopBatchDictation: post-history");

                    // An incomplete transcript keeps its audio: a retry (or
                    // another provider) may get the part that's missing.
                    if (take != null)
                    {
                        if (shortfall is { } sf) _unsent!.Keep(take, UnsentTakes.Incomplete, "incomplete: " + sf.Describe());
                        else _unsent!.Delivered(take);
                    }

                    // One cue per outcome. Text that needs a second look gets
                    // the Warn double-pulse and a status that says why — the
                    // Success chirp must only ever mean "all of it, where you
                    // were typing".
                    if (shortfall is { } missing)
                        WarnDelivered("⚠ Check the end!", "Transcript may be incomplete",
                            $"{result.ProviderName} {missing.Describe()}. {(pasted ? "It was pasted" : "It's on the clipboard")} — check the end of the note. The audio is saved: ↻ Unsent dictations can retry it.");
                    else if (micCutOut)
                        WarnDelivered("⚠ Mic cut out!", "Microphone stopped mid-dictation",
                            $"Only the first {audioMs / 1000.0:F0}s of a {holdMs / 1000.0:F0}s hold were recorded. {(pasted ? "That part was pasted" : "That part is on the clipboard")} — anything said after it is missing.");
                    else if (!pasted)
                        WarnDelivered("Copied — paste it", "Text copied, not pasted",
                            "The window you dictated into was no longer in front, so nothing was pasted. The text is on the clipboard — paste it where it belongs.");
                    else
                        PlayUiSound(UiSound.Success);

                    int charCount = text.Length;
                    Log($"Batch pipeline: capture={tCapture}ms  transcribe={tTranscribe - tCapture}ms  {(pasted ? "paste" : "clipboard")}={tPaste - tTranscribe}ms  ({charCount} chars)  TOTAL={swBatch.ElapsedMilliseconds}ms");
                }
                else if (text != null)
                {
                    // The provider answered but returned no text. For a take
                    // the detector judges silent that's expected — and only
                    // reachable with the gate disabled, since the gate drops
                    // those before any API call. It gets the quiet-take cue
                    // and is kept, not deleted.
                    //
                    // Anything else had real sound in it (loud enough, or
                    // sustained speech however quiet), and nothing came out —
                    // which is also exactly how a broken backend presents: a
                    // CrispASR server missing a DLL answering with empty text,
                    // a wrong or corrupt model, a provider-side fault. As a
                    // quiet "(nothing heard)" every dictation could fail that
                    // way unnoticed until someone saw nothing was pasting, so
                    // it is a failure: error tone, a status that says so, and
                    // the evidence in the log.
                    double speechFloor = _silenceThreshold > 0 ? _silenceThreshold : DefaultSilenceThreshold;
                    if (SpeechDetector.IsSilent(level, speechFloor) && !micCutOut)
                    {
                        Log($"[skip] provider returned no text for a silent take ({level.Describe()}) — kept under ↻ Unsent → Judged silent");
                        if (take != null) _unsent!.Keep(take, UnsentTakes.Quiet, "no text for a silent take");
                        CueQuietTake(holdMs, "(nothing heard)", "Nothing heard? Saved ↻");
                    }
                    else
                    {
                        Log($"[error] {result.ProviderName} returned NO TEXT for {audioMs:F0}ms of audio with sound in it ({level.Describe()}; speech floor {speechFloor:F5}) — nothing pasted; the audio is kept for a retry");
                        if (take != null) _unsent!.Keep(take, UnsentTakes.Failed, "no text returned");
                        PlayUiSound(UiSound.Error);
                        FlashStatus("No text! Saved ↻", 3000);
                    }
                }
                else
                {
                    // Timed out or failed outright. The take's audio is kept,
                    // and the tray says so in words — a status flash alone is
                    // easy to miss when you're already looking at the chart.
                    if (take != null) _unsent!.Keep(take, UnsentTakes.Failed, result.FailReason);
                    PlayUiSound(UiSound.Error);
                    FlashStatus(result.DeadlineHit ? "Timed out — saved ↻" : "Failed — saved ↻", 3000);
                    _tray?.ShowBalloon("Dictation not transcribed",
                        $"{result.ProviderName}: {result.FailReason}. The audio is saved — right-click the tray icon → ↻ Unsent dictations to retry.",
                        warning: true);
                }

                Log("[diag] StopBatchDictation: exit");
            }
            catch (Exception ex)
            {
                Log($"[diag] StopBatchDictation: UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                // Whatever broke, the take's audio must not go down with it.
                if (take is { Resolved: false })
                    _unsent?.Keep(take, UnsentTakes.Failed, $"WhisperInk error ({ex.GetType().Name})");
                throw;
            }
            finally
            {
                // A take that was discarded (tap, silence, no signal) never
                // finished its stream: cancelling it mid-body means the service
                // never gets a complete request to transcribe or bill.
                streamed?.Dispose();

                // Release on EVERY exit path. A thrown transcription/paste (e.g.
                // an HTTP timeout, or a network error) used to skip this,
                // stranding Ctrl "down" — the keyboard hook swallows the user's
                // physical key-up, so this synthetic release is the only thing
                // that tells the OS the key came back up. A stuck Ctrl turns the
                // next keystroke into Ctrl+<key> (e.g. Ctrl+O pops an Open dialog).
                _injector.ReleaseAllModifierKeys();
                Volatile.Write(ref _recState, 0);
                ResetUi();
                UpdateStatusLabel();
                RestartMicIdleTimer();
            }
        }

        /// <summary>The cue for a take judged silent (and kept under ↻ Unsent
        /// → Judged silent). A short hold gets the quiet Dismissed blip; a long
        /// one gets Warn, because it may have been speech the mic barely
        /// caught, and the quiet blip alone once let three such takes go
        /// unnoticed.</summary>
        private void CueQuietTake(long holdMs, string shortStatus, string longStatus)
        {
            if (holdMs >= LongSilentHoldMs)
            {
                PlayUiSound(UiSound.Warn);
                FlashStatus(longStatus, 3000);
            }
            else
            {
                PlayUiSound(UiSound.Dismissed);
                FlashStatus(shortStatus);
            }
        }

        /// <summary>Shows a transient status without holding the recording
        /// state machine. The old error path did `await Task.Delay(1500)`
        /// INSIDE the try, so _recState stayed at Stopping for the whole
        /// delay — the hotkey was dead for ~1.5 s after every mis-press, on
        /// top of the transcription round-trip it had just wasted. This runs
        /// at Background priority so it lands after the finally block's
        /// UpdateStatusLabel() rather than being clobbered by it.
        ///
        /// Both halves bail out while recording. Dismissing an accidental tap
        /// and immediately starting a real dictation is the common case, and
        /// UpdateStatusLabel() sets the label unconditionally — without these
        /// checks a stale flash would wipe "🎙 REC" off a live recording.</summary>
        private void FlashStatus(string text, int ms = 900)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsRecording) return;
                lblStatus.Content = text;
                lblStatus.Opacity = 1;
                _statusFlashTimer?.Stop();
                _statusFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
                _statusFlashTimer.Tick += (_, _) =>
                {
                    _statusFlashTimer?.Stop();
                    _statusFlashTimer = null;
                    if (!IsRecording) UpdateStatusLabel();
                };
                _statusFlashTimer.Start();
            }), DispatcherPriority.Background);
        }

        /// <summary>Writes the replay/debug copy of the capture. Fire-and-forget:
        /// the transcription path uses the in-memory bytes, and MyDocuments
        /// resolves to a OneDrive-synced folder on this machine, so a sync stall
        /// must never be able to stall a dictation.</summary>
        private void WriteDebugWav(byte[] wav)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRecordings");
            string path = Path.Combine(folder, "temp_audio.wav");
            _ = Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    File.WriteAllBytes(path, wav);
                }
                catch (Exception ex) { Log($"[diag] debug WAV write failed: {ex.Message}"); }
            });
        }

        /// <summary>Arms the countdown that closes the warm mic once dictation
        /// has been idle for a while, so the Windows microphone-in-use
        /// indicator isn't lit for the whole session.</summary>
        private void RestartMicIdleTimer()
        {
            _micIdleTimer?.Stop();
            if (!_warmMicEnabled || _warmMicIdleSeconds <= 0) return;
            _micIdleTimer ??= CreateMicIdleTimer();
            _micIdleTimer.Interval = TimeSpan.FromSeconds(_warmMicIdleSeconds);
            _micIdleTimer.Start();
        }

        private DispatcherTimer CreateMicIdleTimer()
        {
            var timer = new DispatcherTimer();
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (IsRecording || IsStopping) return;
                if (_mic?.IsWarm == true)
                {
                    _mic.Release();
                    Log($"[mic] released after {_warmMicIdleSeconds}s idle");
                }
            };
            return timer;
        }

        /// <summary>Opens the mic ahead of the first dictation so it isn't the
        /// press itself that pays the ~130 ms device open.</summary>
        private void WarmMic()
        {
            if (!_warmMicEnabled || _mic == null) return;
            if (_mic.EnsureOpen()) RestartMicIdleTimer();
        }

        // ── Transcription dispatch ──────────────────────────────────────
        //
        // Single entry point for every batch transcription — live takes and
        // retries alike. The factory hands back the right ITranscriber for the
        // provider (cloud HTTP, auto-spawned CrispASR server, Google Chirp 3,
        // Soniox, Deepgram, Modulate, Smallest.ai, Reson8); we don't care which
        // it is. Adding a new model is a config-only change — no new branches
        // here.

        /// <summary>What one transcription attempt produced — and, when it
        /// produced nothing, a short reason fit for the menu and the tray.</summary>
        private readonly record struct TakeTranscription(
            string? Text,
            string ProviderName,
            bool DeadlineHit,
            string FailReason,
            double? LastWordEndSeconds,
            double? DecodedAudioSeconds);

        private async Task<TakeTranscription> TranscribeTakeAsync(ApiProvider provider, byte[] wavBytes, double audioMs,
                                                                 StreamedTranscription? streamed = null)
        {
            static TakeTranscription Fail(string name, string reason) => new(null, name, false, reason, null, null);
            if (_transcribers == null) return Fail(provider.Name, "WhisperInk was still starting");

            ITranscriber transcriber;
            try { transcriber = _transcribers.GetOrCreate(provider); }
            catch (Exception ex)
            {
                Log($"Transcriber init failed for {provider.Id}: {ex.Message}");
                return Fail(provider.Name, "the provider could not start");
            }

            if (!transcriber.IsReady(out var diag))
            {
                Log($"{transcriber.DisplayName}: {diag}");
                return Fail(transcriber.DisplayName, $"not ready ({diag})");
            }

            // Scaled to the recording (TranscriptionDeadline), never flat: a
            // flat deadline fails long takes however healthy the service is.
            var deadline = TranscriptionDeadline.For(provider, audioMs / 1000.0);
            using var cts = new CancellationTokenSource(deadline);
            Log($"[diag] {transcriber.DisplayName}: deadline {deadline.TotalSeconds:F0}s for {audioMs / 1000.0:F1}s of audio");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? result;
            string how = "";
            // A take streamed while it was spoken is finished through the
            // transcriber that opened it, only if that is still this
            // provider's (a provider switch or a settings save during the hold
            // replaces it). Anything else about the stream that is off, it
            // falls back to sending the WAV, under the same deadline.
            if (streamed != null && ReferenceEquals(streamed.Transcriber, transcriber))
            {
                var outcome = await streamed.FinishAsync(StreamedTranscription.PcmLength(wavBytes), cts.Token);
                if (outcome.FallBack)
                {
                    Log($"[stream] {provider.Id}: sending the take as a file instead ({outcome.Reason})");
                    result = await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms, cts.Token);
                    how = " (stream unusable, sent as a file)";
                }
                else
                {
                    result = outcome.Text;
                    how = " after release (streamed)";
                }
            }
            else
            {
                if (streamed != null)
                {
                    Log($"[stream] {provider.Id}: the provider changed during the take; sending it as a file");
                    streamed.Dispose();
                }
                result = await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms, cts.Token);
            }
            sw.Stop();

            double rtfx    = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
            string preview = result == null ? "(null)" : result[..Math.Min(200, result.Length)];
            Log($"{transcriber.DisplayName} took {sw.ElapsedMilliseconds}ms{how} on {audioMs:F0}ms audio = RTFx {rtfx:F2}x -- result: {preview}");

            bool deadlineHit = result == null && cts.IsCancellationRequested;
            if (deadlineHit)
                Log($"[error] {transcriber.DisplayName} did not finish within its {deadline.TotalSeconds:F0}s deadline ({audioMs:F0}ms of audio) — nothing pasted; the audio is kept for a retry");

            var coverage = transcriber as ITranscriptCoverage;
            string failReason = result != null ? ""
                : deadlineHit ? $"no answer within its {deadline.TotalSeconds:F0}s deadline"
                : "the request failed (details in debug.log)";
            return new TakeTranscription(result, transcriber.DisplayName, deadlineHit, failReason,
                coverage?.LastWordEndSeconds, coverage?.DecodedAudioSeconds);
        }

        /// <summary>The incomplete-transcript check (TranscriptCoverage),
        /// for providers that report word timing. Null = no concern.</summary>
        private static TranscriptCoverage.Shortfall? CheckCoverage(byte[] wav, double audioMs, TakeTranscription result)
        {
            if (result.LastWordEndSeconds == null && result.DecodedAudioSeconds == null) return null;
            double? lastSpeech = TranscriptCoverage.LastSpeechSeconds(wav);
            var shortfall = TranscriptCoverage.Check(audioMs / 1000.0, lastSpeech,
                result.LastWordEndSeconds, result.DecodedAudioSeconds);
            if (shortfall is { } s)
                Log($"[warn] {result.ProviderName} transcript may be INCOMPLETE — {s.Describe()}; recorded {audioMs / 1000.0:F1}s, last speech at {lastSpeech?.ToString("F1") ?? "?"}s. Delivered anyway, audio kept for a retry");
            return shortfall;
        }

        /// <summary>Pastes into the window the dictation was started in — but
        /// only when that window is really in front. SetForegroundWindow's
        /// result used to be ignored: when Windows refused the switch (it does,
        /// for a background process, once the user has clicked elsewhere), the
        /// Ctrl+V landed in whatever had focus by then — a transcript pasted
        /// into the wrong app, or the wrong chart. Now anything short of a
        /// verified target leaves the text on the clipboard instead, and the
        /// caller warns. True when pasted.</summary>
        private async Task<bool> DeliverAsync(string text)
        {
            IntPtr target = _targetWindow;
            IntPtr self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (target != IntPtr.Zero && target != self)
            {
                if (GetForegroundWindow() != target) SetForegroundWindow(target);
                // A granted switch can land a beat after the call returns.
                for (int i = 0; i < 10 && GetForegroundWindow() != target; i++)
                    await Task.Delay(15);
                if (GetForegroundWindow() == target)
                {
                    _injector.PasteTextToActiveWindow(text);
                    return true;
                }
            }

            // Handles only — window titles can carry patient names.
            IntPtr front = GetForegroundWindow();
            bool copied = _injector.CopyToClipboard(text);
            Log($"[warn] not pasted: the window the dictation started in (0x{target.ToInt64():X}) is not in front (0x{front.ToInt64():X}{(front == self ? ", WhisperInk itself" : "")}) — text {(copied ? "left on the clipboard" : "could NOT be put on the clipboard either; it is in History")}");
            return false;
        }

        /// <summary>Delivered, but look at it: the Warn double-pulse, a status
        /// that says why, and the full story in the tray — a status flash on
        /// its own is easy to miss while looking at the chart.</summary>
        private void WarnDelivered(string status, string balloonTitle, string balloonBody)
        {
            PlayUiSound(UiSound.Warn);
            FlashStatus(status, 4000);
            _tray?.ShowBalloon(balloonTitle, balloonBody, warning: true);
        }

        /// <summary>The mic died while a dictation was being recorded (unplug,
        /// driver reset). Say so NOW, while the user is still talking into it,
        /// rather than after release. The take keeps what was captured before
        /// the failure and is flagged when it is delivered.</summary>
        private void OnMicLostMidDictation()
        {
            if (!IsRecording) return;
            Log("[error] microphone lost mid-dictation — nothing said from here on is being recorded");
            PlayUiSound(UiSound.Error);
            lblStatus.Content = "🎙 MIC LOST";
        }

        // ── Unsent takes: retry ───────────────────────────────────────

        /// <summary>Re-transcribes a kept take. The text goes to the
        /// clipboard, not into a window: by the time anyone retries, the window
        /// the take was dictated into is long gone from the foreground, and a
        /// guess would paste into the wrong place. A local model standing in
        /// for the usual provider is flagged as a fallback everywhere it
        /// shows.</summary>
        private async Task RetryTakeAsync(UnsentTakes.Take take, ApiProvider provider, bool localFallback)
        {
            // The same gate as a dictation, so a retry and a live take never
            // overlap; a press during the retry gets the Dismissed blip.
            if (Interlocked.CompareExchange(ref _recState, 2, 0) != 0)
            {
                FlashStatus("Busy — try again", 1500);
                return;
            }
            try
            {
                byte[]? wav = _unsent?.ReadAudio(take);
                if (wav is not { Length: > 0 })
                {
                    PlayUiSound(UiSound.Error);
                    FlashStatus("Audio missing!", 2500);
                    return;
                }
                double audioMs = GetWavDurationMs(wav);
                lblStatus.Content = localFallback ? "Retrying (local)…" : "Retrying…";
                lblStatus.Opacity = 1;
                Log($"[retry] {take.Id} ({audioMs:F0}ms; was: {take.Reason}) with {provider.Name}{(localFallback ? " — LOCAL FALLBACK" : "")}");

                var result = await TranscribeTakeAsync(provider, wav, audioMs);
                // A fallback model shouldn't stay resident (GBs of VRAM) for a
                // provider nobody switched to.
                if (localFallback) _transcribers?.Drop(provider.Id);

                if (string.IsNullOrWhiteSpace(result.Text))
                {
                    string reason = result.Text == null ? result.FailReason : "no text returned";
                    bool judgedSilent = take.Status == UnsentTakes.Quiet;
                    if (judgedSilent && result.Text != null)
                    {
                        // Judged silent, and the provider heard nothing either:
                        // almost certainly silence. It stays under Judged
                        // silent, where its own small allowance retires it,
                        // instead of joining the real failures.
                        _unsent?.Keep(take, UnsentTakes.Quiet, "judged silent; a retry heard nothing either");
                        PlayUiSound(UiSound.Dismissed);
                        FlashStatus("Nothing heard", 2500);
                        return;
                    }
                    // A take judged silent whose retry FAILED stays where it
                    // was too; the failure itself still gets the error cue.
                    _unsent?.Keep(take, judgedSilent ? UnsentTakes.Quiet : UnsentTakes.Failed, "retry failed: " + reason);
                    PlayUiSound(UiSound.Error);
                    FlashStatus("Retry failed", 2500);
                    _tray?.ShowBalloon("Retry failed", $"{result.ProviderName}: {reason}. The audio is still saved.", warning: true);
                    return;
                }

                string text = result.Text;
                var shortfall = CheckCoverage(wav, audioMs, result);
                _injector.CopyToClipboard(text);
                HistoryService.Add(text);
                if (shortfall is { } sf) _unsent?.Keep(take, UnsentTakes.Incomplete, "retry incomplete: " + sf.Describe());
                else _unsent?.Remove(take);
                Log($"[retry] {take.Id}: {text.Length} chars on the clipboard{(localFallback ? " (LOCAL FALLBACK)" : "")}{(shortfall != null ? " (incomplete)" : "")}");

                string who = localFallback
                    ? $"Transcribed by {result.ProviderName} — a LOCAL FALLBACK, not your usual provider. Check it before it goes in a chart."
                    : $"Transcribed by {result.ProviderName}.";
                string body = $"{who} It's on the clipboard — paste it where it belongs."
                              + (shortfall is { } s ? $" It may be incomplete: {s.Describe()}." : "");
                if (localFallback || shortfall != null)
                {
                    PlayUiSound(UiSound.Warn);
                    FlashStatus(localFallback ? "Fallback — copied" : "⚠ Copied — check end", 4000);
                    _tray?.ShowBalloon(shortfall != null ? "Recovered — may be incomplete" : "Recovered with a local fallback", body, warning: true);
                }
                else
                {
                    PlayUiSound(UiSound.Success);
                    FlashStatus("Recovered — copied", 3000);
                    _tray?.ShowBalloon("Recovered dictation copied", body);
                }
            }
            finally
            {
                Volatile.Write(ref _recState, 0);
                UpdateStatusLabel();
            }
        }

        /// <summary>Local presets that could transcribe right now — exe and
        /// model on disk — offered as the Retry menu's offline fallback.</summary>
        private IEnumerable<ApiProvider> ReadyLocalProviders(string? exceptId) =>
            _providers.Where(p => p.IsLocalProvider && p.Id != exceptId && LocalModelPresent(p));

        private static bool LocalModelPresent(ApiProvider p)
        {
            if (string.IsNullOrWhiteSpace(p.LocalModelGlob)) return false;
            string sub = string.IsNullOrWhiteSpace(p.LocalModelFolder) ? "cohere-gguf" : p.LocalModelFolder;
            string folder = Path.Combine(ConfigFolder, sub);
            return File.Exists(Path.Combine(folder, "crispasr.exe"))
                && MissingIfAbsent(folder, p.LocalModelGlob) == null;
        }

        /// <summary>↻ Unsent dictations: every kept take, newest first (ten
        /// listed), each with Retry on the active provider and on any local
        /// model that could run now — the offline fallback, labelled as one.
        /// Rebuilt on every open, like the rest of the menu.</summary>
        private MenuNode BuildUnsentMenu()
        {
            var all = _unsent?.List() ?? new List<UnsentTakes.Take>();
            var takes = all.Where(t => t.Status != UnsentTakes.Quiet).ToList();
            var quiet = all.Where(t => t.Status == UnsentTakes.Quiet).ToList();
            var active = GetActiveProvider();
            var fallbacks = ReadyLocalProviders(active?.Id).ToList();

            MenuNode TakeNode(UnsentTakes.Take t)
            {
                var actions = new List<MenuNode>();
                if (active != null)
                    actions.Add(new MenuNode
                    {
                        Header = $"Retry with {active.Name}",
                        Action = () => RunSafe(() => RetryTakeAsync(t, active, localFallback: false), "RetryTake"),
                    });
                foreach (var local in fallbacks)
                {
                    var l = local;
                    actions.Add(new MenuNode
                    {
                        Header = $"Retry on {l.Name} (local fallback)",
                        Action = () => RunSafe(() => RetryTakeAsync(t, l, localFallback: true), "RetryTake"),
                    });
                }
                actions.Add(MenuNode.Separator());
                actions.Add(new MenuNode { Header = "Show audio file", Action = () => OpenExplorerSelect(t.WavPath) });
                return new MenuNode
                {
                    Header = t.Label,
                    ToolTip = string.IsNullOrWhiteSpace(t.ProviderName) ? null : $"Recorded for {t.ProviderName}",
                    Children = actions,
                };
            }

            var children = new List<MenuNode>();
            if (takes.Count == 0)
                children.Add(new MenuNode { Header = "(none — every dictation was delivered)", IsEnabled = false });
            foreach (var take in takes.Take(10))
                children.Add(TakeNode(take));
            if (takes.Count > 10)
                children.Add(new MenuNode { Header = $"…and {takes.Count - 10} older (open the folder)", IsEnabled = false });
            // Takes the silence check dropped without sending, kept in case one
            // was quiet speech. Apart from the real failures, and not counted
            // in the header, since most of them really are silence.
            if (quiet.Count > 0)
            {
                children.Add(MenuNode.Separator());
                children.Add(new MenuNode
                {
                    Header = $"🔇 Judged silent ({quiet.Count})",
                    ToolTip = $"Dropped as silence without being sent. The newest {UnsentTakes.MaxQuietCount} are kept in case one was quiet speech.",
                    Children = quiet.Select(TakeNode).ToList(),
                });
            }
            children.Add(MenuNode.Separator());
            children.Add(new MenuNode
            {
                Header = "📂 Open unsent folder",
                Action = () => OpenFolder(_unsent?.Folder ?? Path.Combine(ConfigFolder, "unsent")),
            });

            return new MenuNode
            {
                Header = takes.Count == 0 ? "↻ Unsent dictations" : $"↻ Unsent dictations ({takes.Count})",
                Children = children,
            };
        }

        // ── Text input ──────────────────────────────────────────────────
        // Typing, paste-with-clipboard-restore, selection grab, and modifier
        // release all live in TextInjector (_injector).

        private void ResetUi()
        {
            _animationTimer.Stop();
            HistogramPanel.Visibility = Visibility.Collapsed;
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 64, 64));

            foreach (var child in HistogramPanel.Children)
                if (child is Border bar) bar.Height = 2;
        }

        private void UpdateHistogram()
        {
            foreach (var child in HistogramPanel.Children)
            {
                if (child is Border bar)
                {
                    double target = IsRecording ? _rng.Next(4, 22) : 2;
                    bar.Height = bar.Height + (target - bar.Height) * 0.4;
                }
            }
        }

        /// <summary>Chirp. Delegates to the persistent output device in
        /// UiSoundPlayer — the previous implementation synthesised a WAV and
        /// called SoundPlayer.PlaySync() per chirp, which measured 190-222 ms
        /// for a 30 ms tone because winmm reopens the render endpoint every
        /// time. That delay was the "lag on the beep": the press had already
        /// registered, but the confirmation arrived a fifth of a second late.</summary>
        private void PlayUiSound(UiSound type)
        {
            if (!_isSoundEnabled) return;
            _sounds?.Play(type);
        }

        // ── Shared app menu ────────────────────────────────────────────
        // ONE canonical MenuNode tree drives both surfaces: the floating
        // bar's WPF context menu (right-click, rendered here) and the tray
        // icon's WinForms menu (rendered by TrayIconManager). Rebuilt on
        // every open so check states are always current. Build order is
        // display order.

        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var menu = WpfMenuRenderer.Build(BuildAppMenu());
            menu.IsOpen = true;
        }

        private IReadOnlyList<MenuNode> BuildAppMenu() => new List<MenuNode>
        {
            // Tray-only header: the floating bar already shows status inline.
            new MenuNode
            {
                Header = $"{_lastHealth.Dot}  Active provider: {ActiveProviderName}",
                IsEnabled = false,
                ToolTip = string.IsNullOrWhiteSpace(_lastHealth.Summary) ? null : _lastHealth.Summary,
                Surface = MenuSurface.TrayOnly,
            },
            new MenuNode { Header = "Show Window", Action = ShowMainWindow, Surface = MenuSurface.TrayOnly },
            MenuNode.Separator(MenuSurface.TrayOnly),

            BuildProviderMenu(),
            MenuNode.Separator(),
            BuildMicMenu(),
            new MenuNode
            {
                Header = _isSoundEnabled ? "🔊 Sound: ON" : "🔇 Sound: OFF",
                Action = () => { _isSoundEnabled = !_isSoundEnabled; SaveConfig(); },
            },
            MenuNode.Separator(),
            new MenuNode
            {
                Header = "🎯 Context Bias Terms",
                Action = () =>
                {
                    var biasWindow = new ContextBiasWindow(_contextBiasTerms);
                    if (biasWindow.ShowDialog() == true)
                    {
                        _contextBiasTerms = biasWindow.BiasTerms;
                        SaveConfig();
                    }
                },
            },
            new MenuNode { Header = "📋 History", Action = () => new HistoryWindow().Show() },
            BuildUnsentMenu(),
            MenuNode.Separator(),
            BuildGpuBackendMenu(),
            MenuNode.Separator(),
            new MenuNode { Header = "📂 Open config folder", ToolTip = ConfigFile, Action = () => OpenExplorerSelect(ConfigFile) },
            new MenuNode { Header = "Open debug log", Action = () => OpenPath(LogFile) },
            new MenuNode { Header = "Open model folder", Action = () => OpenFolder(Path.Combine(ConfigFolder, "cohere-gguf")) },
            new MenuNode { Header = "Copy support bundle", Action = CopySupportBundle },
            new MenuNode { Header = "Diagnose active provider", Action = DiagnoseActiveProvider },
            MenuNode.Separator(),
            new MenuNode { Header = "About…", Action = ShowAboutDialog },
            new MenuNode { Header = "View README", Action = () => OpenUrl(AboutWindow.ReadmeUrl) },
            MenuNode.Separator(),
            new MenuNode
            {
                Header = "Quit on close",
                IsChecked = _quitOnClose,
                Action = () => SetQuitOnClose(!_quitOnClose),
            },
            new MenuNode
            {
                Header = "Launch at Windows start",
                IsChecked = _launchAtStartup,
                Action = () => SetLaunchAtStartup(!_launchAtStartup),
            },
            MenuNode.Separator(),
            new MenuNode { Header = "⬇ Hide to tray", Action = HideToTray, Surface = MenuSurface.BarOnly },
            new MenuNode { Header = "❌ Quit", Action = QuitApplication },
        };

        private MenuNode BuildProviderMenu()
        {
            var children = new List<MenuNode>();
            foreach (var provider in _providers)
            {
                string pid = provider.Id; // capture for the closure
                children.Add(new MenuNode
                {
                    Header = provider.Name,
                    IsChecked = pid == _activeProviderId,
                    Action = () => SwitchProvider(pid),
                });
            }
            children.Add(MenuNode.Separator());
            children.Add(new MenuNode { Header = "⚙ Configure Providers...", Action = OpenProviderSettingsDialog });
            return new MenuNode { Header = $"🔌 Provider: {GetActiveProvider()?.Name ?? "?"}", Children = children };
        }

        private MenuNode BuildMicMenu()
        {
            var children = new List<MenuNode>();
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                int deviceIndex = i; // capture for the closure
                var cap = WaveIn.GetCapabilities(i);
                children.Add(new MenuNode
                {
                    Header = cap.ProductName,
                    IsChecked = i == _selectedDeviceNumber,
                    Action = () =>
                    {
                        _selectedDeviceNumber = deviceIndex;
                        SaveConfig();
                        // The warm mic is bound to the old index — reopen it on
                        // the new one, or the user's choice silently does nothing.
                        _mic?.DeviceChanged();
                    },
                });
            }

            children.Add(MenuNode.Separator());
            children.Add(new MenuNode
            {
                // Holding the device open is what removes the ~130 ms open from
                // the press and makes pre-roll possible; the cost is that
                // Windows shows the mic as in use between dictations.
                Header = _warmMicEnabled ? "⚡ Instant start: ON" : "⚡ Instant start: OFF",
                Action = () =>
                {
                    _warmMicEnabled = !_warmMicEnabled;
                    SaveConfig();
                    if (_warmMicEnabled) WarmMic();
                    else { _micIdleTimer?.Stop(); _mic?.Release(); }
                },
            });

            return new MenuNode { Header = "🎙 Microphone", Children = children };
        }

        private MenuNode BuildGpuBackendMenu()
        {
            (string label, string value)[] options =
            {
                ("Auto (recommended)", "auto"),
                ("Vulkan (GPU)",       "vulkan"),
                ("CUDA (NVIDIA GPU)",  "cuda"),
                ("CPU only",           "cpu"),
            };
            var children = new List<MenuNode>();
            foreach (var (label, value) in options)
            {
                string capture = value; // capture for the closure
                children.Add(new MenuNode
                {
                    Header = label,
                    IsChecked = string.Equals(_crispGpuBackend, capture, StringComparison.OrdinalIgnoreCase),
                    Action = () => SetCrispGpuBackend(capture),
                });
            }
            return new MenuNode
            {
                Header = "🖥 Local GPU backend",
                ToolTip = string.IsNullOrWhiteSpace(CrispGpuProbe.Summary) ? null : CrispGpuProbe.Summary,
                Children = children,
            };
        }

        // Shell helpers backing the menu actions (formerly in TrayIcon.cs).

        private static void OpenExplorerSelect(string file)
        {
            try { Process.Start("explorer.exe", $"/select,\"{file}\""); }
            catch (Exception ex) { Log($"Open explorer failed: {ex.Message}"); }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    MessageBox.Show($"Not found:\n{path}", "WhisperInk",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WhisperInk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WhisperInk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        // ════════════════════════════════════════════════════════════════
        // TRAY + HEALTH + SUPPORT BUNDLE + FIRST-RUN (Deliverables 3–6, 8)
        // ════════════════════════════════════════════════════════════════

        private void InitializeTrayAndHealth()
        {
            try
            {
                _tray = new TrayIconManager(
                    menuSource: BuildAppMenu,
                    onActivate: ShowMainWindow,
                    trayTooltip: () => $"WhisperInk — {_lastHealth.Dot} {ActiveProviderName}");
            }
            catch (Exception ex) { Log($"Tray init failed: {ex.Message}"); }

            try
            {
                _healthProbe = new HealthProbe(GetActiveProvider, OnHealthReport);
                _healthProbe.Start();
            }
            catch (Exception ex) { Log($"Health probe init failed: {ex.Message}"); }

            // One-shot GPU probe — runs crispasr.exe --help on a worker thread
            // so the UI can show "Detected: AMD Radeon Graphics (Vulkan)" next
            // to the backend combo. Failure is non-fatal.
            try { _ = CrispGpuProbe.StartAsync(); }
            catch (Exception ex) { Log($"GPU probe init failed: {ex.Message}"); }

            // Surface any missing local-model banner at startup.
            try { UpdateLocalModelBanner(); }
            catch (Exception ex) { Log($"Banner init failed: {ex.Message}"); }
        }

        private void OnHealthReport(HealthReport r)
        {
            _lastHealth = r;
            Dispatcher.BeginInvoke(() =>
            {
                UpdateHealthDot();
                _tray?.RefreshTooltip();
                // UpdateLocalModelBanner takes priority — missing GGUF is the
                // actionable failure, and it falls through to UpdateSetupBannerVisibility
                // when nothing is missing.
                UpdateLocalModelBanner();
            });
        }

        private void UpdateSetupBannerVisibility()
        {
            if (_lastHealth.Status == HealthStatus.Fail)
            {
                lblBanner.Text = $"Setup needed: {_lastHealth.Summary} — click to fix";
                SetupBanner.Visibility = Visibility.Visible;
                Height = 68;
            }
            else
            {
                SetupBanner.Visibility = Visibility.Collapsed;
                Height = 38;
            }
        }

        private void SetupBanner_Click(object sender, MouseButtonEventArgs e)
        {
            var prov = GetActiveProvider();
            if (prov == null) return;

            // Cloud providers with a missing key → open the provider
            // settings dialog. Local providers → open the model folder
            // so the user can drop files in.
            if (prov.IsLocalProvider)
            {
                try
                {
                    string sub = string.IsNullOrWhiteSpace(prov.LocalModelFolder) ? "cohere-gguf" : prov.LocalModelFolder;
                    string modelFolder = Path.Combine(ConfigFolder, sub);
                    if (!Directory.Exists(modelFolder)) Directory.CreateDirectory(modelFolder);
                    Process.Start(new ProcessStartInfo(modelFolder) { UseShellExecute = true });
                }
                catch (Exception ex) { Log($"Open model folder failed: {ex.Message}"); }
            }
            else
            {
                OpenProviderSettingsDialog();
            }
        }

        /// <summary>Opens the provider settings dialog and applies whatever the
        /// user chose when they save — provider list edits, the new active
        /// provider, and the local-GPU backend toggle.</summary>
        private void OpenProviderSettingsDialog()
        {
            var win = new ProviderSettingsWindow(
                _providers, _activeProviderId, _crispGpuBackend, CrispGpuProbe.Summary);
            if (win.ShowDialog() != true) return;

            _providers = win.ResultProviders;

            // The user may have edited any provider's URL / key / port / GGUF
            // path — drop every cached transcriber so the next dictation
            // re-creates against the new config.
            _transcribers?.DropAll();

            string? desiredActive = win.ResultActiveProviderId;
            if (!string.IsNullOrWhiteSpace(desiredActive) &&
                _providers.Any(p => p.Id == desiredActive))
            {
                ApplyProviderSwitch(desiredActive!);
            }
            else
            {
                // Active provider wasn't in the edited list (rare — deleted). Fall
                // back to the first provider so we never end up with a dangling id.
                if (!_providers.Any(p => p.Id == _activeProviderId))
                    ApplyProviderSwitch(_providers.First().Id);
                else
                    ApplyActiveProvider();
            }

            ApplyGpuBackendChange(win.ResultGpuBackend);

            SaveConfig();
            UpdateStatusLabel();
            UpdateLocalModelBanner();
            _healthProbe?.RequestProbe();
        }

        private void RunFirstRunCheck()
        {
            if (!_hasSeenFirstRun)
            {
                _hasSeenFirstRun = true;
                SaveConfig();
                _tray?.ShowBalloon(
                    "WhisperInk is running",
                    "Hold Ctrl+Space to dictate. Right-click the tray icon for support, logs, and diagnostics.");
            }
        }

        private void SyncLaunchAtStartupFromRegistry()
        {
            // External toggles (Task Manager → Startup apps, scripts, etc.)
            // can flip the registry entry behind our back. Treat the
            // registry as the source of truth at launch.
            bool regEnabled = AutoStart.IsEnabled();
            if (regEnabled != _launchAtStartup)
            {
                _launchAtStartup = regEnabled;
                SaveConfig();
            }
        }

        private void HideToTray()
        {
            try { Hide(); } catch { }
        }

        // ── Menu-backed actions (shared by tray + bar via BuildAppMenu) ──

        private string ActiveProviderName => GetActiveProvider()?.Name ?? "?";

        private void SetCrispGpuBackend(string value)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ApplyGpuBackendChange(value);
                SaveConfig();
            });
        }

        public void ShowMainWindow()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                Topmost = true;
            });
        }

        public void CopySupportBundle()
        {
            try
            {
                string zip = SupportBundle.Build(_providers, _activeProviderId);
                _tray?.ShowBalloon("Support bundle ready",
                    $"Saved to Desktop and copied to clipboard — paste into Slack / email.\n{Path.GetFileName(zip)}");
                Log($"Support bundle written: {zip}");
            }
            catch (Exception ex)
            {
                Log($"Support bundle failed: {ex.Message}");
                System.Windows.MessageBox.Show($"Could not build support bundle:\n{ex.Message}",
                    "WhisperInk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        public void DiagnoseActiveProvider()
        {
            Dispatcher.BeginInvoke(async () =>
            {
                string block = await ProviderDiagnostics.BuildAsync(GetActiveProvider());
                var win = new Window
                {
                    Title                 = "Diagnose active provider",
                    Width                 = 640,
                    Height                = 400,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    ShowInTaskbar         = false,
                    Background            = System.Windows.Media.Brushes.Black,
                };
                var tb = new System.Windows.Controls.TextBox
                {
                    Text               = block,
                    IsReadOnly         = true,
                    FontFamily         = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize           = 12,
                    Foreground         = System.Windows.Media.Brushes.White,
                    Background         = System.Windows.Media.Brushes.Black,
                    BorderThickness    = new Thickness(0),
                    VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    AcceptsReturn      = true,
                    TextWrapping       = TextWrapping.NoWrap,
                    Padding            = new Thickness(12),
                };
                win.Content = tb;
                win.ShowDialog();
            });
        }

        public void ShowAboutDialog()
        {
            Dispatcher.BeginInvoke(() =>
            {
                var w = new AboutWindow();
                w.ShowDialog();
            });
        }

        public void QuitApplication()
        {
            _exiting = true;
            Dispatcher.BeginInvoke(() => System.Windows.Application.Current.Shutdown());
        }

        public void SetQuitOnClose(bool enabled)
        {
            if (_quitOnClose == enabled) return;
            _quitOnClose = enabled;
            SaveConfig();
        }

        public void SetLaunchAtStartup(bool enabled)
        {
            if (_launchAtStartup == enabled) return;
            _launchAtStartup = enabled;
            SaveConfig();
            try
            {
                string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                AutoStart.SetEnabled(enabled, exe);
            }
            catch (Exception ex) { Log($"Auto-start registry write failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Wrapper stream that forwards all operations to the inner stream but
    /// ignores Dispose. Needed because WaveFileWriter disposes its underlying
    /// stream on completion — but we need the MemoryStream to stay open long
    /// enough to call ToArray() afterward.
    /// </summary>
    internal sealed class IgnoreDisposeStream : Stream
    {
        private readonly Stream _inner;
        public IgnoreDisposeStream(Stream inner) { _inner = inner; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { /* intentionally do not dispose inner */ }
    }
}