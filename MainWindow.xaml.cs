using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
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

        // Bounded timeout so a stalled SendAsync surfaces as TaskCanceledException
        // (caught + logged by the global handler) instead of pretending to work.
        // Successful ElevenLabs calls have measured at 300–870 ms in normal use.
        private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

        // One transcriber instance per provider, lazily constructed by the
        // factory on first use. Replaces the old fan-out of 12 per-provider
        // fields plus their disposal boilerplate.
        private TranscriberFactory? _transcribers;

        private WaveInEvent? _waveIn;
        private WaveFileWriter? _writer;
        private string _currentFileName = "";

        // ── In-memory WAV capture (Path 1 optimization) ───────────────
        // Captures each audio chunk into an in-memory WaveFileWriter
        // alongside the disk writer. When transcribing via local GGUF
        // server providers, we upload these bytes directly and skip the
        // disk-read round-trip, saving ~20-30ms per dictation.
        private MemoryStream? _memWavStream;
        private WaveFileWriter? _memWavWriter;
        private byte[]? _lastWavBytes;

        private DispatcherTimer _animationTimer = null!;
        private readonly Random _rng = new();

        private enum SoundType { Start, Stop, Success, Error }

        private static readonly string LogFile = Path.Combine(ConfigFolder, "debug.log");

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
                t => Log($"[unhandled] {name}: {t.Exception?.GetBaseException().Message}"),
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

            try { _hook?.Dispose(); } catch { }
            try { _transcribers?.Dispose(); } catch { }
            try { _healthProbe?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Fresh log per session — must happen BEFORE LoadConfig, or the
            // truncation wipes the very startup diagnostics it should keep
            // (provider appends, active-provider line).
            try { File.WriteAllText(LogFile, $"=== WhisperInk started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); } catch { }

            Topmost = true;
            var screen = SystemParameters.WorkArea;
            Left = screen.Width - Width - 10;
            Top = screen.Height - Height - 10;

            // All hotkey decisions live in the service; these callbacks fire
            // on the hook thread and immediately marshal to the UI thread.
            _hook = new KeyboardHookService(
                isRecording: () => IsRecording,
                onDictationStart: target =>
                {
                    _targetWindow = target;
                    Dispatcher.BeginInvoke(() => StartBatchDictation());
                },
                onDictationStop: () =>
                    Dispatcher.BeginInvoke(() => RunSafe(StopBatchDictationAsync, "StopBatchDictation")));
            _hook.Install();

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _animationTimer.Tick += (_, _) => UpdateHistogram();

            LoadConfig();

            // Factory owns one ITranscriber per provider. The GPU-backend
            // delegate lets it pick up live edits from the settings dialog
            // without needing to drop and recreate every CrispASR server.
            _transcribers = new TranscriberFactory(_httpClient, () => _crispGpuBackend, Log);

            UpdateStatusLabel();

            InitializeTrayAndHealth();
            SyncLaunchAtStartupFromRegistry();
            RunFirstRunCheck();
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

            Log($"Active provider: {provider.Name} → STT={provider.ResolvedTranscriptionUrl}  (auth={(string.IsNullOrEmpty(provider.AuthHeaderName) ? "Bearer" : provider.AuthHeaderName)}, modelField={provider.ResolvedModelField})");
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
                        foreach (var def in defaults)
                        {
                            if (!existingIds.Contains(def.Id))
                            {
                                _providers.Add(def);
                                Log($"Added new default provider: {def.Name}");
                            }
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
                    CrispGpuBackend = _crispGpuBackend
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
        // BATCH DICTATION MODE — now with parallel in-memory WAV capture
        // ════════════════════════════════════════════════════════════════

        private void StartBatchDictation()
        {
            var startProvider = GetActiveProvider();
            if (startProvider != null && startProvider.RequiresApiKey && string.IsNullOrWhiteSpace(startProvider.ApiKey))
            {
                lblStatus.Content = "No API key!";
                return;
            }

            if (Interlocked.CompareExchange(ref _recState, 1, 0) != 0) return;
            _hook?.BeginSuppression();
            _lastWavBytes = null;
            _injector.ReleaseAllModifierKeys();

            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
            lblStatus.Content = "🎙 REC";
            lblStatus.Opacity = 1;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRecordings");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            _currentFileName = Path.Combine(folder, "temp_audio.wav");

            _waveIn = new WaveInEvent();
            if (_selectedDeviceNumber < WaveIn.DeviceCount) _waveIn.DeviceNumber = _selectedDeviceNumber;
            else _selectedDeviceNumber = 0;
            _waveIn.WaveFormat = new WaveFormat(16000, 1);

            // Parallel writers: one to disk (for replay / debugging),
            // one to memory (for local GGUF server providers to use directly).
            _writer = new WaveFileWriter(_currentFileName, _waveIn.WaveFormat);
            _memWavStream = new MemoryStream();
            _memWavWriter = new WaveFileWriter(new IgnoreDisposeStream(_memWavStream), _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnBatchAudioDataAvailable;
            _waveIn.StartRecording();

            PlayUiSound(SoundType.Start);
        }

        private void OnBatchAudioDataAvailable(object? sender, WaveInEventArgs a)
        {
            // Write each audio chunk to both sinks in the same callback, so they
            // stay byte-identical. Tiny CPU cost; no I/O on the memory path.
            try { _writer?.Write(a.Buffer, 0, a.BytesRecorded); } catch { }
            try { _memWavWriter?.Write(a.Buffer, 0, a.BytesRecorded); } catch { }
        }

        private async Task StopBatchDictationAsync()
        {
            if (Interlocked.CompareExchange(ref _recState, 2, 1) != 1) return;
            Log("[diag] StopBatchDictation: enter");

            try
            {
                // ── Pipeline timing instrumentation ──
                var swBatch = System.Diagnostics.Stopwatch.StartNew();
                long tPlaySound, tWaveStop, tFlush, tTranscribe, tPaste;

                PlayUiSound(SoundType.Stop);
                tPlaySound = swBatch.ElapsedMilliseconds;

                try { _waveIn?.StopRecording(); } catch { }
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;
                tWaveStop = swBatch.ElapsedMilliseconds;

                // Flush both writers and capture in-memory bytes.
                try { _writer?.Dispose(); } catch { }
                _writer = null;

                try
                {
                    _memWavWriter?.Dispose();
                    if (_memWavStream != null)
                    {
                        _lastWavBytes = _memWavStream.ToArray();
                        _memWavStream.Dispose();
                    }
                }
                catch (Exception ex) { Log($"Memory WAV flush error: {ex.Message}"); _lastWavBytes = null; }
                finally { _memWavWriter = null; _memWavStream = null; }
                tFlush = swBatch.ElapsedMilliseconds;

                lblStatus.Content = "Processing...";
                lblStatus.Opacity = 1;

                Log("[diag] StopBatchDictation: pre-transcribe");
                string? text = await TranscribeAudioAsync(_currentFileName);
                tTranscribe = swBatch.ElapsedMilliseconds;
                Log($"[diag] StopBatchDictation: post-transcribe, text.Length={text?.Length ?? -1}");
                if (!string.IsNullOrEmpty(text))
                {
                    if (_targetWindow != IntPtr.Zero)
                        SetForegroundWindow(_targetWindow);
                    Log("[diag] StopBatchDictation: pre-paste");
                    _injector.PasteTextToActiveWindow(text);
                    tPaste = swBatch.ElapsedMilliseconds;
                    Log("[diag] StopBatchDictation: post-paste, pre-history");
                    HistoryService.Add(text);
                    Log("[diag] StopBatchDictation: post-history");
                    PlayUiSound(SoundType.Success);

                    int charCount = text?.Length ?? 0;
                    Log($"Batch pipeline: sound={tPlaySound}ms  waveStop={tWaveStop - tPlaySound}ms  flush={tFlush - tWaveStop}ms  transcribe={tTranscribe - tFlush}ms  paste={tPaste - tTranscribe}ms  ({charCount} chars)  TOTAL={swBatch.ElapsedMilliseconds}ms");
                }
                else
                {
                    PlayUiSound(SoundType.Error);
                    lblStatus.Content = "Error";
                    await Task.Delay(1500);
                }

                Log("[diag] StopBatchDictation: exit");
            }
            catch (Exception ex)
            {
                Log($"[diag] StopBatchDictation: UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
            finally
            {
                // Release on EVERY exit path. A thrown transcription/paste (e.g.
                // the 15s HTTP timeout, or a network error) used to skip this,
                // stranding Ctrl "down" — the keyboard hook swallows the user's
                // physical key-up, so this synthetic release is the only thing
                // that tells the OS the key came back up. A stuck Ctrl turns the
                // next keystroke into Ctrl+<key> (e.g. Ctrl+O pops an Open dialog).
                _injector.ReleaseAllModifierKeys();
                Volatile.Write(ref _recState, 0);
                ResetUi();
                UpdateStatusLabel();
            }
        }

        // ── Transcription dispatch ──────────────────────────────────────
        //
        // Single entry point for every batch transcription. The factory hands
        // back the right ITranscriber for the active provider (cloud HTTP,
        // auto-spawned CrispASR server, Google Chirp 3, Soniox, Deepgram); we
        // don't care which it is. Adding a new model is now a config-only change â€”
        // no new branches here.

        private async Task<string?> TranscribeAudioAsync(string filePath)
        {
            if (_transcribers == null) return null;

            var provider = GetActiveProvider();
            if (provider == null) { Log("[diag] Transcribe: no active provider"); return null; }

            byte[] wavBytes;
            if (_lastWavBytes is { Length: > 0 })
            {
                wavBytes = _lastWavBytes;
            }
            else if (File.Exists(filePath))
            {
                wavBytes = await File.ReadAllBytesAsync(filePath);
            }
            else
            {
                Log("[diag] Transcribe: no audio bytes and file missing");
                return null;
            }

            ITranscriber transcriber;
            try { transcriber = _transcribers.GetOrCreate(provider); }
            catch (Exception ex) { Log($"Transcriber init failed for {provider.Id}: {ex.Message}"); return null; }

            if (!transcriber.IsReady(out var diag))
            {
                Log($"{transcriber.DisplayName}: {diag}");
                return null;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? result = await transcriber.TranscribeAsync(wavBytes, _contextBiasTerms);
            sw.Stop();

            double audioMs = GetWavDurationMs(wavBytes);
            double rtfx    = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
            string mode    = _lastWavBytes is { Length: > 0 } ? "mem" : "disk";
            string preview = result == null ? "(null)" : result[..Math.Min(200, result.Length)];
            Log($"{transcriber.DisplayName} ({mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}x -- result: {preview}");
            return result;
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

        private void PlayUiSound(SoundType type)
        {
            if (!_isSoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    int sampleRate = 44100;
                    int duration = type switch { SoundType.Start => 30, SoundType.Stop => 30, SoundType.Success => 50, SoundType.Error => 120, _ => 0 };
                    double freq = type switch { SoundType.Start => 1200, SoundType.Stop => 800, SoundType.Success => 1600, SoundType.Error => 300, _ => 0 };

                    int samples = sampleRate * duration / 1000;
                    using var ms = new MemoryStream();
                    using var writer = new BinaryWriter(ms);

                    int dataSize = samples * 2;
                    writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                    writer.Write(36 + dataSize);
                    writer.Write(Encoding.ASCII.GetBytes("WAVE"));
                    writer.Write(Encoding.ASCII.GetBytes("fmt "));
                    writer.Write(16);
                    writer.Write((short)1);
                    writer.Write((short)1);
                    writer.Write(sampleRate);
                    writer.Write(sampleRate * 2);
                    writer.Write((short)2);
                    writer.Write((short)16);
                    writer.Write(Encoding.ASCII.GetBytes("data"));
                    writer.Write(dataSize);

                    for (int i = 0; i < samples; i++)
                    {
                        double t = (double)i / sampleRate;
                        double envelope = Math.Max(0, 1.0 - t / (duration / 1000.0));
                        double sample = Math.Sin(2 * Math.PI * freq * t) * envelope * 0.3;
                        writer.Write((short)(sample * short.MaxValue));
                    }

                    ms.Position = 0;
                    using var player = new System.Media.SoundPlayer(ms);
                    player.PlaySync();
                }
                catch { }
            });
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
                    Action = () => { _selectedDeviceNumber = deviceIndex; SaveConfig(); },
                });
            }
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