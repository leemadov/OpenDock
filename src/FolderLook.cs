using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OpenDock
{
    // Finder's folders as macOS 26 draws them: a two-layer folder in a tag colour (blue by default) with an embossed
    // symbol on the front -- the special folders' own symbols, or one you pick. Colour and symbol are kept per folder
    // (folders.txt), and the colour doubles as the folder's tag: a dot by its name and an entry under Tags.
    static class FolderLook
    {
        public static readonly string[] Names = { "Red", "Orange", "Yellow", "Green", "Blue", "Purple", "Gray" };
        public static readonly string[] Hex = { "#EC4D47", "#F2913E", "#F5C842", "#5DC25F", "#3D84F5", "#B13FD6", "#8C8C93" };
        const string Default = "#6DB6EE";   // macOS's own folder blue

        // Symbols to choose from: Segoe Fluent Icons, drawn in one tone like macOS's SF Symbols on folders.
        public static readonly string[] Symbols = { "", "", "", "", "", "", "", "", "", "",
            "", "", "", "", "", "", "", "", "", "" };

        class Look { public int Color = -1; public string Symbol; }
        static Dictionary<string, Look> looks;
        public static event Action Changed;
        static string File { get { return System.IO.Path.Combine(Settings.DataDir, "folders.txt"); } }

        static Dictionary<string, Look> All
        {
            get
            {
                if (looks != null) return looks;
                var read = new Dictionary<string, Look>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (System.IO.File.Exists(File))
                        foreach (var line in System.IO.File.ReadAllLines(File))
                        {
                            var p = line.Split('\t');
                            if (p.Length < 3 || p[0].Length == 0) continue;
                            int c; int.TryParse(p[1], out c);
                            read[Norm(p[0])] = new Look { Color = c >= 0 && c < Names.Length ? c : -1, Symbol = p[2].Length > 0 ? p[2] : null };
                        }
                }
                catch (Exception ex)
                {
                    // couldn't read it this time (locked by a backup or sync tool...): try again next time, and don't
                    // cache the empty list -- the next edit would save it over every tag and symbol you have
                    App.Log(ex); unreadable = true;
                    return read;
                }
                unreadable = false;
                looks = read;
                return looks;
            }
        }

        static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { path = System.IO.Path.GetFullPath(path); } catch { }   // one spelling per folder: slashes, ".." and so on
            return path.Length > 3 ? path.TrimEnd('\\') : path;
        }

        static bool unreadable;

        static void Save()
        {
            try
            {
                if (looks == null || unreadable) { App.Log("folders.txt couldn't be read; not overwriting it"); return; }
                Directory.CreateDirectory(Settings.DataDir);
                var lines = All.Where(kv => kv.Value.Color >= 0 || kv.Value.Symbol != null).Select(kv => kv.Key + "\t" + kv.Value.Color + "\t" + (kv.Value.Symbol ?? ""));
                string tmp = File + ".tmp";
                System.IO.File.WriteAllLines(tmp, lines);
                if (System.IO.File.Exists(File)) System.IO.File.Replace(tmp, File, null); else System.IO.File.Move(tmp, File);
            }
            catch (Exception ex) { App.Log(ex); }
            if (Changed != null) Changed();
        }

        public static int ColorOf(string path) { Look l; return path != null && All.TryGetValue(Norm(path), out l) ? l.Color : -1; }
        public static string SymbolOf(string path) { Look l; return path != null && All.TryGetValue(Norm(path), out l) ? l.Symbol : null; }

        public static void SetColor(string path, int color)
        {
            Look l; if (!All.TryGetValue(Norm(path), out l)) All[Norm(path)] = l = new Look();
            l.Color = color; Save();
        }

        public static void SetSymbol(string path, string symbol)
        {
            Look l; if (!All.TryGetValue(Norm(path), out l)) All[Norm(path)] = l = new Look();
            l.Symbol = string.IsNullOrEmpty(symbol) ? null : symbol; Save();
        }

        // Everything tagged with a colour that still exists.
        public static List<string> Tagged(int color)
        {
            return All.Where(kv => kv.Value.Color == color).Select(kv => kv.Key).Where(p => Directory.Exists(p) || System.IO.File.Exists(p)).OrderBy(p => p).ToList();
        }

        public static Brush TagBrush(string path)
        {
            int c = ColorOf(path);
            return c < 0 ? null : Theme.B(Hex[c]);
        }

        // The special folders keep macOS's symbols.
        static string Special(string path)
        {
            Func<Environment.SpecialFolder, string> sf = f => { try { return Norm(Environment.GetFolderPath(f)); } catch { return ""; } };
            string p = Norm(path);
            if (p.Length == 0) return null;
            if (string.Equals(p, sf(Environment.SpecialFolder.DesktopDirectory), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, sf(Environment.SpecialFolder.MyDocuments), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, Norm(Launcher.Downloads), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, sf(Environment.SpecialFolder.MyPictures), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, sf(Environment.SpecialFolder.MyMusic), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, sf(Environment.SpecialFolder.MyVideos), StringComparison.OrdinalIgnoreCase)) return "";
            if (string.Equals(p, sf(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)) return "";
            return null;
        }

        // The colour a folder is drawn in: its tag, else blue -- or, with "one colour everywhere", the icon colour.
        public static Color ColorFor(string path)
        {
            if (IconLook.OneColor) return IconLook.Accent;
            int c = ColorOf(path);
            return (Color)ColorConverter.ConvertFromString(c >= 0 ? Hex[c] : Default);
        }

        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>();

        public static ImageSource Icon(string path)
        {
            var color = ColorFor(path);
            string symbol = SymbolOf(path) ?? Special(path);
            string key = color + "|" + symbol + "|" + Theme.Dark;
            ImageSource img;
            if (cache.TryGetValue(key, out img)) return img;
            try { img = Draw(color, symbol, Theme.Dark); }
            catch (Exception ex) { App.Log(ex); img = null; }
            if (cache.Count > 200) cache.Clear();
            cache[key] = img;
            return img;
        }

        static Color Shade(Color c, double k) { return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k)); }
        static Color Lift(Color c, double k) { return Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k)); }

        // A 128 px folder: the back with its tab, then the front panel a little lower, lit from above.
        public static BitmapSource Draw(Color c, string symbol, bool dark)
        {
            const double S = 128;
            var root = new Grid { Width = S, Height = S };
            var canvas = new Canvas { Width = S, Height = S };
            // the back and its tab
            var back = new PathGeometry();
            var f = new PathFigure { StartPoint = new Point(10, 30), IsClosed = true };
            f.Segments.Add(new ArcSegment(new Point(18, 22), new Size(8, 8), 0, false, SweepDirection.Clockwise, true));
            f.Segments.Add(new LineSegment(new Point(46, 22), true));
            f.Segments.Add(new BezierSegment(new Point(51, 22), new Point(52, 29), new Point(58, 29), true));
            f.Segments.Add(new LineSegment(new Point(110, 29), true));
            f.Segments.Add(new ArcSegment(new Point(118, 37), new Size(8, 8), 0, false, SweepDirection.Clockwise, true));
            f.Segments.Add(new LineSegment(new Point(118, 96), true));
            f.Segments.Add(new LineSegment(new Point(10, 96), true));
            back.Figures.Add(f);
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = back, Fill = new LinearGradientBrush(Shade(c, 0.86), Shade(c, 0.80), 90) });
            // the front panel
            var front = new Border
            {
                Width = 108, Height = 70, CornerRadius = new CornerRadius(8),
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Lift(c, 0.16), 0), new GradientStop(c, 0.55), new GradientStop(Shade(c, 0.94), 1) }, new Point(0, 0), new Point(0, 1)),
                BorderThickness = new Thickness(0, 1.2, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.5, Direction = 270, Opacity = 0.22 }
            };
            Canvas.SetLeft(front, 10); Canvas.SetTop(front, 38);
            canvas.Children.Add(front);
            root.Children.Add(canvas);
            // the symbol, pressed into the front: a darker tone of the folder with a light edge under it
            if (!string.IsNullOrEmpty(symbol))
            {
                var glyph = new TextBlock
                {
                    Text = symbol, FontFamily = Theme.Glyphs, FontSize = 40, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(235, Shade(c, 0.56).R, Shade(c, 0.56).G, Shade(c, 0.56).B)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 52, 0, 0),
                    Effect = new DropShadowEffect { BlurRadius = 0.5, ShadowDepth = 1.2, Direction = 270, Opacity = 0.55, Color = Colors.White }
                };
                root.Children.Add(glyph);
            }
            var img = Icons.Render(root, (int)S, (int)S);
            img.Freeze();
            return img;
        }
    }
}
