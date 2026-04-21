using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace WhisperInk
{
    /// <summary>
    /// Owns the Windows notification-area icon and its right-click
    /// support menu. Pure UI — all actions delegate back to the main
    /// window through the <see cref="TrayIconHost"/> callback interface.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private readonly TrayIconHost _host;
        private readonly System.Windows.Forms.NotifyIcon _notify;

        public TrayIconManager(TrayIconHost host)
        {
            _host = host;

            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon    = LoadIcon(),
                Text    = "WhisperInk",
                Visible = true,
            };

            _notify.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) _host.ShowMainWindow();
            };
            _notify.MouseDoubleClick += (_, _) => _host.ShowMainWindow();

            _notify.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            _notify.ContextMenuStrip.Opening += (_, _) =>
            {
                _notify.ContextMenuStrip.Items.Clear();
                BuildMenu(_notify.ContextMenuStrip.Items);
            };
        }

        private void BuildMenu(System.Windows.Forms.ToolStripItemCollection items)
        {
            items.Add(Item("Show Window", _ => _host.ShowMainWindow()));
            items.Add(ProviderLabel());
            items.Add(GpuBackendSubMenu());
            items.Add(new System.Windows.Forms.ToolStripSeparator());

            items.Add(Item("Open debug log",    _ => OpenPath(_host.DebugLogPath)));
            items.Add(Item("Open config folder",_ => OpenFolder(_host.ConfigFolder)));
            items.Add(Item("Open model folder", _ => OpenFolder(_host.ModelFolder)));
            items.Add(Item("Copy support bundle", _ => _host.CopySupportBundle()));
            items.Add(Item("Diagnose active provider", _ => _host.DiagnoseActiveProvider()));
            items.Add(new System.Windows.Forms.ToolStripSeparator());

            items.Add(Item("About…",       _ => _host.ShowAboutDialog()));
            items.Add(Item("View README",  _ => OpenUrl(AboutWindow.ReadmeUrl)));
            items.Add(new System.Windows.Forms.ToolStripSeparator());

            var quitOnClose = Checkable("Quit on close", _host.QuitOnClose, v => _host.SetQuitOnClose(v));
            items.Add(quitOnClose);

            var startup = Checkable("Launch at Windows start", _host.LaunchAtStartup, v => _host.SetLaunchAtStartup(v));
            items.Add(startup);

            items.Add(new System.Windows.Forms.ToolStripSeparator());
            items.Add(Item("Quit", _ => _host.QuitApplication()));
        }

        private System.Windows.Forms.ToolStripMenuItem GpuBackendSubMenu()
        {
            string current = _host.CrispGpuBackend;
            var parent = new System.Windows.Forms.ToolStripMenuItem("Local GPU backend");
            if (!string.IsNullOrWhiteSpace(_host.DetectedGpuSummary))
                parent.ToolTipText = _host.DetectedGpuSummary;

            (string label, string value)[] options =
            {
                ("Auto (recommended)", "auto"),
                ("Vulkan (GPU)",       "vulkan"),
                ("CUDA (NVIDIA GPU)",  "cuda"),
                ("CPU only",           "cpu"),
            };
            foreach (var (label, value) in options)
            {
                string capture = value;
                bool active = string.Equals(current, capture, StringComparison.OrdinalIgnoreCase);
                string prefix = active ? "● " : "   ";
                var mi = new System.Windows.Forms.ToolStripMenuItem(prefix + label);
                mi.Click += (_, _) => _host.SetCrispGpuBackend(capture);
                parent.DropDownItems.Add(mi);
            }
            return parent;
        }

        private System.Windows.Forms.ToolStripMenuItem ProviderLabel()
        {
            var report = _host.CurrentHealth;
            string name = _host.ActiveProviderName;
            var item = new System.Windows.Forms.ToolStripMenuItem($"{report.Dot}  Active provider: {name}") { Enabled = false };
            if (!string.IsNullOrWhiteSpace(report.Summary))
                item.ToolTipText = report.Summary;
            return item;
        }

        public void NotifyHealth(HealthReport r)
        {
            try
            {
                _notify.Text = $"WhisperInk — {r.Dot} {_host.ActiveProviderName}".Substring(0, Math.Min(63, ($"WhisperInk — {r.Dot} {_host.ActiveProviderName}").Length));
            }
            catch { }
        }

        public void ShowBalloon(string title, string body, bool warning = false)
        {
            try
            {
                _notify.BalloonTipTitle = title;
                _notify.BalloonTipText  = body;
                _notify.BalloonTipIcon  = warning
                    ? System.Windows.Forms.ToolTipIcon.Warning
                    : System.Windows.Forms.ToolTipIcon.Info;
                _notify.ShowBalloonTip(5000);
            }
            catch { }
        }

        private static System.Windows.Forms.ToolStripMenuItem Item(string text, Action<object?> click)
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(text);
            it.Click += (s, _) => click(s);
            return it;
        }

        private static System.Windows.Forms.ToolStripMenuItem Checkable(string text, bool initial, Action<bool> toggled)
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(text) { Checked = initial, CheckOnClick = true };
            it.CheckedChanged += (_, _) => toggled(it.Checked);
            return it;
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    System.Windows.MessageBox.Show($"Not found:\n{path}", "WhisperInk",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "WhisperInk",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
                System.Windows.MessageBox.Show(ex.Message, "WhisperInk",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private static System.Drawing.Icon LoadIcon()
        {
            // 1) Same-folder Assets/icon.ico (published build).
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"),
                Path.Combine(AppContext.BaseDirectory, "icon.ico"),
            };
            foreach (var p in candidates)
            {
                try { if (File.Exists(p)) return new System.Drawing.Icon(p); } catch { }
            }

            // 2) The executable's own icon (embedded via ApplicationIcon).
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe!);
                    if (ico != null) return ico;
                }
            }
            catch { }

            // 3) Hard fallback — the system application icon.
            return System.Drawing.SystemIcons.Application;
        }

        public void Dispose()
        {
            try { _notify.Visible = false; } catch { }
            try { _notify.ContextMenuStrip?.Dispose(); } catch { }
            try { _notify.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Contract implemented by MainWindow to back the tray menu. Keeps
    /// the tray free of direct main-window coupling.
    /// </summary>
    public interface TrayIconHost
    {
        string ActiveProviderName { get; }
        string DebugLogPath       { get; }
        string ConfigFolder       { get; }
        string ModelFolder        { get; }
        HealthReport CurrentHealth { get; }

        bool QuitOnClose      { get; }
        bool LaunchAtStartup  { get; }

        string CrispGpuBackend    { get; }
        string DetectedGpuSummary { get; }

        void ShowMainWindow();
        void CopySupportBundle();
        void DiagnoseActiveProvider();
        void ShowAboutDialog();
        void QuitApplication();
        void SetQuitOnClose(bool enabled);
        void SetLaunchAtStartup(bool enabled);
        void SetCrispGpuBackend(string value);
    }
}
