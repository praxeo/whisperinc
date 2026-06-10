using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
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
    public partial class MainWindow : Window, TrayIconHost
    {
        // ── Win32 imports ──────────────────────────────────────────────
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hInstance, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        private const uint WM_CHAR = 0x0102;
        private const uint GW_CHILD = 5;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        // ── SendInput structures ───────────────────────────────────────
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_CONTROL = 0x11;
        private const int VK_SPACE = 0x20;
        private const int VK_RETURN = 0x0D;

        private const byte KEYEVENTF_EXTENDEDKEY = 0x01;
        private const byte KEYEVENTF_KEYUP_BYTE = 0x02;

        private const int SYNTHETIC_MARKER_VALUE = 0x5AFE;
        private static readonly UIntPtr SYNTHETIC_MARKER = (UIntPtr)SYNTHETIC_MARKER_VALUE;
        private static readonly IntPtr SYNTHETIC_MARKER_PTR = new IntPtr(SYNTHETIC_MARKER_VALUE);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int vkCode;
            public int scanCode;
            public int flags;
            public int time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        // ── API configuration (derived from active provider) ────────
        private const string RealtimeModel = "voxtral-mini-transcribe-realtime-2602";
        private const string RealtimeWsUrl = "ws://localhost:8765/v1/realtime";

        private string _audioApiUrl = "";
        private string _audioModel = "";
        private string _chatApiUrl = "";
        private string _chatModel = "";
        private string _postProcessModel = "";
        private string _activeApiKey = "";
        private bool _activeSupportsRealtime = false;
        private bool _activeSupportsTranscription = true;
        private string _activeAuthHeaderName = "";
        private string _activeModelFieldName = "model";

        private static readonly string ConfigFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".WhisperInk");
        private static readonly string ConfigFile = Path.Combine(ConfigFolder, "config.json");

        // ── State ──────────────────────────────────────────────────────
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _hookCallback;
        private bool _isRecording;
        private bool _ctrlPressed;
        private bool _winPressed;
        private bool _altPressed;
        private bool _spacePressed;
        private bool _suppressingKeys;

        private string _mistralApiKey = "";
        private bool _isSoundEnabled = true;
        private string _systemPrompt = new AppConfig().SystemPrompt;
        private int _selectedDeviceNumber;
        private int _targetStreamingDelayMs = 480;
        private string _proxyPath = "";

        private string _dictationMode = "Realtime";
        private bool IsRealtimeMode => _dictationMode == "Realtime";

        private List<string> _contextBiasTerms = new();

        private bool _postProcessBatch = false;
        private string _postProcessPrompt = new AppConfig().PostProcessPrompt;

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

        // Saved clipboard contents from before the most recent paste, restored
        // asynchronously after SimulateCtrlV() so dictation doesn't clobber what
        // the user had copied. If a new paste arrives during the restore window,
        // we cancel and reuse the pending data — that way rapid-fire dictation
        // still ends with the *original* clipboard, not the previous transcript.
        private IDataObject? _pendingRestoreData;
        private CancellationTokenSource? _pendingRestoreCts;

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

        // Watches the Mistral realtime proxy subprocess. The proxy can die
        // silently — without this heartbeat, the next dictation surfaces the
        // failure only when the WebSocket connect times out. Tick interval is
        // 5s, low enough to feel "live" without flooding debug.log.
        private DispatcherTimer? _proxyHeartbeat;
        private bool _proxyHealthy = true;

        private enum RecordingMode { Dictation, AnalyzeContext }
        private RecordingMode _currentMode = RecordingMode.Dictation;
        private enum SoundType { Start, Stop, Success, Error }

        private ClientWebSocket? _realtimeWs;
        private CancellationTokenSource? _realtimeCts;
        private Task? _receiveTask;
        private string _accumulatedTranscript = "";
        private bool _leadingSpaceSent;
        private bool _isStopping;
        private readonly SemaphoreSlim _wsSendLock = new(1, 1);
        private Process? _proxyProcess;

        private static readonly string LogFile = Path.Combine(ConfigFolder, "debug.log");

        private static void Log(string msg)
        {
            try { File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
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
            _hookCallback = HookCallback;
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

            if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
            try { _proxyHeartbeat?.Stop(); } catch { }
            try { _proxyProcess?.Kill(); } catch (Exception ex) { Log($"Proxy kill on close failed: {ex.Message}"); }
            try { _transcribers?.Dispose(); } catch { }
            try { _healthProbe?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Topmost = true;
            var screen = SystemParameters.WorkArea;
            Left = screen.Width - Width - 10;
            Top = screen.Height - Height - 10;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, GetModuleHandle(curModule.ModuleName!), 0);

            _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _animationTimer.Tick += (_, _) => UpdateHistogram();

            LoadConfig();

            // Factory owns one ITranscriber per provider. The GPU-backend
            // delegate lets it pick up live edits from the settings dialog
            // without needing to drop and recreate every CrispASR server.
            _transcribers = new TranscriberFactory(_httpClient, () => _crispGpuBackend, Log);

            if (!string.IsNullOrWhiteSpace(_proxyPath) && _activeSupportsRealtime)
            {
                Log($"Starting proxy: {_proxyPath}");
                try
                {
                    _proxyProcess = new Process();

                    if (_proxyPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
                    {
                        _proxyProcess.StartInfo = new ProcessStartInfo
                        {
                            FileName = "py",
                            Arguments = $"\"{_proxyPath}\" --api-key {_activeApiKey}",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardError = true
                        };
                    }
                    else
                    {
                        _proxyProcess.StartInfo = new ProcessStartInfo
                        {
                            FileName = _proxyPath,
                            Arguments = $"--api-key {_activeApiKey}",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardError = true
                        };
                    }

                    _proxyProcess.Start();
                    Log($"Proxy process started (PID {_proxyProcess.Id}), waiting for it to bind...");
                    System.Threading.Thread.Sleep(2500);
                }
                catch (Exception ex) { Log($"Proxy start error: {ex.Message}"); }
            }
            else if (string.IsNullOrWhiteSpace(_proxyPath))
            {
                Log("ProxyPath is empty — proxy will NOT auto-start. Start it manually or set ProxyPath in config.");
            }
            else
            {
                Log($"Active provider ({GetActiveProvider()?.Name}) does not support realtime — proxy not started.");
            }

            try { File.WriteAllText(LogFile, $"=== WhisperInk started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\n"); } catch { }

            UpdateStatusLabel();

            InitializeTrayAndHealth();
            SyncLaunchAtStartupFromRegistry();
            RunFirstRunCheck();

            StartProxyHeartbeat();
        }

        private void StartProxyHeartbeat()
        {
            if (_proxyHeartbeat != null) return;
            _proxyHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _proxyHeartbeat.Tick += (_, _) => CheckProxyHealth();
            _proxyHeartbeat.Start();
        }

        private void CheckProxyHealth()
        {
            // Heartbeat is meaningful only when the active provider needs a
            // proxy. For cloud/local providers without realtime support,
            // we treat the proxy as healthy regardless (it may not even be
            // running) so the status label doesn't show false warnings.
            bool needsProxy = _activeSupportsRealtime
                              && IsRealtimeMode
                              && !string.IsNullOrWhiteSpace(_proxyPath);
            if (!needsProxy)
            {
                if (!_proxyHealthy)
                {
                    _proxyHealthy = true;
                    UpdateStatusLabel();
                }
                return;
            }

            bool alive = false;
            try { alive = _proxyProcess != null && !_proxyProcess.HasExited; }
            catch { alive = false; }

            if (alive == _proxyHealthy) return;
            _proxyHealthy = alive;
            Log(alive ? "Mistral proxy recovered." : "Mistral proxy is no longer running — realtime dictation will fail until it restarts.");
            UpdateStatusLabel();
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

            string baseUrl = provider.BaseUrl.TrimEnd('/');
            _audioApiUrl = provider.ResolvedTranscriptionUrl;
            _chatApiUrl = $"{baseUrl}/v1/chat/completions";
            _audioModel = provider.TranscriptionModel;
            _chatModel = provider.ChatModel;
            _postProcessModel = string.IsNullOrWhiteSpace(provider.PostProcessModel)
                ? provider.ChatModel : provider.PostProcessModel;
            _activeApiKey = provider.ApiKey;
            _activeSupportsRealtime = provider.SupportsRealtime;
            _activeSupportsTranscription = provider.SupportsTranscription;
            _activeAuthHeaderName = provider.AuthHeaderName ?? "";
            _activeModelFieldName = provider.ResolvedModelField;

            Log($"Active provider: {provider.Name} → STT={_audioApiUrl}  (RT={_activeSupportsRealtime}, auth={(_activeAuthHeaderName == "" ? "Bearer" : _activeAuthHeaderName)}, modelField={_activeModelFieldName})");
        }

        private void SwitchProvider(string providerId)
        {
            ApplyProviderSwitch(providerId);
            SaveConfig();

            if (IsRealtimeMode && !_activeSupportsRealtime)
            {
                _dictationMode = "Batch";
                SaveConfig();
            }

            UpdateStatusLabel();
            UpdateLocalModelBanner();
            _healthProbe?.RequestProbe();
        }

        /// <summary>
        /// Core of every active-provider transition — from tray menu, context
        /// menu, or dialog close. Logs the change, flips the id, disposes any
        /// local CrispASR server owned by the previous provider (so we don't
        /// leak ~2 GB of resident model per switch), and re-derives the
        /// cached per-provider state (_audioApiUrl, _activeApiKey, …).
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
            string provTag = provider?.Name ?? "?";
            string modeTag = IsRealtimeMode ? "RT" : "Batch";
            string proxyTag = (_activeSupportsRealtime && IsRealtimeMode && !_proxyHealthy)
                ? "  ⚠ proxy down"
                : "";
            lblStatus.Content = $"{provTag} ({modeTag}){proxyTag}";
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
                    if (root.TryGetProperty("MistralApiKey", out var key)) _mistralApiKey = key.GetString() ?? "";
                    if (root.TryGetProperty("IsSoundEnabled", out var snd)) _isSoundEnabled = snd.GetBoolean();
                    if (root.TryGetProperty("SystemPrompt", out var sp)) _systemPrompt = sp.GetString() ?? _systemPrompt;
                    if (root.TryGetProperty("SelectedDevice", out var dev)) _selectedDeviceNumber = dev.GetInt32();
                    if (root.TryGetProperty("TargetStreamingDelayMs", out var delay)) _targetStreamingDelayMs = delay.GetInt32();
                    if (root.TryGetProperty("ProxyPath", out var pp)) _proxyPath = pp.GetString() ?? "";
                    if (root.TryGetProperty("DictationMode", out var dm))
                    {
                        string mode = dm.GetString() ?? "Realtime";
                        _dictationMode = (mode == "Batch") ? "Batch" : "Realtime";
                    }
                    if (root.TryGetProperty("ContextBiasTerms", out var cbt) && cbt.ValueKind == JsonValueKind.Array)
                    {
                        _contextBiasTerms = new List<string>();
                        foreach (var term in cbt.EnumerateArray())
                        {
                            var s = term.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) _contextBiasTerms.Add(s);
                        }
                    }
                    if (root.TryGetProperty("PostProcessBatch", out var ppb)) _postProcessBatch = ppb.GetBoolean();
                    if (root.TryGetProperty("PostProcessPrompt", out var ppp)) _postProcessPrompt = ppp.GetString() ?? _postProcessPrompt;

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
                            if (pEl.TryGetProperty("ChatModel", out var cm)) p.ChatModel = cm.GetString() ?? "";
                            if (pEl.TryGetProperty("PostProcessModel", out var ppm)) p.PostProcessModel = ppm.GetString() ?? "";
                            if (pEl.TryGetProperty("SupportsRealtime", out var sr)) p.SupportsRealtime = sr.GetBoolean();
                            if (pEl.TryGetProperty("SupportsTranscription", out var st)) p.SupportsTranscription = st.GetBoolean();
                            if (pEl.TryGetProperty("TranscriptionEndpoint", out var te)) p.TranscriptionEndpoint = te.GetString() ?? "";
                            if (pEl.TryGetProperty("AuthHeaderName", out var ahn)) p.AuthHeaderName = ahn.GetString() ?? "";
                            if (pEl.TryGetProperty("ModelFieldName", out var mfn)) p.ModelFieldName = mfn.GetString() ?? "";
                            if (pEl.TryGetProperty("TranscriptionTemperature", out var tt) && tt.ValueKind != JsonValueKind.Null)
                                p.TranscriptionTemperature = tt.GetDouble();
                            if (pEl.TryGetProperty("ContextBiasMode", out var cbm))
                                p.ContextBiasMode = cbm.GetString() ?? "none";
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
                        if (!string.IsNullOrWhiteSpace(_mistralApiKey))
                        {
                            var mistral = _providers.FirstOrDefault(p => p.Id == "mistral");
                            if (mistral != null) mistral.ApiKey = _mistralApiKey;
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

                var mistralProvider = _providers.FirstOrDefault(p => p.Id == "mistral");
                string legacyKey = mistralProvider?.ApiKey ?? _mistralApiKey;

                var config = new
                {
                    MistralApiKey = legacyKey,
                    IsSoundEnabled = _isSoundEnabled,
                    SystemPrompt = _systemPrompt,
                    SelectedDevice = _selectedDeviceNumber,
                    TargetStreamingDelayMs = _targetStreamingDelayMs,
                    ProxyPath = _proxyPath,
                    DictationMode = _dictationMode,
                    ContextBiasTerms = _contextBiasTerms,
                    PostProcessBatch = _postProcessBatch,
                    PostProcessPrompt = _postProcessPrompt,
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

        // ── Keyboard Hook ──────────────────────────────────────────────
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = hookData.vkCode;
                bool isDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
                bool isUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);
                bool isSynthetic = (hookData.dwExtraInfo == new IntPtr(SYNTHETIC_MARKER_VALUE));

                if (_suppressingKeys && !isSynthetic)
                {
                    if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL || vkCode == VK_SPACE)
                    {
                        if (vkCode == VK_SPACE) _spacePressed = isDown;
                        else _ctrlPressed = isDown;

                        if (isUp && _isRecording && _currentMode == RecordingMode.Dictation)
                        {
                            if (!_ctrlPressed || !_spacePressed)
                            {
                                if (IsRealtimeMode)
                                    Dispatcher.BeginInvoke(() => StopRealtimeStreaming());
                                else
                                    Dispatcher.BeginInvoke(() => StopBatchDictation());
                            }
                        }
                        if (!_ctrlPressed && !_spacePressed)
                            _suppressingKeys = false;

                        return (IntPtr)1;
                    }
                }

                if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                {
                    if (!isSynthetic) _ctrlPressed = isDown;
                    if (isUp && _isRecording && _currentMode == RecordingMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            Dispatcher.BeginInvoke(() => StopBatchRecording());
                    }
                }
                else if (vkCode == VK_SPACE)
                {
                    if (!isSynthetic) _spacePressed = isDown;
                    if (isDown && !isSynthetic && _ctrlPressed && !_isRecording && !_suppressingKeys)
                    {
                        _targetWindow = GetForegroundWindow();
                        _currentMode = RecordingMode.Dictation;
                        _suppressingKeys = true;

                        if (IsRealtimeMode)
                            Dispatcher.BeginInvoke(() => StartRealtimeStreaming());
                        else
                            Dispatcher.BeginInvoke(() => StartBatchDictation());

                        return (IntPtr)1;
                    }
                }
                else if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                {
                    _winPressed = isDown;
                }
                else if (vkCode == VK_LMENU || vkCode == VK_RMENU)
                {
                    _altPressed = isDown;
                    if (isDown && _ctrlPressed && !_isRecording)
                    {
                        _currentMode = RecordingMode.AnalyzeContext;
                        Dispatcher.BeginInvoke(() => StartBatchRecording());
                    }
                    if (isUp && _isRecording && _currentMode == RecordingMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            Dispatcher.BeginInvoke(() => StopBatchRecording());
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        // ════════════════════════════════════════════════════════════════
        // MISTRAL REALTIME STREAMING MODE
        // ════════════════════════════════════════════════════════════════

        private async void StartRealtimeStreaming()
        {
            if (_isRecording) return;
            if (string.IsNullOrEmpty(_activeApiKey))
            {
                lblStatus.Content = "No API key!";
                return;
            }
            if (!_activeSupportsRealtime)
            {
                lblStatus.Content = "Provider: no RT!";
                return;
            }

            _isRecording = true;
            _accumulatedTranscript = "";
            _leadingSpaceSent = false;
            _suppressingKeys = true;
            ReleaseAllModifierKeys();

            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 255, 100));
            lblStatus.Content = "🎙 LIVE";
            lblStatus.Opacity = 1;
            HistogramPanel.Visibility = Visibility.Visible;
            _animationTimer.Start();

            _realtimeCts = new CancellationTokenSource();

            try
            {
                _realtimeWs = new ClientWebSocket();
                _realtimeWs.Options.SetRequestHeader("Authorization", $"Bearer {_activeApiKey}");
                Log($"Connecting to Mistral Realtime {RealtimeWsUrl}...");

                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_realtimeCts.Token, connectTimeout.Token);

                try
                {
                    await _realtimeWs.ConnectAsync(new Uri(RealtimeWsUrl), linked.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException || ex is System.Net.WebSockets.WebSocketException || ex is System.Net.Http.HttpRequestException)
                {
                    string hint = string.IsNullOrWhiteSpace(_proxyPath)
                        ? "Proxy not configured — set ProxyPath in config or start proxy manually"
                        : "Proxy may not be running — check debug.log";
                    Log($"WS connect failed: {ex.GetType().Name}: {ex.Message} — {hint}");
                    ForceCleanupRealtime($"No proxy! Start it first");
                    return;
                }

                var sessionUpdate = JsonSerializer.Serialize(new
                {
                    type = "session.update",
                    session = new
                    {
                        model = RealtimeModel,
                        target_streaming_delay_ms = _targetStreamingDelayMs,
                        audio_format = new
                        {
                            encoding = "pcm_s16le",
                            sample_rate = 16000
                        }
                    }
                });
                await SendTextMessageSafe(sessionUpdate, _realtimeCts.Token);

                _waveIn = new WaveInEvent();
                if (_selectedDeviceNumber < WaveIn.DeviceCount) _waveIn.DeviceNumber = _selectedDeviceNumber;
                else _selectedDeviceNumber = 0;

                _waveIn.WaveFormat = new WaveFormat(16000, 16, 1);
                _waveIn.BufferMilliseconds = 100;
                _waveIn.DataAvailable += OnAudioDataAvailable;
                _waveIn.StartRecording();

                PlayUiSound(SoundType.Start);
                _receiveTask = Task.Run(() => ReceiveTranscriptionLoop(_realtimeWs, _realtimeCts.Token));
            }
            catch (Exception ex)
            {
                Log($"[diag] StartRealtimeStreaming: ERROR {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                ForceCleanupRealtime($"Error: {ex.Message}");
            }
        }

        private async void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            var cts = _realtimeCts;
            if (!_isRecording || _realtimeWs?.State != WebSocketState.Open || cts == null || cts.IsCancellationRequested)
                return;

            try
            {
                string base64Audio = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                var msg = JsonSerializer.Serialize(new
                {
                    type = "input_audio_buffer.append",
                    audio = base64Audio
                });
                await SendTextMessageSafe(msg, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log($"[diag] OnAudioDataAvailable: ERROR {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); }
        }

        private async Task ReceiveTranscriptionLoop(ClientWebSocket ws, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var messageBuilder = new StringBuilder();

            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            if (_isStopping || result.CloseStatus == WebSocketCloseStatus.NormalClosure) return;
                            var desc = result.CloseStatusDescription ?? result.CloseStatus?.ToString() ?? "unknown";
                            Dispatcher.Invoke(() => ForceCleanupRealtime($"WS closed: {desc}"));
                            return;
                        }
                        messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    ProcessRealtimeEvent(messageBuilder.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => ForceCleanupRealtime($"WS error: {ex.Message}"));
            }
        }

        private void ProcessRealtimeEvent(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl)) return;
                string eventType = typeEl.GetString() ?? "";

                switch (eventType)
                {
                    case "transcription.text.delta":
                        if (!_isRecording) break;
                        if (root.TryGetProperty("text", out var textEl))
                        {
                            string delta = textEl.GetString() ?? "";
                            if (!string.IsNullOrEmpty(delta))
                            {
                                Log($"delta: \"{delta}\"");
                                _accumulatedTranscript += delta;

                                Dispatcher.Invoke(() =>
                                {
                                    Log($"Typing to hwnd: {_targetWindow}");
                                    if (_targetWindow != IntPtr.Zero)
                                        SetForegroundWindow(_targetWindow);
                                    if (!_leadingSpaceSent)
                                    {
                                        TypeTextViaInput(" ");
                                        _leadingSpaceSent = true;
                                    }
                                    TypeTextViaInput(delta);
                                });
                            }
                        }
                        break;

                    case "transcription.done":
                        Log($"Transcription done: {_accumulatedTranscript}");
                        break;

                    case "error":
                        if (root.TryGetProperty("error", out var errEl))
                        {
                            string errorMsg = errEl.ToString();
                            if (errEl.ValueKind == JsonValueKind.Object && errEl.TryGetProperty("message", out var msgEl))
                                errorMsg = msgEl.GetString() ?? errorMsg;
                            Dispatcher.Invoke(() => ForceCleanupRealtime($"Error: {errorMsg}"));
                        }
                        break;
                }
            }
            catch (Exception ex) { Log($"Parse error: {ex.Message}"); }
        }

        private async void StopRealtimeStreaming()
        {
            if (!_isRecording || _isStopping) return;
            _isStopping = true;
            Log("[diag] StopRealtimeStreaming: enter");

            try
            {
                PlayUiSound(SoundType.Stop);

                try { _waveIn?.StopRecording(); } catch { }
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;

                try
                {
                    if (_realtimeWs?.State == WebSocketState.Open && _realtimeCts != null && !_realtimeCts.IsCancellationRequested)
                    {
                        var commit = JsonSerializer.Serialize(new { type = "input_audio_buffer.commit" });
                        await SendTextMessageSafe(commit, _realtimeCts.Token);
                        await Task.Delay(500);
                    }
                }
                catch (Exception ex) { Log($"Realtime commit send failed: {ex.Message}"); }

                _isRecording = false;

                if (_realtimeWs != null && _realtimeWs.State == WebSocketState.Open)
                {
                    try { await _realtimeWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Finished", CancellationToken.None); }
                    catch { }
                }

                try { _realtimeCts?.Cancel(); } catch { }
                try { _realtimeWs?.Dispose(); } catch { }
                _realtimeWs = null;

                if (_receiveTask != null)
                {
                    try { await Task.WhenAny(_receiveTask, Task.Delay(1000)); } catch { }
                    _receiveTask = null;
                }

                if (!string.IsNullOrWhiteSpace(_accumulatedTranscript))
                {
                    HistoryService.Add(_accumulatedTranscript.Trim());
                    PlayUiSound(SoundType.Success);
                }

                Log("[diag] StopRealtimeStreaming: exit");
            }
            catch (Exception ex)
            {
                Log($"[diag] StopRealtimeStreaming: UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
            finally
            {
                // Always run cleanup — see StopBatchDictation. Clearing
                // _isStopping here too prevents a thrown stop from wedging the
                // state machine so that the next Ctrl+Space could never start.
                ReleaseAllModifierKeys();
                _isStopping = false;
                ResetUi();
                UpdateStatusLabel();
            }
        }

        private void ForceCleanupRealtime(string statusMessage)
        {
            Log($"ForceCleanupRealtime: {statusMessage}");

            if (!_isRecording && _realtimeWs == null && !_isStopping) return;
            _isRecording = false;
            _isStopping = false;

            try { _waveIn?.StopRecording(); _waveIn?.Dispose(); } catch { }
            _waveIn = null;

            try { _realtimeCts?.Cancel(); _realtimeWs?.Dispose(); } catch { }
            _realtimeWs = null;

            ReleaseAllModifierKeys();
            ResetUi();
            lblStatus.Content = statusMessage;
            lblStatus.Opacity = 1;

            PlayUiSound(SoundType.Error);
        }

        // ════════════════════════════════════════════════════════════════
        // BATCH DICTATION MODE — now with parallel in-memory WAV capture
        // ════════════════════════════════════════════════════════════════

        private void StartBatchDictation()
        {
            if (_isRecording) return;
            if (string.IsNullOrEmpty(_activeApiKey) && !GetActiveProvider()!.BaseUrl.Contains("localhost") && !IsLocalProvider)
            {
                lblStatus.Content = "No API key!";
                return;
            }

            _isRecording = true;
            _suppressingKeys = true;
            _lastWavBytes = null;
            ReleaseAllModifierKeys();

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

            // Parallel writers: one to disk (for AnalyzeContext replay / debugging),
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

        private async void StopBatchDictation()
        {
            if (!_isRecording) return;
            _isRecording = false;
            Log("[diag] StopBatchDictation: enter");

            try
            {
                // ── Pipeline timing instrumentation ──
                var swBatch = System.Diagnostics.Stopwatch.StartNew();
                long tPlaySound, tWaveStop, tFlush, tTranscribe, tPostProc, tPaste;

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
                    if (_postProcessBatch)
                    {
                        lblStatus.Content = "Correcting...";
                        text = await PostProcessTranscription(text) ?? text;
                    }
                    tPostProc = swBatch.ElapsedMilliseconds;

                    if (_targetWindow != IntPtr.Zero)
                        SetForegroundWindow(_targetWindow);
                    Log("[diag] StopBatchDictation: pre-paste");
                    PasteTextToActiveWindow(text);
                    tPaste = swBatch.ElapsedMilliseconds;
                    Log("[diag] StopBatchDictation: post-paste, pre-history");
                    HistoryService.Add(text);
                    Log("[diag] StopBatchDictation: post-history");
                    PlayUiSound(SoundType.Success);

                    int charCount = text?.Length ?? 0;
                    Log($"Batch pipeline: sound={tPlaySound}ms  waveStop={tWaveStop - tPlaySound}ms  flush={tFlush - tWaveStop}ms  transcribe={tTranscribe - tFlush}ms  postproc={tPostProc - tTranscribe}ms  paste={tPaste - tPostProc}ms  ({charCount} chars)  TOTAL={swBatch.ElapsedMilliseconds}ms");
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
                ReleaseAllModifierKeys();
                ResetUi();
                UpdateStatusLabel();
            }
        }

        // ════════════════════════════════════════════════════════════════
        // BATCH RECORDING MODE (Ctrl+Alt = AnalyzeContext)
        // ════════════════════════════════════════════════════════════════

        public void StartBatchRecording()
        {
            if (string.IsNullOrEmpty(_activeApiKey) && !GetActiveProvider()!.BaseUrl.Contains("localhost") && !IsLocalProvider)
            {
                lblStatus.Content = "No API key!";
                return;
            }

            _isRecording = true;
            _lastWavBytes = null;

            try { Clipboard.Clear(); } catch { }
            MainBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 100, 255));

            lblStatus.Content = "🎙 AI";
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

            _writer = new WaveFileWriter(_currentFileName, _waveIn.WaveFormat);
            _memWavStream = new MemoryStream();
            _memWavWriter = new WaveFileWriter(new IgnoreDisposeStream(_memWavStream), _waveIn.WaveFormat);

            _waveIn.DataAvailable += OnBatchAudioDataAvailable;
            _waveIn.StartRecording();

            PlayUiSound(SoundType.Start);
        }

        private async void StopBatchRecording()
        {
            if (!_isRecording) return;
            _isRecording = false;
            Log("[diag] StopBatchRecording: enter");

            try
            {
                PlayUiSound(SoundType.Stop);

                try { _waveIn?.StopRecording(); } catch { }
                try { _waveIn?.Dispose(); } catch { }
                _waveIn = null;

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

                lblStatus.Content = "Processing...";
                lblStatus.Opacity = 1;

                string selectedText = GetSelectedText();
                Log("[diag] StopBatchRecording: pre-transcribe");
                string? transcribedVoice = await TranscribeAudioAsync(_currentFileName);
                Log($"[diag] StopBatchRecording: post-transcribe, len={transcribedVoice?.Length ?? -1}");

                if (!string.IsNullOrEmpty(transcribedVoice))
                {
                    lblStatus.Content = "AI...";
                    string? aiResponse = await ProcessAiQueryAsync(selectedText, transcribedVoice);
                    Log($"[diag] StopBatchRecording: post-AI, len={aiResponse?.Length ?? -1}");
                    if (!string.IsNullOrEmpty(aiResponse))
                    {
                        Log("[diag] StopBatchRecording: pre-paste");
                        PasteTextToActiveWindow(aiResponse);
                        Log("[diag] StopBatchRecording: post-paste, pre-history");
                        HistoryService.Add(aiResponse);
                        Log("[diag] StopBatchRecording: post-history");
                        PlayUiSound(SoundType.Success);
                    }
                    else { PlayUiSound(SoundType.Error); lblStatus.Content = "AI error"; }
                }
                else { PlayUiSound(SoundType.Error); lblStatus.Content = "Transcribe error"; }

                Log("[diag] StopBatchRecording: exit");
            }
            catch (Exception ex)
            {
                Log($"[diag] StopBatchRecording: UNHANDLED {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
            finally
            {
                // Always release — see StopBatchDictation.
                ReleaseAllModifierKeys();
                ResetUi();
                UpdateStatusLabel();
            }
        }

        // â”€â”€ Transcription dispatch â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //
        // Single entry point for every batch transcription. The factory hands
        // back the right ITranscriber for the active provider (cloud HTTP,
        // local ONNX, auto-spawned CrispASR server, Google Chirp 3); we don't
        // care which it is. Adding a new model is now a config-only change â€”
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

        private async Task<string?> ProcessAiQueryAsync(string context, string voiceInstruction)
        {
            try
            {
                string userContent = string.IsNullOrEmpty(context)
                    ? voiceInstruction
                    : $"Context:\n{context}\n\nInstruction: {voiceInstruction}";

                var payload = new
                {
                    model = _chatModel,
                    messages = new[] {
                        new { role = "system", content = _systemPrompt },
                        new { role = "user", content = userContent }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, _chatApiUrl);
                if (!string.IsNullOrEmpty(_activeApiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _activeApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(responseString);
                return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            }
            catch (Exception ex) { Log($"AI error: {ex.Message}"); return null; }
        }

        private async Task<string?> PostProcessTranscription(string rawText)
        {
            try
            {
                Log("Post-processing batch transcription...");
                var messages = new[] {
                    new { role = "user", content = $"{_postProcessPrompt}\n\nINPUT:\n{rawText}\n\nOUTPUT:\n" }
                };
                var payload = new
                {
                    model = _postProcessModel,
                    messages,
                    temperature = 0.0
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, _chatApiUrl);
                if (!string.IsNullOrEmpty(_activeApiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _activeApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Log($"Post-process failed ({response.StatusCode}), using raw transcription");
                    return rawText;
                }
                using var doc = JsonDocument.Parse(responseString);
                string? corrected = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

                if (!string.IsNullOrEmpty(corrected))
                {
                    if (corrected.StartsWith("OUTPUT:", StringComparison.OrdinalIgnoreCase))
                        corrected = corrected.Substring(7);
                    corrected = corrected.Replace("**", "").Replace("*", "").Replace("```", "");
                    corrected = corrected.Trim();
                    int blankLine = corrected.IndexOf("\n\n");
                    if (blankLine > 0) corrected = corrected.Substring(0, blankLine).Trim();

                    if (corrected.StartsWith("(") ||
                        corrected.Contains("no correction", StringComparison.OrdinalIgnoreCase) ||
                        corrected.Contains("not clinical", StringComparison.OrdinalIgnoreCase) ||
                        corrected.Contains("no speech recognition", StringComparison.OrdinalIgnoreCase) ||
                        corrected.Contains("cannot", StringComparison.OrdinalIgnoreCase) ||
                        corrected.Contains("I'm sorry", StringComparison.OrdinalIgnoreCase) ||
                        corrected.Contains("no errors", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("Post-process returned commentary, using raw transcription");
                        return rawText;
                    }
                }

                Log($"Post-process done: {corrected?.Length ?? 0} chars");
                return string.IsNullOrWhiteSpace(corrected) ? rawText : corrected;
            }
            catch (Exception ex)
            {
                Log($"Post-process error: {ex.Message}, using raw transcription");
                return rawText;
            }
        }

        // ── Text input helpers ──────────────────────────────────────────

        private void TypeTextViaInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (_targetWindow == IntPtr.Zero) return;

            foreach (char c in text)
            {
                PostMessage(_targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        private string GetSelectedText()
        {
            try
            {
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, 0, UIntPtr.Zero);
                keybd_event(0x43, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);

                Thread.Sleep(100);
                string text = "";
                var staThread = new Thread(() => { try { text = Clipboard.GetText(); } catch { } });
                staThread.SetApartmentState(ApartmentState.STA);
                staThread.Start();
                staThread.Join();
                return text;
            }
            catch (Exception ex)
            {
                Log($"GetSelectedText failed: {ex.Message}");
                return "";
            }
        }

        private void PasteTextToActiveWindow(string text)
        {
            text = " " + text;
            Log($"[diag] Paste: enter, len={text.Length}");

            // If a previous paste's restore is still pending, its saved data IS the
            // real prior clipboard (the current clipboard holds the previous transcript).
            // Reuse it so chained dictations still restore the user's original copy.
            IDataObject? savedClipboard = _pendingRestoreData;
            try { _pendingRestoreCts?.Cancel(); } catch { }
            _pendingRestoreCts = null;
            _pendingRestoreData = null;

            Exception? clipEx = null;
            var staThread = new Thread(() =>
            {
                if (savedClipboard == null)
                {
                    try { savedClipboard = CloneClipboardData(); } catch { }
                }
                try { Clipboard.SetText(text); }
                catch (Exception ex) { clipEx = ex; }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            Log("[diag] Paste: STA started, joining");
            staThread.Join();
            Log(clipEx == null
                ? "[diag] Paste: STA joined OK, calling SimulateCtrlV"
                : $"[diag] Paste: STA joined with SetText error {clipEx.GetType().Name}: {clipEx.Message}");
            SimulateCtrlV();

            if (clipEx == null && savedClipboard != null)
            {
                var cts = new CancellationTokenSource();
                _pendingRestoreCts = cts;
                _pendingRestoreData = savedClipboard;
                _ = RestoreClipboardAfterDelay(savedClipboard, cts);
            }
            Log("[diag] Paste: exit");
        }

        // Snapshot every format on the clipboard into a new DataObject. Skips
        // formats that throw on read (delayed-render, COM-marshalled handles)
        // so a single bad format doesn't lose the rest. Must run on STA thread.
        private static IDataObject? CloneClipboardData()
        {
            var src = Clipboard.GetDataObject();
            if (src == null) return null;
            var clone = new DataObject();
            bool any = false;
            foreach (var fmt in src.GetFormats(autoConvert: false))
            {
                try
                {
                    var data = src.GetData(fmt, autoConvert: false);
                    if (data != null) { clone.SetData(fmt, data); any = true; }
                }
                catch { }
            }
            return any ? clone : null;
        }

        // Wait long enough for the target app to consume the simulated Ctrl+V,
        // then restore the saved clipboard. Cancellable so a fresh paste can
        // pre-empt this one without racing on Clipboard.SetDataObject.
        private async Task RestoreClipboardAfterDelay(IDataObject data, CancellationTokenSource cts)
        {
            try { await Task.Delay(250, cts.Token); }
            catch (OperationCanceledException) { return; }

            var t = new Thread(() =>
            {
                try { Clipboard.SetDataObject(data, copy: true); }
                catch (Exception ex) { Log($"Clipboard restore failed: {ex.Message}"); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (ReferenceEquals(_pendingRestoreCts, cts))
            {
                _pendingRestoreCts = null;
                _pendingRestoreData = null;
                Log("[diag] Paste: clipboard restored");
            }
        }

        private void SimulateCtrlV()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
        }

        private async Task SendTextMessageSafe(string message, CancellationToken ct)
        {
            if (_realtimeWs == null || _realtimeWs.State != WebSocketState.Open) return;

            await _wsSendLock.WaitAsync(ct);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _realtimeWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch (Exception ex) { Log($"WS send failed: {ex.Message}"); }
            finally { _wsSendLock.Release(); }
        }

        private void ReleaseAllModifierKeys()
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event(0xA0, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event(0xA1, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_LMENU, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_RMENU, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_LWIN, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
            keybd_event((byte)VK_RWIN, 0, KEYEVENTF_KEYUP_BYTE, SYNTHETIC_MARKER);
        }

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
                    double target = _isRecording ? _rng.Next(4, 22) : 2;
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

        // ── Tray context menu ──────────────────────────────────────────
        // Sequenced top-level builder: every submenu lives in its own
        // helper so each piece reads as a unit. Read top-to-bottom; the
        // order here is the order the user sees.

        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            menu.Items.Add(BuildProviderMenu());
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildModeMenu());
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildMicMenu());
            menu.Items.Add(BuildSoundToggle());
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildDelayMenu());
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildPromptItem());
            menu.Items.Add(BuildBiasItem());
            menu.Items.Add(BuildHistoryItem());
            menu.Items.Add(new Separator());
            menu.Items.Add(BuildConfigItem());
            menu.Items.Add(BuildPostProcessToggle());
            menu.Items.Add(BuildHideItem());
            menu.Items.Add(BuildExitItem());
            menu.IsOpen = true;
        }

        private static MenuItem MakeItem(string header, Action onClick, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, IsChecked = isChecked };
            item.Click += (_, _) => onClick();
            return item;
        }

        private MenuItem BuildProviderMenu()
        {
            var providerMenu = new MenuItem { Header = $"🔌 Provider: {GetActiveProvider()?.Name ?? "?"}" };
            foreach (var provider in _providers)
            {
                string pid = provider.Id;
                providerMenu.Items.Add(MakeItem(provider.Name, () => SwitchProvider(pid), isChecked: pid == _activeProviderId));
            }
            providerMenu.Items.Add(new Separator());
            providerMenu.Items.Add(MakeItem("⚙ Configure Providers...", OpenProviderSettingsDialog));
            return providerMenu;
        }

        private MenuItem BuildModeMenu()
        {
            var modeMenu = new MenuItem { Header = $"⚡ Mode: {_dictationMode}" };
            modeMenu.Items.Add(MakeItem("Realtime (live typing)", () =>
            {
                if (!_activeSupportsRealtime)
                {
                    MessageBox.Show("Current provider does not support Mistral Realtime.\nSwitch to Mistral or use Batch mode.",
                        "Realtime Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _dictationMode = "Realtime"; SaveConfig(); UpdateStatusLabel();
            }, isChecked: IsRealtimeMode));

            modeMenu.Items.Add(MakeItem("Batch (record → paste)", () =>
            {
                _dictationMode = "Batch"; SaveConfig(); UpdateStatusLabel();
            }, isChecked: !IsRealtimeMode));
            return modeMenu;
        }

        private MenuItem BuildMicMenu()
        {
            var micMenu = new MenuItem { Header = "🎙 Microphone" };
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                int deviceIndex = i; // capture for the closure
                var cap = WaveIn.GetCapabilities(i);
                micMenu.Items.Add(MakeItem(cap.ProductName, () =>
                {
                    _selectedDeviceNumber = deviceIndex; SaveConfig();
                }, isChecked: i == _selectedDeviceNumber));
            }
            return micMenu;
        }

        private MenuItem BuildSoundToggle()
            => MakeItem(_isSoundEnabled ? "🔊 Sound: ON" : "🔇 Sound: OFF",
                () => { _isSoundEnabled = !_isSoundEnabled; SaveConfig(); });

        private MenuItem BuildDelayMenu()
        {
            var delayMenu = new MenuItem { Header = "⏱ Streaming Delay" };
            foreach (int ms in new[] { 240, 480, 1000, 1500, 2400 })
            {
                int delayMs = ms;
                string label = ms switch
                {
                    240  => "240ms (fastest)",
                    480  => "480ms (recommended)",
                    1000 => "1000ms",
                    1500 => "1500ms",
                    2400 => "2400ms (most accurate)",
                    _    => $"{ms}ms"
                };
                delayMenu.Items.Add(MakeItem(label, () =>
                {
                    _targetStreamingDelayMs = delayMs; SaveConfig();
                }, isChecked: _targetStreamingDelayMs == ms));
            }
            return delayMenu;
        }

        private MenuItem BuildPromptItem()
            => MakeItem("📝 System Prompt", () =>
            {
                var pw = new PromptWindow(_systemPrompt);
                if (pw.ShowDialog() == true) { _systemPrompt = pw.PromptText; SaveConfig(); }
            });

        private MenuItem BuildBiasItem()
            => MakeItem("🎯 Context Bias Terms", () =>
            {
                var biasWindow = new ContextBiasWindow(_contextBiasTerms);
                if (biasWindow.ShowDialog() == true)
                {
                    _contextBiasTerms = biasWindow.BiasTerms;
                    SaveConfig();
                }
            });

        private MenuItem BuildHistoryItem()
            => MakeItem("📋 History", () => new HistoryWindow().Show());

        private MenuItem BuildConfigItem()
            => MakeItem($"📂 Config: {ConfigFile}",
                () => Process.Start("explorer.exe", $"/select,\"{ConfigFile}\""));

        private MenuItem BuildPostProcessToggle()
            => MakeItem(_postProcessBatch ? "🩺 Med Correction: ON" : "🩺 Med Correction: OFF", () =>
            {
                _postProcessBatch = !_postProcessBatch;
                SaveConfig();
                UpdateStatusLabel();
            });

        private MenuItem BuildHideItem()
            => MakeItem("⬇ Hide to tray", HideToTray);

        private MenuItem BuildExitItem()
            => MakeItem("❌ Quit", QuitApplication);

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
                _tray = new TrayIconManager(this);
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
                _tray?.NotifyHealth(r);
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

        // ── TrayIconHost ────────────────────────────────────────────────

        public string ActiveProviderName => GetActiveProvider()?.Name ?? "?";
        public string DebugLogPath       => LogFile;
        // Explicit impl — would otherwise collide with the existing private
        // static field also named ConfigFolder.
        string TrayIconHost.ConfigFolder => MainWindow.ConfigFolder;
        string TrayIconHost.ModelFolder  => Path.Combine(MainWindow.ConfigFolder, "cohere-gguf");
        public HealthReport CurrentHealth => _lastHealth;
        public bool QuitOnClose          => _quitOnClose;
        public bool LaunchAtStartup      => _launchAtStartup;

        public string CrispGpuBackend    => _crispGpuBackend;
        public string DetectedGpuSummary => CrispGpuProbe.Summary;

        public void SetCrispGpuBackend(string value)
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