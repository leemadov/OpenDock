using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OpenDock
{
    // System Settings search, like macOS: it finds the options themselves -- every switch, slider, menu choice, colour,
    // button and note in every pane -- not just the pane names. Results list each match with where it lives
    // ("Appearance › Icons › Dock icon style"); picking one opens that pane, scrolls to the option and makes it glow.
    //
    // The index is made by building each pane off screen once (slow system reads, which panes do through Load, and
    // thumbnails are skipped while indexing), then reading the text of what was built.
    partial class SettingsWindow
    {
        internal const string HeadingTag = "settings-heading", RowTag = "settings-row";
        static bool indexing;

        internal enum HitKind { Pane, Heading, Option, Choice, Button, Info, Note }
        internal class Hit
        {
            public string Text, Context, Section, Pane; public HitKind Kind; public int Order;
            public string Where(string parent)
            {
                var parts = new List<string>();
                if (parent != null) parts.Add(parent);
                if (Kind != HitKind.Pane) parts.Add(Pane);
                if (Section != null && Section != Text) parts.Add(Section);
                if (Context != null && Context != Text) parts.Add(Context);
                parts = parts.Where((p, i) => i == 0 || p != parts[i - 1]).ToList();   // "Appearance › Appearance" -> once
                return string.Join(" › ", parts);
            }
        }

        // What some panes only show once a slow system read finishes (so it isn't in the off-screen build).
        static readonly Dictionary<string, string> Extras = new Dictionary<string, string>
        {
            { "Wi-Fi", "Wi-Fi|Other Networks|Disconnect|Join|Password" },
            { "Bluetooth", "Bluetooth|My Devices|Paired" },
            { "Sound", "Output volume|Mute|Output|Input|Input volume|Output device|Input device|Microphone|Speakers" },
            { "Battery", "Power Mode|Power plan|Balanced|Best performance|Power saver" },
            { "Lock Screen", "Turn display off after|Put the computer to sleep after|Lock this PC now|Lock Screen|Sleep" },
            { "Privacy & Security", "Security|Antivirus" },
            { "Users & Groups", "Accounts|Administrator" },
            { "Printers & Scanners", "Printers|Default printer" },
            { "Displays", "Resolution|Refresh rate|Scale|Brightness" },
            { "Notifications", "Application Notifications" },
            { "About", "Name|Chip|Memory|Graphics|Serial number|Windows|Storage" },
            { "Login Items", "Open at Login|Startup apps" },
        };

        List<Hit> index;
        bool showingResults;
        List<Hit> shown = new List<Hit>();

        class Ctx { public string Pane, Section; public int Order; public List<Hit> Into; public HashSet<string> Seen; }

        static bool Meaningful(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.Length < 2) return false;
            return s.Any(c => char.IsLetter(c) && (c < '' || c > ''));   // not just a glyph or a number
        }

        static void Add(Ctx ctx, string text, string context, HitKind kind)
        {
            if (!Meaningful(text)) return;
            text = text.Trim();
            if (context != null) context = context.Trim();
            string key = ctx.Pane + "|" + ctx.Section + "|" + text + "|" + context;
            if (!ctx.Seen.Add(key)) return;
            ctx.Into.Add(new Hit { Text = text, Context = context, Section = ctx.Section, Pane = ctx.Pane, Kind = kind, Order = ctx.Order++ });
        }

        static string TextOf(TextBlock t)
        {
            if (!string.IsNullOrEmpty(t.Text)) return t.Text;
            return string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text));
        }

        // The label of a row. Rows are a DockPanel with the control (or value) docked right and the label filling the
        // rest -- the panel's last child -- or a grid/stack with the label first.
        static string LabelOf(DependencyObject d)
        {
            var t = d as TextBlock;
            if (t != null) { string s = TextOf(t); return Meaningful(s) ? s.Trim() : null; }
            if (d is ComboBox || d is Button) return null;
            var dock = d as DockPanel;
            if (dock != null && dock.LastChildFill && dock.Children.Count > 0)
            {
                string s = LabelOf(dock.Children[dock.Children.Count - 1]);
                if (s != null) return s;
            }
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            {
                string s = LabelOf(c);
                if (s != null) return s;
            }
            return null;
        }

        static void Walk(DependencyObject d, Ctx ctx, string row)
        {
            var fe = d as FrameworkElement;
            if (fe != null && fe.Visibility == Visibility.Collapsed) return;
            if (fe != null && RowTag.Equals(fe.Tag)) row = LabelOf(d);
            var tip = fe != null ? fe.ToolTip as string : null;
            if (tip != null) Add(ctx, tip, row, HitKind.Choice);   // colour swatches, wallpapers...
            var t = d as TextBlock;
            if (t != null)
            {
                string s = TextOf(t);
                if (HeadingTag.Equals(t.Tag)) { if (Meaningful(s)) { ctx.Section = s.Trim(); Add(ctx, s, null, HitKind.Heading); } return; }
                bool inButton = LogicalTreeHelper.GetParent(t) is Button;
                bool note = row == null && s != null && s.Length > 70;
                Add(ctx, s, inButton || note || s == row ? null : row, inButton ? HitKind.Button : note ? HitKind.Note : s == row ? HitKind.Option : HitKind.Info);
                return;
            }
            var combo = d as ComboBox;
            if (combo != null)
            {
                foreach (var item in combo.Items) Add(ctx, item as string ?? (item != null ? item.ToString() : null), row, HitKind.Choice);
                return;
            }
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) Walk(c, ctx, row);
        }

        // Which sidebar category a pane belongs to (General's sub-panes belong to General).
        Cat CatOf(string pane)
        {
            var r = rows.FirstOrDefault(x => x.Cat.Name == pane);
            return r != null ? r.Cat : rows.First(x => x.Cat.Name == "General").Cat;
        }

        void BuildIndex()
        {
            var list = new List<Hit>();
            var panes = rows.Select(r => new KeyValuePair<string, Action<StackPanel>>(r.Cat.Name, r.Cat.Build)).Concat(sub).ToList();
            var ctx = new Ctx { Into = list, Seen = new HashSet<string>() };
            indexing = true;
            try
            {
                foreach (var p in panes)
                {
                    ctx.Pane = p.Key; ctx.Section = null;
                    Add(ctx, p.Key, null, HitKind.Pane);
                    var body = new StackPanel();
                    try { p.Value(body); Walk(body, ctx, null); }
                    catch (Exception ex) { App.Log(ex); }
                    string extra;
                    ctx.Section = null;
                    if (Extras.TryGetValue(p.Key, out extra)) foreach (var e in extra.Split('|')) Add(ctx, e, null, HitKind.Option);
                }
            }
            finally { indexing = false; }
            index = list;
        }

        static string Norm(string s)
        {
            if (s == null) return "";
            s = s.ToLowerInvariant().Replace('…', ' ').Replace("...", " ").Replace('&', ' ').Replace('·', ' ');
            return string.Join(" ", s.Split(new[] { ' ', '\t', '(', ')', ',', '/', '-' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static bool AtWordStart(string text, string token)
        {
            for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0; i = text.IndexOf(token, i + 1, StringComparison.Ordinal))
                if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) return true;
            return false;
        }

        // How well an entry matches: the exact option first, then options starting with or containing the words,
        // then entries that match only together with where they live.
        internal static int Score(Hit h, string q)
        {
            string text = Norm(h.Text), all = Norm(h.Text + " " + h.Context + " " + h.Section + " " + h.Pane);
            q = Norm(q);
            if (q.Length == 0) return 0;
            var tokens = q.Split(' ');
            int s;
            if (text == q) s = 1000;
            else if (text.StartsWith(q, StringComparison.Ordinal)) s = 800;
            else if (AtWordStart(text, q)) s = 700;
            else if (tokens.All(t => AtWordStart(text, t))) s = 600;
            else if (text.Contains(q)) s = 500;
            else if (tokens.All(t => text.Contains(t))) s = 400;
            else
            {
                // matched only with where it lives ("hot corner" -> the four corner settings): the settings
                // themselves, not every menu item or value inside them
                if (h.Kind == HitKind.Choice || h.Kind == HitKind.Info || h.Kind == HitKind.Note) return 0;
                if (tokens.All(t => AtWordStart(all, t))) s = 250;
                else if (tokens.All(t => all.Contains(t))) s = 150;
                else return 0;
            }
            switch (h.Kind)
            {
                case HitKind.Pane: s += 40; break;
                case HitKind.Option: case HitKind.Choice: case HitKind.Info: s += 20; break;
                case HitKind.Heading: s += 10; break;
                case HitKind.Note: s -= 120; break;
            }
            return s - Math.Min(80, text.Length / 2);   // shorter, closer labels first
        }

        void Filter(string q)
        {
            if (q.Length == 0)
            {
                foreach (var r in rows) r.Box.Visibility = Visibility.Visible;
                foreach (var s in sections) s.Visibility = Visibility.Visible;
                if (showingResults) { showingResults = false; Show(current, false); }
                return;
            }
            if (index == null) BuildIndex();
            shown = index.Select(h => new { h, s = Score(h, q) }).Where(x => x.s > 0).OrderByDescending(x => x.s).ThenBy(x => x.h.Order).Select(x => x.h).Take(80).ToList();
            var cats = new HashSet<string>(shown.Select(h => CatOf(h.Pane).Name));
            foreach (var r in rows) r.Box.Visibility = cats.Contains(r.Cat.Name) || Norm(r.Cat.Name).Contains(Norm(q)) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var s in sections) s.Visibility = s.Children.Cast<UIElement>().Any(e => e.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
            ShowResults(q);
        }

        void ShowResults(string q)
        {
            showingResults = true;
            title.Text = "Search"; Title = "System Settings";
            var body = new StackPanel { Margin = new Thickness(24, 0, 24, 28) };
            if (shown.Count == 0)
            {
                var none = Text("No Results for “" + q + "”", 17, "FgDim", FontWeights.SemiBold);
                none.HorizontalAlignment = HorizontalAlignment.Center; none.Margin = new Thickness(0, 60, 0, 6);
                body.Children.Add(none);
                var tip = Text("Try a setting's name, like “magnification” or “dark”.", 12, "FgDim");
                tip.HorizontalAlignment = HorizontalAlignment.Center;
                body.Children.Add(tip);
                paneHost.Content = body;
                return;
            }
            var head = Text(shown.Count + (shown.Count == 1 ? " result" : " results") + (shown.Count == 80 ? "+" : ""), 12, "FgDim");
            head.Margin = new Thickness(4, 4, 0, 8);
            body.Children.Add(head);
            var g = Group(body);
            foreach (var h in shown) Row(g, ResultRow(h, q)).Tag = null;
            paneHost.Content = body;
            paneHost.ScrollToTop();
        }

        FrameworkElement ResultRow(Hit h, string q)
        {
            var cat = CatOf(h.Pane);
            var tile = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = new LinearGradientBrush(Lighter(cat.Color), Hex(cat.Color), 90), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            tile.Child = new TextBlock { Text = cat.Glyph, FontFamily = Theme.Glyphs, FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(tile, Dock.Left);
            var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Mark(name, h.Kind == HitKind.Note && h.Text.Length > 110 ? h.Text.Substring(0, 107) + "…" : h.Text, q);
            string where = h.Where(cat.Name != h.Pane ? cat.Name : null);
            var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lines.Children.Add(name);
            if (where.Length > 0) { var w = Text(where, 11.5, "FgDim"); w.TextTrimming = TextTrimming.CharacterEllipsis; w.Margin = new Thickness(0, 1, 0, 0); lines.Children.Add(w); }
            var go = Text("", 11, "FgDim"); go.FontFamily = Theme.Glyphs; go.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(go, Dock.Right);
            var d = new DockPanel { Background = Brushes.Transparent, Cursor = Cursors.Hand };
            d.Children.Add(tile); d.Children.Add(go); d.Children.Add(lines);
            d.MouseEnter += delegate { d.SetResourceReference(Panel.BackgroundProperty, "Hover"); };
            d.MouseLeave += delegate { d.Background = Brushes.Transparent; };
            d.MouseLeftButtonUp += delegate { Open(h); };
            return d;
        }

        // The text with the words you typed in bold.
        static void Mark(TextBlock into, string text, string q)
        {
            var bold = new bool[text.Length];
            string lower = text.ToLowerInvariant();
            foreach (var tok in Norm(q).Split(' ').Where(x => x.Length > 0))
                for (int i = lower.IndexOf(tok, StringComparison.Ordinal); i >= 0; i = lower.IndexOf(tok, i + tok.Length, StringComparison.Ordinal))
                    for (int k = i; k < i + tok.Length && k < bold.Length; k++) bold[k] = true;
            int at = 0;
            while (at < text.Length)
            {
                int end = at; while (end < text.Length && bold[end] == bold[at]) end++;
                var run = new Run(text.Substring(at, end - at));
                if (bold[at]) run.FontWeight = FontWeights.Bold;
                into.Inlines.Add(run);
                at = end;
            }
        }

        void OpenFirstResult()
        {
            if (showingResults && shown.Count > 0) Open(shown[0]);
        }

        // Opens the pane a result lives in and makes the option glow.
        void Open(Hit h)
        {
            showingResults = false;
            Show(h.Pane, true);
            if (h.Kind == HitKind.Pane) return;
            int tries = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += delegate
            {
                // panes that load in the background get a little longer to show the option
                var body = paneHost.Content as StackPanel;
                var target = body != null ? Find(body, h) : null;
                if (target == null && ++tries < 40) return;
                t.Stop();
                if (target == null) return;
                Glow(target);
                // a menu choice: open the menu with that choice highlighted, ready to pick (Enter or a click)
                var combo = target as ComboBox;
                if (combo != null && combo.IsEnabled)
                    Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                    {
                        combo.IsDropDownOpen = true;
                        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                        {
                            var item = combo.Items.Cast<object>().FirstOrDefault(i => h.Text.Equals(i as string ?? (i != null ? i.ToString() : null)));
                            var box = item != null ? combo.ItemContainerGenerator.ContainerFromItem(item) as ComboBoxItem : null;
                            if (box != null) { box.BringIntoView(); box.Focus(); }
                        }));
                    }));
            };
            t.Start();
        }

        static FrameworkElement Find(DependencyObject d, Hit h)
        {
            var fe = d as FrameworkElement;
            if (fe != null && fe.Visibility == Visibility.Collapsed) return null;
            if (fe != null && h.Text.Equals(fe.ToolTip as string)) return fe;
            var t = d as TextBlock;
            if (t != null) { string s = TextOf(t); return s != null && s.Trim() == h.Text ? t : null; }
            var combo = d as ComboBox;
            if (combo != null) return combo.Items.Cast<object>().Any(i => h.Text.Equals(i as string ?? (i != null ? i.ToString() : null))) ? combo : null;
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            {
                var f = Find(c, h);
                if (f != null) return f;
            }
            return null;
        }

        void Glow(FrameworkElement target)
        {
            // light up the whole row the option sits in (or the option itself, outside a row)
            FrameworkElement glow = target;
            for (DependencyObject p = target; p != null; p = LogicalTreeHelper.GetParent(p) ?? VisualTreeHelper.GetParent(p))
            {
                var fe = p as FrameworkElement;
                if (fe != null && RowTag.Equals(fe.Tag)) { glow = fe; break; }
                if (p == paneHost) break;
            }
            glow.BringIntoView(new Rect(0, -40, Math.Max(1, glow.ActualWidth), glow.ActualHeight + 80));
            var accent = TryFindResource("Accent") as SolidColorBrush;
            var c = accent != null ? accent.Color : Color.FromRgb(0x0A, 0x84, 0xFF);
            var brush = new SolidColorBrush(Color.FromArgb(0x60, c.R, c.G, c.B));
            var border = glow as Border; var panel = glow as Panel; var control = glow as Control;
            Brush before = border != null ? border.Background : panel != null ? panel.Background : control != null ? control.Background : null;
            if (border != null) border.Background = brush; else if (panel != null) panel.Background = brush; else if (control != null) control.Background = brush; else return;
            var fade = new ColorAnimation { To = Color.FromArgb(0, c.R, c.G, c.B), BeginTime = TimeSpan.FromMilliseconds(1100), Duration = TimeSpan.FromMilliseconds(900) };
            fade.Completed += delegate
            {
                if (border != null && border.Background == brush) border.Background = before;
                else if (panel != null && panel.Background == brush) panel.Background = before;
                else if (control != null && control.Background == brush) control.Background = before;
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, fade);
        }
    }
}
