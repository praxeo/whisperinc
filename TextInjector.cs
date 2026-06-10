using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WhisperInk
{
    /// <summary>
    /// Synthetic text delivery into other applications: per-character
    /// WM_CHAR typing (realtime mode), clipboard paste-with-restore
    /// (batch / AI modes), selection grab via a synthetic Ctrl+C, and the
    /// modifier-release that prevents stuck hotkeys. Owns the pending
    /// clipboard-restore state so rapid-fire dictations still end with the
    /// user's original clipboard.
    /// </summary>
    public sealed class TextInjector
    {
        /// <summary>Stamped into dwExtraInfo on the synthetic key events the
        /// app must NOT react to itself; KeyboardHookService filters on it.
        /// The plain Ctrl+C / Ctrl+V synthesis is deliberately unstamped —
        /// it happens only while no recording is active.</summary>
        public const int SyntheticMarkerValue = 0x5AFE;
        private static readonly UIntPtr SyntheticMarker = (UIntPtr)SyntheticMarkerValue;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_CHAR = 0x0102;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_LMENU = 0xA4;
        private const byte VK_RMENU = 0xA5;
        private const byte VK_LWIN = 0x5B;
        private const byte VK_RWIN = 0x5C;
        private const byte KEYEVENTF_KEYUP_BYTE = 0x02;

        private readonly Action<string> _log;

        // Saved clipboard contents from before the most recent paste, restored
        // asynchronously after SimulateCtrlV() so dictation doesn't clobber what
        // the user had copied. If a new paste arrives during the restore window,
        // we cancel and reuse the pending data — that way rapid-fire dictation
        // still ends with the *original* clipboard, not the previous transcript.
        private IDataObject? _pendingRestoreData;
        private CancellationTokenSource? _pendingRestoreCts;

        public TextInjector(Action<string> log)
        {
            _log = log ?? (_ => { });
        }

        /// <summary>Realtime path: types each character straight into the
        /// target window via WM_CHAR — bypasses IME and focus stealing.</summary>
        public void TypeTextTo(IntPtr targetWindow, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (targetWindow == IntPtr.Zero) return;

            foreach (char c in text)
            {
                PostMessage(targetWindow, WM_CHAR, (IntPtr)c, IntPtr.Zero);
            }
        }

        /// <summary>Grabs the foreground app's selection via synthetic Ctrl+C.
        /// The 100ms sleep gives the target time to populate the clipboard —
        /// known wart, lives here until a clipboard-listener replaces it.</summary>
        public string GetSelectedText()
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
                _log($"GetSelectedText failed: {ex.Message}");
                return "";
            }
        }

        public void PasteTextToActiveWindow(string text)
        {
            text = " " + text;
            _log($"[diag] Paste: enter, len={text.Length}");

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
            _log("[diag] Paste: STA started, joining");
            staThread.Join();
            _log(clipEx == null
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
            _log("[diag] Paste: exit");
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
                catch (Exception ex) { _log($"Clipboard restore failed: {ex.Message}"); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join();

            if (ReferenceEquals(_pendingRestoreCts, cts))
            {
                _pendingRestoreCts = null;
                _pendingRestoreData = null;
                _log("[diag] Paste: clipboard restored");
            }
        }

        private void SimulateCtrlV()
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, 0, UIntPtr.Zero);
            keybd_event(0x56, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, UIntPtr.Zero);
        }

        /// <summary>Synthetic key-ups for every modifier, stamped with the
        /// marker so the hook ignores them. Runs after every recording: the
        /// hook swallows the user's physical key-ups while a hotkey is held,
        /// so this is the only thing telling the OS the keys came back up.</summary>
        public void ReleaseAllModifierKeys()
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(0xA0, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(0xA1, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(VK_LMENU, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(VK_RMENU, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
            keybd_event(VK_RWIN, 0, KEYEVENTF_KEYUP_BYTE, SyntheticMarker);
        }
    }
}
