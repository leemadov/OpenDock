using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace OpenDock
{
    // Finder's macOS 26 look: toolbar capsules, the path bar, colour tags and folder symbols, and the Tags view.
    partial class FinderWindow
    {
        const string TagPrefix = "::tag:";

        static bool Virtual(string p) { return p != null && p.StartsWith("::"); }
        static int TagOf(string p) { int c; return p != null && p.StartsWith(TagPrefix) && int.TryParse(p.Substring(TagPrefix.Length), out c) && c >= 0 && c < FolderLook.Names.Length ? c : -1; }

        static string LocationName(string p)
        {
            if (p == null) return "";
            if (p == AppsLocation) return "Applications";
            int tag = TagOf(p);
            if (tag >= 0) return FolderLook.Names[tag];
            return p;
        }

        // A row of toolbar buttons on one glass capsule, with a soft shadow under it.
        FrameworkElement Capsule(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var b in buttons)
            {
                b.Style = (Style)Application.Current.Resources["Pill"];
                b.Width = 38; b.Height = 30; b.Margin = new Thickness(0);
                var t = b.Content as TextBlock; if (t != null) t.FontSize = 15;
                row.Children.Add(b);
            }
            var pill = new Border { Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Child = row };
            pill.SetResourceReference(Border.BackgroundProperty, "Capsule");
            pill.SetResourceReference(Border.BorderBrushProperty, "CapsuleEdge");
            var shadow = new Border { Height = 36, CornerRadius = new CornerRadius(18), Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1.5, Direction = 270, Opacity = 0.10 } };
            shadow.SetResourceReference(Border.BackgroundProperty, "Capsule");
            var holder = new Grid { VerticalAlignment = VerticalAlignment.Center };
            holder.Children.Add(shadow); holder.Children.Add(pill);
            WindowChrome.SetIsHitTestVisibleInChrome(holder, true);
            return holder;
        }

        void GoToFolder()
        {
            location.Visibility = Visibility.Visible;
            address.Text = Virtual(path) ? "" : path;
            address.Focus(); address.SelectAll();
        }

        void MoreMenu(FrameworkElement anchor)
        {
            var cm = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
            Action<string, Action, bool> add = (text, a, enabled) => { var mi = new MenuItem { Header = text, IsEnabled = enabled }; mi.Click += delegate { a(); }; cm.Items.Add(mi); };
            add("New Folder", NewFolder, !Virtual(path));
            add("New Tab", () => (host ?? this).NewTab(path), true);
            add("Split View", () => (host ?? this).ToggleSplit(), true);
            cm.Items.Add(new Separator());
            add("Go to Folder…", GoToFolder, true);
            add("Open in Explorer", () => System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\""), !Virtual(path));
            cm.IsOpen = true;
        }

        // Colour tags for the selection (or, with nothing selected, this folder), as a row of dots like macOS.
        void TagMenu(ContextMenu into)
        {
            var targets = Selected();
            if (targets.Count == 0 && !Virtual(path)) targets = new List<string> { path };
            if (targets.Count == 0) return;
            var cm = into ?? new ContextMenu();
            cm.Items.Add(TagRow(targets, cm));
            var dirs = targets.Where(Directory.Exists).ToList();
            if (dirs.Count > 0) cm.Items.Add(SymbolMenu(dirs, cm));
            if (into == null) cm.IsOpen = true;
        }

        MenuItem TagRow(List<string> targets, ContextMenu cm)
        {
            int current = targets.Select(FolderLook.ColorOf).Distinct().Count() == 1 ? FolderLook.ColorOf(targets[0]) : -2;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 2, 0, 2) };
            for (int i = -1; i < FolderLook.Names.Length; i++)
            {
                int color = i;
                var dot = new Grid { Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent,
                    ToolTip = color < 0 ? "No Tag" : FolderLook.Names[color] };
                var e = new Ellipse { Width = 16, Height = 16, StrokeThickness = color < 0 ? 1.4 : (color == current ? 2.5 : 0) };
                if (color < 0) { e.SetResourceReference(Shape.StrokeProperty, "FgDim"); e.Fill = Brushes.Transparent; }
                else { e.Fill = Theme.B(FolderLook.Hex[color]); e.SetResourceReference(Shape.StrokeProperty, "Fg"); }
                dot.Children.Add(e);
                if (color < 0) dot.Children.Add(new Line { X1 = 6, Y1 = 16, X2 = 16, Y2 = 6, StrokeThickness = 1.4, Stroke = (Brush)Application.Current.Resources["FgDim"] });
                dot.MouseLeftButtonUp += delegate { cm.IsOpen = false; foreach (var t in targets) FolderLook.SetColor(t, color); };
                row.Children.Add(dot);
            }
            return new MenuItem { Header = row, StaysOpenOnClick = true };
        }

        MenuItem SymbolMenu(List<string> dirs, ContextMenu cm)
        {
            var menu = new MenuItem { Header = "Folder Symbol" };
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 5, Margin = new Thickness(-6, 0, 0, 0) };
            foreach (var g in FolderLook.Symbols)
            {
                string glyph = g;
                var b = new Button { Content = new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = 17 }, Width = 34, Height = 32, Style = (Style)Application.Current.Resources["Flat"] };
                b.SetResourceReference(Control.ForegroundProperty, "Fg");
                b.Click += delegate { cm.IsOpen = false; foreach (var d in dirs) FolderLook.SetSymbol(d, glyph); };
                grid.Children.Add(b);
            }
            menu.Items.Add(new MenuItem { Header = grid, StaysOpenOnClick = true });
            var none = new MenuItem { Header = "No Symbol" };
            none.Click += delegate { foreach (var d in dirs) FolderLook.SetSymbol(d, null); };
            menu.Items.Add(none);
            return menu;
        }

        // The path bar: each folder from the drive down to this one, with its icon; click one to go there.
        void UpdateCrumbs()
        {
            crumbs.Children.Clear();
            var steps = new List<string>();
            if (Virtual(path)) steps.Add(path);
            else
            {
                try { for (var d = new DirectoryInfo(path); d != null; d = d.Parent) steps.Insert(0, d.FullName); }
                catch { steps.Add(path); }
            }
            for (int i = 0; i < steps.Count; i++)
            {
                string step = steps[i];
                bool last = i == steps.Count - 1;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (!Virtual(step))
                    row.Children.Add(new Image { Source = System.IO.Path.GetPathRoot(step) == step ? null : FolderLook.Icon(step), Width = 16, Height = 16, Margin = new Thickness(0, 0, 4, 0) });
                string name = Virtual(step) ? LocationName(step) : (System.IO.Path.GetPathRoot(step) == step ? step.TrimEnd('\\') : System.IO.Path.GetFileName(step));
                var label = Text(name, 12, last ? "Fg" : "FgDim");
                label.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(label);
                var b = new Button { Content = row, Style = (Style)Application.Current.Resources["Flat"], Padding = new Thickness(5, 2, 5, 2), IsEnabled = !last || true };
                b.Click += delegate { if (!string.Equals(step, path, StringComparison.OrdinalIgnoreCase)) Go(step); };
                crumbs.Children.Add(b);
                if (!last) { var chev = Text("", 8, "FgDim"); chev.FontFamily = Theme.Glyphs; chev.VerticalAlignment = VerticalAlignment.Center; chev.Margin = new Thickness(1, 1, 1, 0); crumbs.Children.Add(chev); }
            }
        }

        // The Tags view: everything carrying a colour tag.
        List<Entry> TaggedEntries(int tag)
        {
            var list = new List<Entry>();
            foreach (var p in FolderLook.Tagged(tag))
            {
                bool isDir = Directory.Exists(p);
                var entry = new Entry { Name = System.IO.Path.GetFileName(p.TrimEnd('\\')), Path = p, IsDir = isDir, Kind = isDir ? "Folder" : System.IO.Path.GetExtension(p).TrimStart('.').ToUpperInvariant() + " file" };
                if (entry.Name.Length == 0) entry.Name = p;
                try { var fi = isDir ? (FileSystemInfo)new DirectoryInfo(p) : new FileInfo(p); entry.Modified = fi.LastWriteTime.ToString("g"); if (!isDir) entry.SizeText = Size(((FileInfo)fi).Length); } catch { }
                list.Add(entry);
            }
            return list;
        }
    }
}
