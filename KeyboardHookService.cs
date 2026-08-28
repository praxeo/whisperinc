using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WhisperInk
{
    /// <summary>
    /// Owns the WH_KEYBOARD_LL global hook and all hotkey decision logic:
    /// modifier tracking, key suppression while a dictation hotkey is held,
    /// and the synthetic-event filter (events stamped with
    /// <see cref="TextInjector.SyntheticMarkerValue"/> are our own
    /// injections and never treated as physical keys).
    ///
    /// Hotkey: Ctrl+Space hold-to-dictate.
    /// All callbacks fire on the hook thread — the host wraps them in
    /// Dispatcher.BeginInvoke.
    ///
    /// Self-healing: Windows silently drops a WH_KEYBOARD_LL hook whose
    /// callback overruns LowLevelHooksTimeout, and any app that installs
    /// its own low-level keyboard hook after ours runs ahead of us in the
    /// chain from then on (observed in practice: PowerToys re-hooking on
    /// its own restart demoted WhisperInk's hook and Ctrl+Space stopped
    /// reaching us with no exception anywhere). A watchdog timer notices
    /// the hook has gone quiet while the user is visibly typing and
    /// transparently re-installs it — see <see cref="WatchdogInterval"/>.
    /// </summary>
    public sealed class KeyboardHookService : IDisposable
    {
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

        // Watchdog liveness probe. The low-order bit reports "pressed since
        // the last call to this function," which is tracked by the OS
        // independent of any hook — so it still works when our own hook is
        // the one that's gone silent.
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
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
        private readonly Action<string> _log;

        private IntPtr _hookId = IntPtr.Zero;
        // Field, not a local: the hook keeps a native pointer to this
        // delegate — if the GC collects it the hook dies silently.
        private LowLevelKeyboardProc? _hookCallback;

        private bool _ctrlPressed;
        private bool _spacePressed;
        private bool _suppressingKeys;

        // Watchdog: how often we check, and (since the check is "no
        // heartbeat since last tick") also the staleness threshold.
        private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(30);
        private DispatcherTimer? _watchdogTimer;
        // Environment.TickCount64, not DateTime — monotonic, immune to
        // clock/DST adjustments, and cheap. Written and read only from
        // HookCallback / the watchdog Tick, both of which run on the UI
        // thread (the thread that called Install()), so no locking needed.
        private long _lastHookActivityMs;

        public KeyboardHookService(
            Func<bool> isRecording,
            Action<IntPtr> onDictationStart,
            Action onDictationStop,
            Action<string> log)
        {
            _isRecording = isRecording;
            _onDictationStart = onDictationStart;
            _onDictationStop = onDictationStop;
            _log = log ?? (_ => { });
        }

        public void Install()
        {
            if (_hookId != IntPtr.Zero) return;
            _hookCallback = HookCallback;
            _hookId = SetHook();
            _lastHookActivityMs = Environment.TickCount64;
            StartWatchdog();
        }

        private IntPtr SetHook()
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module = process.MainModule!;
            return SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback!, GetModuleHandle(module.ModuleName!), 0);
        }

        private void StartWatchdog()
        {
            _watchdogTimer = new DispatcherTimer { Interval = WatchdogInterval };
            _watchdogTimer.Tick += (_, _) => WatchdogTick();
            _watchdogTimer.Start();
        }

        private void WatchdogTick()
        {
            long quietMs = Environment.TickCount64 - _lastHookActivityMs;
            if (quietMs < (long)WatchdogInterval.TotalMilliseconds) return; // heartbeat is current

            if (!AnyKeyPressedSinceLastCheck()) return; // quiet because the user is quiet -- fine

            _log($"[hook-watchdog] no key events reached the hook in {quietMs / 1000.0:F0}s while keys were being pressed -- reinstalling (LowLevelHooksTimeout drop, or another app's hook now runs first)");
            Reinstall();
        }

        /// <summary>Sweeps the vkey table once (only ever called from a
        /// 30s-interval timer, so ~254 cheap syscalls is inconsequential)
        /// looking for any key pressed since the last sweep. Uses
        /// GetAsyncKeyState rather than our own hook state precisely
        /// because it must still work when the hook itself is the thing
        /// that's dead.</summary>
        private static bool AnyKeyPressedSinceLastCheck()
        {
            for (int vk = 1; vk < 255; vk++)
            {
                if ((GetAsyncKeyState(vk) & 1) != 0) return true;
            }
            return false;
        }

        private void Reinstall()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            // _hookCallback is untouched — same delegate instance as
            // before, so GC still can't collect out from under the native
            // hook chain.
            _hookId = SetHook();
            _lastHookActivityMs = Environment.TickCount64;
        }

        /// <summary>Re-arms hotkey suppression — called by the start paths
        /// so held Ctrl/Space don't leak into the focused app mid-dictation.</summary>
        public void BeginSuppression() => _suppressingKeys = true;

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            _lastHookActivityMs = Environment.TickCount64;
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

                        if (isUp && _isRecording())
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
                }
                else if (vkCode == VK_SPACE)
                {
                    if (!isSynthetic) _spacePressed = isDown;
                    if (isDown && !isSynthetic && _ctrlPressed && !_isRecording() && !_suppressingKeys)
                    {
                        _suppressingKeys = true;
                        _onDictationStart(GetForegroundWindow());
                        return (IntPtr)1;
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            _watchdogTimer?.Stop();
            _watchdogTimer = null;
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _hookCallback = null;
        }
    }
}
