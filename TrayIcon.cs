using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WhisperInk
{
    /// <summary>
    /// Owns the Windows notification-area icon and renders the shared
    /// <see cref="MenuNode"/> tree (built by MainWindow.BuildAppMenu) as a
    /// WinForms ContextMenuStrip. Pure rendering — every action lives in
    /// the node tree, so the tray and the floating bar can never drift.
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private readonly Func<IReadOnlyList<MenuNode>> _menuSource;
        private readonly Action _onActivate;
        private readonly Func<string> _trayTooltip;
        private readonly System.Windows.Forms.NotifyIcon _notify;

        public TrayIconManager(
            Func<IReadOnlyList<MenuNode>> menuSource,
            Action onActivate,
            Func<string> trayTooltip)
        {
            _menuSource = menuSource;
            _onActivate = onActivate;
            _trayTooltip = trayTooltip;

            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon    = LoadIcon(),
                Text    = "WhisperInk",
                Visible = true,
            };

            _notify.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) _onActivate();
            };
            _notify.MouseDoubleClick += (_, _) => _onActivate();

            // Rebuild on every open so check states stay current. The seed
            // placeholder matters: WinForms cancels Opening when the strip
            // has no items yet, so the very first right-click would
            // otherwise race an empty strip.
            _notify.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
            _notify.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripMenuItem("…"));
            _notify.ContextMenuStrip.Opening += (_, _) =>
            {
                _notify.ContextMenuStrip.Items.Clear();
                foreach (var node in MenuNode.ForSurface(_menuSource(), MenuSurface.TrayOnly))
                    _notify.ContextMenuStrip.Items.Add(Render(node));
            };
        }

        private static System.Windows.Forms.ToolStripItem Render(MenuNode node)
        {
            if (node.IsSeparator) return new System.Windows.Forms.ToolStripSeparator();

            var item = new System.Windows.Forms.ToolStripMenuItem(node.Header)
            {
                // Real Checked, never CheckOnClick: the toggle flows through
                // Action (which flips state + saves) and the check state is
                // re-derived from the model on the next open.
                Checked = node.IsChecked,
                Enabled = node.IsEnabled,
            };
            if (!string.IsNullOrWhiteSpace(node.ToolTip)) item.ToolTipText = node.ToolTip;
            if (node.Action is { } action) item.Click += (_, _) => action();
            foreach (var child in MenuNode.ForSurface(node.Children, MenuSurface.TrayOnly))
                item.DropDownItems.Add(Render(child));
            return item;
        }

        /// <summary>Re-reads the tooltip text (provider + health) from the
        /// host. NotifyIcon.Text hard-caps at 63 chars.</summary>
        public void RefreshTooltip()
        {
            try
            {
                string text = _trayTooltip();
                _notify.Text = text.Length <= 63 ? text : text.Substring(0, 63);
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
}
