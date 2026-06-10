using System;
using System.Runtime.InteropServices;

namespace WhisperInk
{
    /// <summary>
    /// Owns the WH_KEYBOARD_LL global hook and all hotkey decision logic:
    /// modifier tracking, key suppression while a dictation hotkey is held,
    /// and the synthetic-event filter (events stamped with
    /// <see cref="TextInjector.SyntheticMarkerValue"/> are our own
    /// injections and never treated as physical keys).
    ///
    /// Hotkeys: Ctrl+Space hold-to-dictate, Ctrl+Alt hold-to-instruct.
    /// All callbacks fire on the hook thread — the host wraps them in
    /// Dispatcher.BeginInvoke.
    /// </summary>
    public sealed class KeyboardHookService : IDisposable
    {
        public enum HotkeyMode { Dictation, AnalyzeContext }

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr hInstance, uint threadId);

        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

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
        private const int VK_SPACE = 0x20;

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

        private readonly Func<bool> _isRecording;
        private readonly Action<IntPtr> _onDictationStart; // arg: foreground hwnd captured at press
        private readonly Action _onDictationStop;
        private readonly Action _onAnalyzeStart;
        private readonly Action _onAnalyzeStop;

        private IntPtr _hookId = IntPtr.Zero;
        // Field, not a local: the hook keeps a native pointer to this
        // delegate — if the GC collects it the hook dies silently.
        private LowLevelKeyboardProc? _hookCallback;

        private bool _ctrlPressed;
        private bool _winPressed;
        private bool _altPressed;
        private bool _spacePressed;
        private bool _suppressingKeys;

        public HotkeyMode CurrentMode { get; private set; } = HotkeyMode.Dictation;

        public KeyboardHookService(
            Func<bool> isRecording,
            Action<IntPtr> onDictationStart,
            Action onDictationStop,
            Action onAnalyzeStart,
            Action onAnalyzeStop)
        {
            _isRecording = isRecording;
            _onDictationStart = onDictationStart;
            _onDictationStop = onDictationStop;
            _onAnalyzeStart = onAnalyzeStart;
            _onAnalyzeStop = onAnalyzeStop;
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            _hookCallback = HookCallback;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, GetModuleHandle(module.ModuleName!), 0);
        }

        /// <summary>Re-arms hotkey suppression — called by the start paths
        /// so held Ctrl/Space don't leak into the focused app mid-dictation.</summary>
        public void BeginSuppression() => _suppressingKeys = true;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var hookData = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vkCode = hookData.vkCode;
                bool isDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
                bool isUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);
                bool isSynthetic = (hookData.dwExtraInfo == new IntPtr(TextInjector.SyntheticMarkerValue));

                if (_suppressingKeys && !isSynthetic)
                {
                    if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL || vkCode == VK_SPACE)
                    {
                        if (vkCode == VK_SPACE) _spacePressed = isDown;
                        else _ctrlPressed = isDown;

                        if (isUp && _isRecording() && CurrentMode == HotkeyMode.Dictation)
                        {
                            if (!_ctrlPressed || !_spacePressed)
                                _onDictationStop();
                        }
                        if (!_ctrlPressed && !_spacePressed)
                            _suppressingKeys = false;

                        return (IntPtr)1;
                    }
                }

                if (vkCode == VK_LCONTROL || vkCode == VK_RCONTROL)
                {
                    if (!isSynthetic) _ctrlPressed = isDown;
                    if (isUp && _isRecording() && CurrentMode == HotkeyMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            _onAnalyzeStop();
                    }
                }
                else if (vkCode == VK_SPACE)
                {
                    if (!isSynthetic) _spacePressed = isDown;
                    if (isDown && !isSynthetic && _ctrlPressed && !_isRecording() && !_suppressingKeys)
                    {
                        CurrentMode = HotkeyMode.Dictation;
                        _suppressingKeys = true;
                        _onDictationStart(GetForegroundWindow());
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
                    if (isDown && _ctrlPressed && !_isRecording())
                    {
                        CurrentMode = HotkeyMode.AnalyzeContext;
                        _onAnalyzeStart();
                    }
                    if (isUp && _isRecording() && CurrentMode == HotkeyMode.AnalyzeContext)
                    {
                        if (!_ctrlPressed || !_altPressed)
                            _onAnalyzeStop();
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _hookCallback = null;
        }
    }
}
