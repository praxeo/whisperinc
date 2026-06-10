using System;
using System.Collections.Generic;
using System.Linq;

namespace WhisperInk
{
    /// <summary>
    /// Which surfaces a menu node appears on. The same canonical tree
    /// (built by <c>MainWindow.BuildAppMenu()</c>) is rendered both as the
    /// tray icon's WinForms ContextMenuStrip and as the floating bar's WPF
    /// ContextMenu; nodes can opt out of either surface.
    /// </summary>
    public enum MenuSurface { Both, TrayOnly, BarOnly }

    /// <summary>
    /// One node of the shared app menu. Pure data — no WinForms or WPF
    /// types — so a single definition drives both renderers and the two
    /// menus can never drift apart again.
    /// </summary>
    public sealed record MenuNode
    {
        public string Header { get; init; } = "";
        public bool IsSeparator { get; init; }
        public bool IsChecked { get; init; }
        public bool IsEnabled { get; init; } = true;
        public string? ToolTip { get; init; }
        public Action? Action { get; init; }
        public IReadOnlyList<MenuNode> Children { get; init; } = Array.Empty<MenuNode>();
        public MenuSurface Surface { get; init; } = MenuSurface.Both;

        public static MenuNode Separator(MenuSurface surface = MenuSurface.Both)
            => new() { IsSeparator = true, Surface = surface };

        /// <summary>Filters a node list for one surface and collapses the
        /// separator runs that filtering can leave behind (doubled, leading,
        /// or trailing separators).</summary>
        public static List<MenuNode> ForSurface(IEnumerable<MenuNode> nodes, MenuSurface keep)
        {
            var visible = nodes.Where(n => n.Surface == MenuSurface.Both || n.Surface == keep).ToList();
            var result = new List<MenuNode>(visible.Count);
            foreach (var n in visible)
            {
                if (n.IsSeparator && (result.Count == 0 || result[^1].IsSeparator)) continue;
                result.Add(n);
            }
            while (result.Count > 0 && result[^1].IsSeparator) result.RemoveAt(result.Count - 1);
            return result;
        }
    }

    /// <summary>Renders a MenuNode tree as a WPF ContextMenu (the floating
    /// bar's right-click menu).</summary>
    public static class WpfMenuRenderer
    {
        public static System.Windows.Controls.ContextMenu Build(IEnumerable<MenuNode> nodes)
        {
            var menu = new System.Windows.Controls.ContextMenu();
            foreach (var node in MenuNode.ForSurface(nodes, MenuSurface.BarOnly))
                menu.Items.Add(Render(node));
            return menu;
        }

        private static object Render(MenuNode node)
        {
            if (node.IsSeparator) return new System.Windows.Controls.Separator();

            // IsCheckable stays false while IsChecked drives the glyph: WPF
            // then renders the check without toggling visual state before
            // the click handler runs (state is re-derived on next open).
            var item = new System.Windows.Controls.MenuItem
            {
                Header = node.Header,
                IsChecked = node.IsChecked,
                IsEnabled = node.IsEnabled,
            };
            if (!string.IsNullOrWhiteSpace(node.ToolTip)) item.ToolTip = node.ToolTip;
            if (node.Action is { } action) item.Click += (_, _) => action();
            foreach (var child in MenuNode.ForSurface(node.Children, MenuSurface.BarOnly))
                item.Items.Add(Render(child));
            return item;
        }
    }
}
