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
    public partial class MainWindow : Window
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

        private IntPtr _targetWindow = IntPtr.Zero;

        private readonly HttpClient _httpClient = new();
        private CohereOnnxTranscriber? _cohereOnnx;
        private CohereGgufTranscriber? _cohereGguf;
        private CohereGgufServerTranscriber? _cohereGgufServer;
        private CohereGgufCudaServerTranscriber? _cohereGgufCudaServer;
        private CohereGgufCudaQ8ServerTranscriber? _cohereGgufCudaQ8Server;
        private CrispAsrServerTranscriber? _parakeetServer;
        private CrispAsrServerTranscriber? _cohereQ4Server;
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
            Loaded += MainWindow_Loaded;
            Closing += (_, _) =>
            {
                if (_hookId != IntPtr.Zero) UnhookWindowsHookEx(_hookId);
                try { _proxyProcess?.Kill(); } catch { }
                try { _cohereOnnx?.Dispose(); } catch { }
                try { _cohereGguf?.Dispose(); } catch { }
                try { _cohereGgufServer?.Dispose(); } catch { }
                try { _cohereGgufCudaServer?.Dispose(); } catch { }
                try { _cohereGgufCudaQ8Server?.Dispose(); } catch { }
                try { _parakeetServer?.Dispose(); } catch { }
                try { _cohereQ4Server?.Dispose(); } catch { }
            };
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
            _activeProviderId = providerId;
            ApplyActiveProvider();
            SaveConfig();

            if (IsRealtimeMode && !_activeSupportsRealtime)
            {
                _dictationMode = "Batch";
                SaveConfig();
            }

            UpdateStatusLabel();
        }

        private void UpdateStatusLabel()
        {
            var provider = GetActiveProvider();
            string provTag = provider?.Name ?? "?";
            string modeTag = IsRealtimeMode ? "RT" : "Batch";
            lblStatus.Content = $"{provTag} ({modeTag})";
        }

        private bool IsLocalOnnxProvider =>
            GetActiveProvider()?.Id == "cohere-onnx";

        private bool IsLocalGgufProvider =>
            GetActiveProvider()?.Id == "cohere-gguf";

        private bool IsLocalGgufServerProvider =>
            GetActiveProvider()?.Id == "cohere-gguf-server";

        private bool IsLocalGgufCudaServerProvider =>
            GetActiveProvider()?.Id == "cohere-gguf-cuda-server";

        private bool IsLocalGgufCudaQ8ServerProvider =>
            GetActiveProvider()?.Id == "cohere-gguf-cuda-server-q8";

        private bool IsParakeetLocalProvider =>
            GetActiveProvider()?.Id == "parakeet-local";

        private bool IsCohereLocalQ4Provider =>
            GetActiveProvider()?.Id == "cohere-local-q4";

        private bool IsLocalProvider =>
            IsLocalOnnxProvider || IsLocalGgufProvider || IsLocalGgufServerProvider || IsLocalGgufCudaServerProvider || IsLocalGgufCudaQ8ServerProvider || IsParakeetLocalProvider || IsCohereLocalQ4Provider;

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
                            _providers.Add(p);
                        }
                    }
                    if (root.TryGetProperty("ActiveProviderId", out var apid))
                        _activeProviderId = apid.GetString() ?? "mistral";

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
                        var existingIds = new HashSet<string>(_providers.Select(p => p.Id));
                        foreach (var def in ApiProvider.CreateDefaults())
                        {
                            if (!existingIds.Contains(def.Id))
                            {
                                _providers.Add(def);
                                Log($"Added new default provider: {def.Name}");
                            }
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
                    ActiveProviderId = _activeProviderId
                };
                File.WriteAllText(ConfigFile, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
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
                Log($"Realtime start error: {ex.Message}");
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
            catch (Exception ex) { Log($"Audio send error: {ex.Message}"); }
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
            catch { }

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

            ReleaseAllModifierKeys();

            _isStopping = false;
            ResetUi();
            UpdateStatusLabel();
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

            PlayUiSound(SoundType.Stop);

            try { _waveIn?.StopRecording(); } catch { }
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;

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

            lblStatus.Content = "Processing...";
            lblStatus.Opacity = 1;

            string? text = await TranscribeAudioAsync(_currentFileName);
            if (!string.IsNullOrEmpty(text))
            {
                if (_postProcessBatch)
                {
                    lblStatus.Content = "Correcting...";
                    text = await PostProcessTranscription(text) ?? text;
                }

                if (_targetWindow != IntPtr.Zero)
                    SetForegroundWindow(_targetWindow);
                PasteTextToActiveWindow(text);
                HistoryService.Add(text);
                PlayUiSound(SoundType.Success);
            }
            else
            {
                PlayUiSound(SoundType.Error);
                lblStatus.Content = "Error";
                await Task.Delay(1500);
            }

            ReleaseAllModifierKeys();
            ResetUi();
            UpdateStatusLabel();
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
            string? transcribedVoice = await TranscribeAudioAsync(_currentFileName);

            if (!string.IsNullOrEmpty(transcribedVoice))
            {
                lblStatus.Content = "AI...";
                string? aiResponse = await ProcessAiQueryAsync(selectedText, transcribedVoice);
                if (!string.IsNullOrEmpty(aiResponse))
                {
                    PasteTextToActiveWindow(aiResponse);
                    HistoryService.Add(aiResponse);
                    PlayUiSound(SoundType.Success);
                }
                else { PlayUiSound(SoundType.Error); lblStatus.Content = "AI error"; }
            }
            else { PlayUiSound(SoundType.Error); lblStatus.Content = "Transcribe error"; }

            ReleaseAllModifierKeys();
            ResetUi();
            UpdateStatusLabel();
        }

        // ── Transcription dispatch ─────────────────────────────────────

        private async Task<string?> TranscribeAudioAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            // ── Local ONNX provider ──
            if (IsLocalOnnxProvider)
            {
                try
                {
                    if (_cohereOnnx == null)
                    {
                        _cohereOnnx = new CohereOnnxTranscriber();
                        if (!_cohereOnnx.ModelFilesExist())
                        {
                            Log("Cohere ONNX model files not found in %APPDATA%\\.WhisperInk\\cohere-onnx\\");
                            return null;
                        }
                    }
                    string language = GetActiveProvider()?.Language ?? "en";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await _cohereOnnx.TranscribeAsync(filePath, language);
                    sw.Stop();
                    double audioMs = GetWavDurationMs(filePath);
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    Log($"Cohere ONNX took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere ONNX error: {ex.Message}");
                    return null;
                }
            }

            // ── Local GGUF (CrispASR subprocess-per-call) ──
            if (IsLocalGgufProvider)
            {
                try
                {
                    if (_cohereGguf == null)
                    {
                        _cohereGguf = new CohereGgufTranscriber();
                        if (!_cohereGguf.ModelFilesExist())
                        {
                            Log("CrispASR/Cohere GGUF files not found in %APPDATA%\\.WhisperInk\\cohere-gguf\\");
                            return null;
                        }
                    }
                    string language = GetActiveProvider()?.Language ?? "en";
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await _cohereGguf.TranscribeAsync(filePath, language);
                    sw.Stop();
                    double audioMs = GetWavDurationMs(filePath);
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    Log($"Cohere GGUF (subprocess) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere GGUF error: {ex.Message}");
                    return null;
                }
            }

            // ── Local GGUF CPU server (port 8766) — in-memory fast path ──
            if (IsLocalGgufServerProvider)
            {
                try
                {
                    if (_cohereGgufServer == null)
                    {
                        _cohereGgufServer = new CohereGgufServerTranscriber();
                        if (!_cohereGgufServer.ModelFilesExist())
                        {
                            Log("CrispASR/Cohere GGUF server files not found in %APPDATA%\\.WhisperInk\\cohere-gguf\\");
                            return null;
                        }
                    }
                    string language = GetActiveProvider()?.Language ?? "en";
                    var biasTerms = GetActiveProvider()?.ContextBiasMode == "cohere_terms" ? _contextBiasTerms : null;
                    if (biasTerms != null && biasTerms.Count > 0)
                        Log($"[bias] sending {biasTerms.Count} terms as prompt: {string.Join(", ", biasTerms)}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    // Prefer in-memory bytes when available (saves ~20-30ms).
                    string? result;
                    double audioMs;
                    if (_lastWavBytes != null && _lastWavBytes.Length > 0)
                    {
                        result  = await _cohereGgufServer.TranscribeAsync(_lastWavBytes, language, biasTerms);
                        audioMs = GetWavDurationMs(_lastWavBytes);
                    }
                    else
                    {
                        result  = await _cohereGgufServer.TranscribeAsync(filePath, language, biasTerms);
                        audioMs = GetWavDurationMs(filePath);
                    }

                    sw.Stop();
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    string mode = _lastWavBytes != null ? "mem" : "disk";
                    Log($"Cohere GGUF (server/{mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere GGUF server error: {ex.Message}");
                    return null;
                }
            }

            // ── Local GGUF CUDA server (port 8767) — in-memory fast path ──
            if (IsLocalGgufCudaServerProvider)
            {
                try
                {
                    if (_cohereGgufCudaServer == null)
                    {
                        _cohereGgufCudaServer = new CohereGgufCudaServerTranscriber();
                        if (!_cohereGgufCudaServer.ModelFilesExist())
                        {
                            Log("CrispASR/Cohere GGUF CUDA server files not found in %APPDATA%\\.WhisperInk\\cohere-gguf-cuda\\");
                            return null;
                        }
                    }
                    string language = GetActiveProvider()?.Language ?? "en";
                    var biasTerms = GetActiveProvider()?.ContextBiasMode == "cohere_terms" ? _contextBiasTerms : null;
                    if (biasTerms != null && biasTerms.Count > 0)
                        Log($"[bias] sending {biasTerms.Count} terms as prompt: {string.Join(", ", biasTerms)}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    string? result;
                    double audioMs;
                    if (_lastWavBytes != null && _lastWavBytes.Length > 0)
                    {
                        result  = await _cohereGgufCudaServer.TranscribeAsync(_lastWavBytes, language, biasTerms);
                        audioMs = GetWavDurationMs(_lastWavBytes);
                    }
                    else
                    {
                        result  = await _cohereGgufCudaServer.TranscribeAsync(filePath, language, biasTerms);
                        audioMs = GetWavDurationMs(filePath);
                    }

                    sw.Stop();
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    string mode = _lastWavBytes != null ? "mem" : "disk";
                    Log($"Cohere GGUF (CUDA/{mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere GGUF CUDA error: {ex.Message}");
                    return null;
                }
            }

            // ── Local GGUF CUDA Q8 server (port 8768) — in-memory fast path ──
            if (IsLocalGgufCudaQ8ServerProvider)
            {
                try
                {
                    if (_cohereGgufCudaQ8Server == null)
                    {
                        _cohereGgufCudaQ8Server = new CohereGgufCudaQ8ServerTranscriber();
                        if (!_cohereGgufCudaQ8Server.ModelFilesExist())
                        {
                            Log("CrispASR/Cohere GGUF CUDA Q8 server files not found in %APPDATA%\\.WhisperInk\\cohere-gguf-cuda-q8\\");
                            return null;
                        }
                    }
                    string language = GetActiveProvider()?.Language ?? "en";
                    var biasTerms = GetActiveProvider()?.ContextBiasMode == "cohere_terms" ? _contextBiasTerms : null;
                    if (biasTerms != null && biasTerms.Count > 0)
                        Log($"[bias] sending {biasTerms.Count} terms as prompt: {string.Join(", ", biasTerms)}");
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    string? result;
                    double audioMs;
                    if (_lastWavBytes != null && _lastWavBytes.Length > 0)
                    {
                        result  = await _cohereGgufCudaQ8Server.TranscribeAsync(_lastWavBytes, language, biasTerms);
                        audioMs = GetWavDurationMs(_lastWavBytes);
                    }
                    else
                    {
                        result  = await _cohereGgufCudaQ8Server.TranscribeAsync(filePath, language, biasTerms);
                        audioMs = GetWavDurationMs(filePath);
                    }

                    sw.Stop();
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    string mode = _lastWavBytes != null ? "mem" : "disk";
                    Log($"Cohere GGUF (CUDA Q8/{mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere GGUF CUDA Q8 error: {ex.Message}");
                    return null;
                }
            }

            // ── Parakeet (CrispASR server, auto-spawned) ──────────────
            if (IsParakeetLocalProvider)
            {
                try
                {
                    var prov = GetActiveProvider();
                    int port = TryParsePortFromUrl(prov?.BaseUrl, 8103);
                    if (_parakeetServer == null || _parakeetServer.Port != port)
                    {
                        _parakeetServer?.Dispose();
                        _parakeetServer = new CrispAsrServerTranscriber(
                            modelGlob: "parakeet-*.gguf",
                            port: port,
                            displayName: "Parakeet");
                        if (!_parakeetServer.ModelFilesExist())
                        {
                            Log($"Parakeet: {_parakeetServer.DiagnoseMissing()}");
                            return null;
                        }
                    }

                    string language = prov?.Language ?? "en";
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    string? result;
                    double audioMs;
                    if (_lastWavBytes != null && _lastWavBytes.Length > 0)
                    {
                        result  = await _parakeetServer.TranscribeAsync(_lastWavBytes, language);
                        audioMs = GetWavDurationMs(_lastWavBytes);
                    }
                    else
                    {
                        result  = await _parakeetServer.TranscribeAsync(filePath, language);
                        audioMs = GetWavDurationMs(filePath);
                    }

                    sw.Stop();
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    string mode = _lastWavBytes != null ? "mem" : "disk";
                    Log($"Parakeet (server/{mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}× — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Parakeet error: {ex.Message}");
                    return null;
                }
            }

            // ── Cohere Q4 Local (CrispASR server, auto-spawned) ──────────
            if (IsCohereLocalQ4Provider)
            {
                try
                {
                    var prov = GetActiveProvider();
                    int port = TryParsePortFromUrl(prov?.BaseUrl, 8104);
                    if (_cohereQ4Server == null || _cohereQ4Server.Port != port)
                    {
                        _cohereQ4Server?.Dispose();
                        _cohereQ4Server = new CrispAsrServerTranscriber(
                            modelGlob: "cohere-transcribe-q4_k.gguf",
                            port: port,
                            displayName: "Cohere Q4",
                            backendHint: "cohere");
                        if (!_cohereQ4Server.ModelFilesExist())
                        {
                            Log($"Cohere Q4: {_cohereQ4Server.DiagnoseMissing()}");
                            return null;
                        }
                    }

                    string language = prov?.Language ?? "en";
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    string? result;
                    double audioMs;
                    if (_lastWavBytes != null && _lastWavBytes.Length > 0)
                    {
                        result  = await _cohereQ4Server.TranscribeAsync(_lastWavBytes, language);
                        audioMs = GetWavDurationMs(_lastWavBytes);
                    }
                    else
                    {
                        result  = await _cohereQ4Server.TranscribeAsync(filePath, language);
                        audioMs = GetWavDurationMs(filePath);
                    }

                    sw.Stop();
                    double rtfx = audioMs > 0 && sw.ElapsedMilliseconds > 0 ? audioMs / sw.ElapsedMilliseconds : 0;
                    string mode = _lastWavBytes != null ? "mem" : "disk";
                    Log($"Cohere Q4 (server/{mode}) took {sw.ElapsedMilliseconds}ms on {audioMs:F0}ms audio = RTFx {rtfx:F2}x — result: {result?[..Math.Min(200, result?.Length ?? 0)]}");
                    return result;
                }
                catch (Exception ex)
                {
                    Log($"Cohere Q4 error: {ex.Message}");
                    return null;
                }
            }

            // ── HTTP providers ────────────────────────────────────────
            var activeProvider = GetActiveProvider();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _audioApiUrl);

                if (!string.IsNullOrEmpty(_activeApiKey))
                {
                    if (!string.IsNullOrWhiteSpace(_activeAuthHeaderName))
                        request.Headers.Add(_activeAuthHeaderName, _activeApiKey);
                    else
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _activeApiKey);
                }

                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(filePath);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");

                if (!string.IsNullOrWhiteSpace(_audioModel))
                    content.Add(new StringContent(_audioModel), _activeModelFieldName);

                string language = activeProvider?.Language ?? "en";
                if (string.IsNullOrWhiteSpace(_activeAuthHeaderName))
                    content.Add(new StringContent(language), "language");

                if (activeProvider?.TranscriptionTemperature.HasValue == true)
                    content.Add(
                        new StringContent(activeProvider.TranscriptionTemperature.Value
                            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
                        "temperature");

                if (_contextBiasTerms.Count > 0)
                {
                    string biasMode = activeProvider?.ContextBiasMode ?? "none";
                    switch (biasMode)
                    {
                        case "cohere_terms":
                            content.Add(new StringContent(JsonSerializer.Serialize(_contextBiasTerms)), "context_bias_terms");
                            break;

                        case "whisper_prompt":
                            content.Add(new StringContent(string.Join(", ", _contextBiasTerms)), "prompt");
                            break;
                    }
                }

                // ElevenLabs Scribe v2 keyterms — repeated form fields (FastAPI List[str] convention).
                // If this yields a 422, switch to the JSON fallback below.
                if (!string.IsNullOrWhiteSpace(activeProvider?.ScribeKeytermsRaw) &&
                    !string.IsNullOrWhiteSpace(activeProvider?.AuthHeaderName))
                {
                    var keyterms = activeProvider.GetValidatedKeyterms(out var ktWarnings);
                    foreach (var w in ktWarnings) Log($"[keyterms] {w}");
                    if (keyterms.Count > 0)
                    {
                        Log($"[keyterms] sending {keyterms.Count} terms");
                        foreach (var term in keyterms)
                            content.Add(new StringContent(term), "keyterms");
                        // JSON fallback (uncomment if repeated fields return 422):
                        // content.Add(new StringContent(JsonSerializer.Serialize(keyterms)), "keyterms");
                    }
                }

                content.Add(fileContent, "file", "audio.wav");

                request.Content = content;
                var response = await _httpClient.SendAsync(request);
                string responseString = await response.Content.ReadAsStringAsync();
                Log($"Transcription response ({response.StatusCode}): {responseString[..Math.Min(500, responseString.Length)]}");

                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(responseString);
                if (doc.RootElement.TryGetProperty("text", out var textElement)) return textElement.GetString();
            }
            catch (Exception ex) { Log($"Network error: {ex.Message}"); }
            return null;
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
            catch { return ""; }
        }

        private void PasteTextToActiveWindow(string text)
        {
            text = " " + text;
            var staThread = new Thread(() => { try { Clipboard.SetText(text); } catch { } });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            SimulateCtrlV();
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
            catch { }
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

        private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();

            var providerMenu = new MenuItem { Header = $"🔌 Provider: {GetActiveProvider()?.Name ?? "?"}" };
            foreach (var provider in _providers)
            {
                string pid = provider.Id;
                var pItem = new MenuItem
                {
                    Header = provider.Name,
                    IsChecked = pid == _activeProviderId
                };
                pItem.Click += (_, _) => SwitchProvider(pid);
                providerMenu.Items.Add(pItem);
            }
            providerMenu.Items.Add(new Separator());
            var configProvidersItem = new MenuItem { Header = "⚙ Configure Providers..." };
            configProvidersItem.Click += (_, _) =>
            {
                var win = new ProviderSettingsWindow(_providers, _activeProviderId);
                if (win.ShowDialog() == true)
                {
                    _providers = win.ResultProviders;
                    if (!_providers.Any(p => p.Id == _activeProviderId))
                        _activeProviderId = _providers.First().Id;
                    ApplyActiveProvider();
                    SaveConfig();
                    UpdateStatusLabel();
                }
            };
            providerMenu.Items.Add(configProvidersItem);
            menu.Items.Add(providerMenu);

            menu.Items.Add(new Separator());

            var modeMenu = new MenuItem { Header = $"⚡ Mode: {_dictationMode}" };
            var rtItem = new MenuItem { Header = "Realtime (live typing)", IsChecked = IsRealtimeMode };
            rtItem.Click += (_, _) =>
            {
                if (!_activeSupportsRealtime)
                {
                    MessageBox.Show("Current provider does not support Mistral Realtime.\nSwitch to Mistral or use Batch mode.",
                        "Realtime Unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _dictationMode = "Realtime"; SaveConfig(); UpdateStatusLabel();
            };
            modeMenu.Items.Add(rtItem);
            var batchItem = new MenuItem { Header = "Batch (record → paste)", IsChecked = !IsRealtimeMode };
            batchItem.Click += (_, _) => { _dictationMode = "Batch"; SaveConfig(); UpdateStatusLabel(); };
            modeMenu.Items.Add(batchItem);
            menu.Items.Add(modeMenu);

            menu.Items.Add(new Separator());

            var micMenu = new MenuItem { Header = "🎙 Microphone" };
            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var cap = WaveIn.GetCapabilities(i);
                int deviceIndex = i;
                var item = new MenuItem { Header = cap.ProductName, IsChecked = i == _selectedDeviceNumber };
                item.Click += (_, _) => { _selectedDeviceNumber = deviceIndex; SaveConfig(); };
                micMenu.Items.Add(item);
            }
            menu.Items.Add(micMenu);

            var soundItem = new MenuItem { Header = _isSoundEnabled ? "🔊 Sound: ON" : "🔇 Sound: OFF" };
            soundItem.Click += (_, _) => { _isSoundEnabled = !_isSoundEnabled; SaveConfig(); };
            menu.Items.Add(soundItem);

            menu.Items.Add(new Separator());

            var delayMenu = new MenuItem { Header = "⏱ Streaming Delay" };
            foreach (int ms in new[] { 240, 480, 1000, 1500, 2400 })
            {
                int delayMs = ms;
                string label = ms switch { 240 => "240ms (fastest)", 480 => "480ms (recommended)", 1000 => "1000ms", 1500 => "1500ms", 2400 => "2400ms (most accurate)", _ => $"{ms}ms" };
                var delayItem = new MenuItem { Header = label, IsChecked = _targetStreamingDelayMs == ms };
                delayItem.Click += (_, _) => { _targetStreamingDelayMs = delayMs; SaveConfig(); };
                delayMenu.Items.Add(delayItem);
            }
            menu.Items.Add(delayMenu);

            menu.Items.Add(new Separator());

            var promptItem = new MenuItem { Header = "📝 System Prompt" };
            promptItem.Click += (_, _) =>
            {
                var pw = new PromptWindow(_systemPrompt);
                if (pw.ShowDialog() == true) { _systemPrompt = pw.PromptText; SaveConfig(); }
            };
            menu.Items.Add(promptItem);

            var biasItem = new MenuItem { Header = "🎯 Context Bias Terms" };
            biasItem.Click += (_, _) =>
            {
                var biasWindow = new ContextBiasWindow(_contextBiasTerms);
                if (biasWindow.ShowDialog() == true)
                {
                    _contextBiasTerms = biasWindow.BiasTerms;
                    SaveConfig();
                }
            };
            menu.Items.Add(biasItem);

            var historyItem = new MenuItem { Header = "📋 History" };
            historyItem.Click += (_, _) => new HistoryWindow().Show();
            menu.Items.Add(historyItem);

            menu.Items.Add(new Separator());

            var configItem = new MenuItem { Header = $"📂 Config: {ConfigFile}" };
            configItem.Click += (_, _) => Process.Start("explorer.exe", $"/select,\"{ConfigFile}\"");
            menu.Items.Add(configItem);

            var ppItem = new MenuItem { Header = _postProcessBatch ? "🩺 Med Correction: ON" : "🩺 Med Correction: OFF" };
            ppItem.Click += (_, _) =>
            {
                _postProcessBatch = !_postProcessBatch;
                SaveConfig();
                UpdateStatusLabel();
            };
            menu.Items.Add(ppItem);

            var exitItem = new MenuItem { Header = "❌ Exit" };
            exitItem.Click += (_, _) => Application.Current.Shutdown();
            menu.Items.Add(exitItem);

            menu.IsOpen = true;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
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