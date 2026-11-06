using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OpenDock
{
    // macOS 26/27 icon styles for the Dock: Default, Tinted (every icon in one colour) and Clear (Liquid Glass).
    //
    // macOS builds these from an icon's layers: a background and the foreground symbol. The system then draws
    // the glass itself -- a specular rim catching the light, an inner edge that gives the glass thickness, a sheen,
    // a soft shadow under the symbol -- and recolours the layers for the Tinted and Clear looks, in light and dark.
    // Windows icons are flat pictures, so the two layers are recovered first: on a tile-shaped icon the background
    // is the tile's own colour (gradients included, estimated from its edges) and the symbol is whatever differs
    // from it; a free-shaped icon (a round logo, a folder) is all symbol, placed on a new tile.
    static class IconLook
    {
        public static readonly string[] Colors = { "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#64D2FF", "#8E8E93" };
        public static readonly string[] Names = { "Blue", "Indigo", "Purple", "Pink", "Red", "Orange", "Yellow", "Green", "Teal", "Graphite" };

        public static bool Tinted { get { return Settings.IconStyle == 1; } }
        public static bool Clear { get { return Settings.IconStyle == 2; } }
        public static string Key
        {
            get
            {
                string k = Settings.IconStyle + "|" + Settings.IconTint + "|" + Theme.Dark;
                if (FromWallpaper) foreach (var c in Palette()) k += "|" + c;
                return k;
            }
        }

        static readonly ConditionalWeakTable<ImageSource, Holder> cache = new ConditionalWeakTable<ImageSource, Holder>();
        class Holder { public string Key; public ImageSource Image; }

        public static Color TintColor
        {
            get { try { return (Color)ColorConverter.ConvertFromString(Settings.IconTint); } catch { return (Color)ColorConverter.ConvertFromString(Colors[0]); } }
        }

        // ---------- Wallpaper colours: every icon gets its own colour, sweeping through all the wallpaper's colours ----------

        public const string WallpaperTint = "wallpaper";
        public static bool FromWallpaper { get { return Tinted && string.Equals(Settings.IconTint, WallpaperTint, StringComparison.OrdinalIgnoreCase); } }

        static Color[] palette; static byte[] paletteOf;

        // The current wallpaper's colours, in hue order (greys last). Falls back to the standard swatches.
        public static Color[] Palette()
        {
            if (Wallpaper.Flat == null) return Array.ConvertAll(Colors, h => (Color)ColorConverter.ConvertFromString(h));
            if (paletteOf != Wallpaper.Flat)
            {
                try { palette = Extract(Wallpaper.Flat, Wallpaper.FlatW, Wallpaper.FlatH, 8); }
                catch (Exception ex) { App.Log(ex); palette = new[] { TintColor }; }
                paletteOf = Wallpaper.Flat;
            }
            return palette;
        }

        // The colours of a picture (Bgra): k-means seeded with far-apart pixels, so small vivid accents survive next to
        // big areas; near-duplicates merge; each colour is brought into a range that still reads as a Tinted icon.
        public static Color[] Extract(byte[] bgra, int w, int h, int k)
        {
            var pts = new List<double[]>();
            int step = Math.Max(1, (int)Math.Sqrt(w * h / 6000.0));
            for (int y = 0; y < h; y += step)
                for (int x = 0; x < w; x += step) { int o = (y * w + x) * 4; pts.Add(new double[] { bgra[o + 2], bgra[o + 1], bgra[o] }); }
            if (pts.Count == 0) return new[] { TintColor };
            Func<double[], double[], double> d2 = (a, b) => (a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]);
            // seeds: the average, then repeatedly the pixel farthest from every seed so far
            var centres = new List<double[]>();
            var avg = new double[3]; foreach (var p in pts) { avg[0] += p[0]; avg[1] += p[1]; avg[2] += p[2]; }
            centres.Add(new[] { avg[0] / pts.Count, avg[1] / pts.Count, avg[2] / pts.Count });
            var near = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++) near[i] = d2(pts[i], centres[0]);
            while (centres.Count < k)
            {
                int far = 0; for (int i = 1; i < pts.Count; i++) if (near[i] > near[far]) far = i;
                if (near[far] < 24 * 24) break;
                centres.Add((double[])pts[far].Clone());
                for (int i = 0; i < pts.Count; i++) near[i] = Math.Min(near[i], d2(pts[i], centres[centres.Count - 1]));
            }
            var owner = new int[pts.Count]; var count = new int[centres.Count];
            for (int pass = 0; pass < 12; pass++)
            {
                var sum = new double[centres.Count, 3]; count = new int[centres.Count];
                for (int i = 0; i < pts.Count; i++)
                {
                    int best = 0; double bd = double.MaxValue;
                    for (int c = 0; c < centres.Count; c++) { double dd = d2(pts[i], centres[c]); if (dd < bd) { bd = dd; best = c; } }
                    owner[i] = best; count[best]++; sum[best, 0] += pts[i][0]; sum[best, 1] += pts[i][1]; sum[best, 2] += pts[i][2];
                }
                for (int c = 0; c < centres.Count; c++) if (count[c] > 0) centres[c] = new[] { sum[c, 0] / count[c], sum[c, 1] / count[c], sum[c, 2] / count[c] };
            }
            // keep every colour covering at least 0.5% of the picture; merge colours that would look the same on an icon
            var kept = new List<double[]>();
            for (int c = 0; c < centres.Count; c++)
            {
                if (count[c] < pts.Count * 0.005) continue;
                var fit = Fit(centres[c]);
                if (kept.Any(o => d2(o, fit) < 28 * 28)) continue;
                kept.Add(fit);
            }
            if (kept.Count == 0) kept.Add(Fit(centres[0]));
            var colours = kept.Select(c => Color.FromRgb((byte)Math.Round(c[0]), (byte)Math.Round(c[1]), (byte)Math.Round(c[2]))).ToList();
            // colours in hue order, starting after the widest gap in the colour wheel so the sweep along the Dock never
            // jumps across it (e.g. indigo, pink, red, orange, yellow rather than red, orange, yellow, indigo, pink); greys last
            double hh, ss, ll;
            Func<Color, double> hueOf = c => { ToHsl(c, out hh, out ss, out ll); return hh; };
            Func<Color, bool> grey = c => { ToHsl(c, out hh, out ss, out ll); return ss < 0.1; };
            var hues = colours.Where(c => !grey(c)).OrderBy(hueOf).ToList();
            if (hues.Count > 1)
            {
                int start = 0; double widest = -1;
                for (int i = 0; i < hues.Count; i++)
                {
                    double gap = (hueOf(hues[i]) - hueOf(hues[(i + hues.Count - 1) % hues.Count]) + 360) % 360;
                    if (gap > widest) { widest = gap; start = i; }
                }
                hues = hues.Skip(start).Concat(hues.Take(start)).ToList();
            }
            return hues.Concat(colours.Where(grey).OrderBy(c => { ToHsl(c, out hh, out ss, out ll); return ll; })).ToArray();
        }

        // A wallpaper colour made usable as a tint: very dark or very pale colours keep their hue but move to the middle.
        static double[] Fit(double[] rgb)
        {
            double h, s, l;
            ToHsl(Color.FromRgb((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]), out h, out s, out l);
            l = Math.Max(0.34, Math.Min(0.64, l));
            var c = FromHsl(h, s, l);
            return new double[] { c.R, c.G, c.B };
        }

        // The colour at position t (0 = first icon, 1 = last) along the wallpaper's colours, blended through hue so
        // neighbouring icons shade from one wallpaper colour into the next.
        public static Color ColorAt(double t)
        {
            var p = Palette();
            if (p.Length == 1) return p[0];
            t = Math.Max(0, Math.Min(1, t)) * (p.Length - 1);
            int i = Math.Min(p.Length - 2, (int)Math.Floor(t));
            double f = t - i;
            double h1, s1, l1, h2, s2, l2;
            ToHsl(p[i], out h1, out s1, out l1); ToHsl(p[i + 1], out h2, out s2, out l2);
            if (s1 < 0.1 || s2 < 0.1)
                return Color.FromRgb((byte)(p[i].R + (p[i + 1].R - p[i].R) * f), (byte)(p[i].G + (p[i + 1].G - p[i].G) * f), (byte)(p[i].B + (p[i + 1].B - p[i].B) * f));
            double dh = h2 - h1; if (dh > 180) dh -= 360; if (dh < -180) dh += 360;
            return FromHsl((h1 + dh * f + 360) % 360, s1 + (s2 - s1) * f, l1 + (l2 - l1) * f);
        }

        public static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2; h = 0; s = 0;
            if (max - min < 1e-9) return;
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) * 60;
            else if (max == g) h = ((b - r) / d + 2) * 60;
            else h = ((r - g) / d + 4) * 60;
        }

        public static Color FromHsl(double h, double s, double l)
        {
            Func<double, double, double, double> hue = (p, q, t) =>
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1 / 6.0) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
                return p;
            };
            double r, g, b;
            if (s < 1e-9) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q, hk = h / 360;
                r = hue(p, q, hk + 1 / 3.0); g = hue(p, q, hk); b = hue(p, q, hk - 1 / 3.0);
            }
            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }

        // A Dock icon (as the Dock would show it) in the current style. `at` is the icon's place along the Dock
        // (0 = first, 1 = last), which picks its colour when the icons take the wallpaper's colours.
        public static ImageSource Apply(ImageSource src, double at = 0.5)
        {
            if ((!Tinted && !Clear) || src == null) return src;
            var raw = Icons.RawOf(src) ?? src as BitmapSource;
            if (raw == null) return src;
            var tint = FromWallpaper ? ColorAt(at) : TintColor;
            string key = Key + "|" + tint;
            Holder h;
            if (cache.TryGetValue(src, out h) && h.Key == key) return h.Image;
            ImageSource result;
            try { result = Style(raw, Settings.IconStyle, tint, Theme.Dark); }
            catch (Exception ex) { App.Log(ex); result = src; }
            cache.Remove(src);
            cache.Add(src, new Holder { Key = key, Image = result });
            return result;
        }

        const int S = 256;

        // style 1 = Tinted, 2 = Clear. Returns a 256 px Dock icon with its shadow, like the Default tiles.
        public static BitmapSource Style(BitmapSource raw, int style, Color tint, bool dark)
        {
            double T = S * 0.875, k = S / 256.0;
            int N = (int)T;
            bool tile = IsTile(raw);
            // ---- 1. the icon on an N x N canvas: tiles fill it, free shapes sit in the middle like a symbol
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                if (tile) dc.DrawImage(raw, new Rect(0, 0, N, N));
                else { double g = N * 0.66; dc.DrawImage(raw, new Rect((N - g) / 2, (N - g) / 2, g, g)); }
            }
            RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
            var canvas = new RenderTargetBitmap(N, N, 96, 96, PixelFormats.Pbgra32);
            canvas.Render(dv);
            var px = new byte[N * N * 4];
            canvas.CopyPixels(px, N * 4, 0);
            // straight colour + alpha
            var r = new double[N * N]; var gr = new double[N * N]; var b = new double[N * N]; var a = new double[N * N];
            for (int p = 0; p < N * N; p++)
            {
                double al = px[p * 4 + 3] / 255.0; a[p] = al;
                if (al > 0) { b[p] = px[p * 4] / 255.0 / al; gr[p] = px[p * 4 + 1] / 255.0 / al; r[p] = px[p * 4 + 2] / 255.0 / al; }
            }
            // ---- 2. the symbol's mask and its shading
            var mask = new double[N * N];
            if (tile) SymbolOnTile(N, r, gr, b, a, mask); else for (int p = 0; p < N * N; p++) mask[p] = a[p];
            var lum = new double[N * N];
            var levels = new List<double>();
            for (int p = 0; p < N * N; p++)
            {
                lum[p] = 0.2126 * r[p] + 0.7152 * gr[p] + 0.0722 * b[p];
                if (mask[p] > 0.5) levels.Add(lum[p]);
            }
            double lo = 0, hi = 1;
            if (levels.Count > 20) { levels.Sort(); lo = levels[levels.Count / 20]; hi = levels[levels.Count * 19 / 20]; }
            if (hi - lo < 0.15) { double mid = (hi + lo) / 2; lo = mid - 0.3; hi = mid + 0.3; }
            // ---- 3. the symbol recoloured for the style
            double tr = tint.R / 255.0, tg = tint.G / 255.0, tb = tint.B / 255.0;
            var glyph = new byte[N * N * 4];
            for (int p = 0; p < N * N; p++)
            {
                double m = mask[p];
                if (m <= 0.002) continue;
                double l = Math.Max(0, Math.Min(1, (lum[p] - lo) / (hi - lo)));
                double cr, cg, cb, alpha = m;
                if (style == 1 && dark) { double t = 0.18 + 0.55 * l; cr = tr + (1 - tr) * t; cg = tg + (1 - tg) * t; cb = tb + (1 - tb) * t; }
                else if (style == 1) { double t = 0.42 * (1 - l); cr = tr * (1 - t); cg = tg * (1 - t); cb = tb * (1 - t); }
                // Clear: Liquid Glass flips its symbols to suit the glass -- white on dark glass, a deep graphite on
                // light glass (white on pale glass is what made light-mode Clear icons nearly invisible)
                else if (dark) { double t = 0.16 * (1 - l); cr = 1 - t; cg = 1 - t * 0.9; cb = 1 - t * 0.7; alpha = m * 0.96; }
                else { double t = 0.10 + 0.22 * l; cr = t; cg = t + 0.01; cb = t + 0.04; alpha = m * 0.86; }
                int i = p * 4;
                glyph[i] = (byte)(cb * 255 * alpha + 0.5); glyph[i + 1] = (byte)(cg * 255 * alpha + 0.5); glyph[i + 2] = (byte)(cr * 255 * alpha + 0.5); glyph[i + 3] = (byte)(255 * alpha + 0.5);
            }
            var glyphImg = BitmapSource.Create(N, N, 96, 96, PixelFormats.Pbgra32, null, glyph, N * 4);
            glyphImg.Freeze();

            // ---- 4. the glass tile, layer by layer
            var shape = Squircle(T);
            Func<string, double, Color> rgba = (hex, al) => { var c = (Color)ColorConverter.ConvertFromString(hex); c.A = (byte)(255 * al); return c; };
            Func<double, Color> towardBlack = t => Color.FromRgb((byte)(tint.R * t), (byte)(tint.G * t), (byte)(tint.B * t));
            Func<double, Color> towardWhite = t => Color.FromRgb((byte)(255 - (255 - tint.R) * t), (byte)(255 - (255 - tint.G) * t), (byte)(255 - (255 - tint.B) * t));
            Brush baseFill = style == 1
                ? (dark ? (Brush)new LinearGradientBrush(towardBlack(0.36), towardBlack(0.13), 90) : new LinearGradientBrush(towardWhite(0.10), towardWhite(0.30), 90))
                // Clear: thin glass, so the backdrop (bent by the Dock's lens under each icon) shows through
                : (dark ? new LinearGradientBrush(rgba("#2A2A32", 0.30), rgba("#0A0A0E", 0.42), 90) : new LinearGradientBrush(rgba("#FFFFFF", 0.34), rgba("#FFFFFF", 0.12), 90));
            var layers = new Grid { Width = T, Height = T, Clip = shape };
            layers.Children.Add(new Path { Data = shape, Fill = baseFill });
            // the sheen: light pooling in the top half of the glass
            layers.Children.Add(new Path { Data = shape, Fill = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb((byte)(style == 2 ? (dark ? 46 : 80) : (dark ? 30 : 60)), 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.55) }, new Point(0, 0), new Point(0, 1)) });
            // the symbol, raised a little off the glass
            var sym = new Image { Source = glyphImg, Width = T, Height = T, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(sym, BitmapScalingMode.HighQuality);
            sym.Effect = style == 2 && !dark
                ? new DropShadowEffect { BlurRadius = 3 * k, ShadowDepth = 1.4 * k, Direction = 270, Opacity = 0.7, Color = System.Windows.Media.Colors.White }   // a dark symbol pressed into light glass
                : new DropShadowEffect { BlurRadius = 6 * k, ShadowDepth = 1.6 * k, Direction = 270, Opacity = style == 2 ? 0.45 : (dark ? 0.45 : 0.18), Color = style == 1 && !dark ? towardBlack(0.35) : Color.FromRgb(0, 0, 0) };
            layers.Children.Add(sym);
            // the glass' thickness: a soft band inside the edge, lit at the top, shadowed at the bottom
            layers.Children.Add(new Path { Data = shape, StrokeThickness = 7 * k, Stroke = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb((byte)(dark ? 40 : 70), 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.4),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7), new GradientStop(Color.FromArgb((byte)(dark ? 60 : 26), 0, 0, 0), 1) }, new Point(0, 0), new Point(0, 1)) });
            // the specular rim: bright where the light catches the top-left and bottom-right edges
            layers.Children.Add(new Path { Data = shape, StrokeThickness = 2.4 * k, Stroke = new LinearGradientBrush(new GradientStopCollection {
                new GradientStop(Color.FromArgb((byte)(dark ? 200 : 245), 255, 255, 255), 0), new GradientStop(Color.FromArgb((byte)(dark ? 30 : 60), 255, 255, 255), 0.3),
                new GradientStop(Color.FromArgb((byte)(dark ? 10 : 20), 255, 255, 255), 0.62), new GradientStop(Color.FromArgb((byte)(dark ? 110 : 170), 255, 255, 255), 1) }, new Point(0.1, 0), new Point(0.9, 1)) });
            var host = new Grid { Width = T, Height = T };
            host.Children.Add(layers);
            host.Effect = new DropShadowEffect { BlurRadius = 12 * k, ShadowDepth = 4 * k, Direction = 270, Opacity = style == 2 ? 0.22 : 0.3 };
            var root = new Grid { Width = S, Height = S };
            root.Children.Add(host);
            return Icons.Render(root, S, S);
        }

        // Apple's icon shape: a superellipse (|x|^5 + |y|^5 = 1), whose corners curve continuously into the sides.
        public static Geometry Squircle(double size)
        {
            var f = new PathFigure { IsClosed = true, IsFilled = true };
            double c = size / 2, n = 5;
            var pts = new List<Point>();
            for (int i = 0; i < 256; i++)
            {
                double t = i * 2 * Math.PI / 256, ct = Math.Cos(t), st = Math.Sin(t);
                pts.Add(new Point(c + c * Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2 / n), c + c * Math.Sign(st) * Math.Pow(Math.Abs(st), 2 / n)));
            }
            f.StartPoint = pts[0];
            f.Segments.Add(new PolyLineSegment(pts.GetRange(1, pts.Count - 1), true));
            var g = new PathGeometry(new[] { f });
            g.Freeze();
            return g;
        }

        // Full-bleed artwork: opaque all along its edges, just inside the corners.
        static bool IsTile(BitmapSource src)
        {
            var bmp = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            var px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);
            Func<double, double, byte> A = (x, y) => px[((int)(y * (h - 1)) * w + (int)(x * (w - 1))) * 4 + 3];
            return A(0.5, 0.07) > 200 && A(0.5, 0.93) > 200 && A(0.07, 0.5) > 200 && A(0.93, 0.5) > 200
                && A(0.09, 0.09) > 200 && A(0.91, 0.09) > 200 && A(0.09, 0.91) > 200 && A(0.91, 0.91) > 200;
        }

        // On a tile, the symbol is what differs from the tile's own colour. That colour is read from four strips just
        // inside the edges (smoothed, so a symbol touching an edge barely counts) and blended across the tile, which
        // follows the top-to-bottom and side-to-side gradients most icon backgrounds have.
        static void SymbolOnTile(int N, double[] r, double[] g, double[] b, double[] a, double[] mask)
        {
            int e0 = (int)(N * 0.1), e1 = N - 1 - e0, rad = Math.Max(2, N / 10);
            var top = Strip(N, rad, i => e0 * N + i, r, g, b); var bottom = Strip(N, rad, i => e1 * N + i, r, g, b);
            var left = Strip(N, rad, i => i * N + e0, r, g, b); var right = Strip(N, rad, i => i * N + e1, r, g, b);
            var tones = Tones(N, e0, e1, r, g, b);
            int covered = 0, inside = 0;
            for (int y = 0; y < N; y++)
            {
                double v = Math.Max(0, Math.Min(1, (y - e0) / (double)(e1 - e0)));
                for (int x = 0; x < N; x++)
                {
                    int p = y * N + x;
                    if (a[p] < 0.02) continue;
                    double u = Math.Max(0, Math.Min(1, (x - e0) / (double)(e1 - e0)));
                    double br = 0.5 * (top[x, 0] + (bottom[x, 0] - top[x, 0]) * v + left[y, 0] + (right[y, 0] - left[y, 0]) * u);
                    double bg = 0.5 * (top[x, 1] + (bottom[x, 1] - top[x, 1]) * v + left[y, 1] + (right[y, 1] - left[y, 1]) * u);
                    double bb = 0.5 * (top[x, 2] + (bottom[x, 2] - top[x, 2]) * v + left[y, 2] + (right[y, 2] - left[y, 2]) * u);
                    double d = Math.Sqrt((r[p] - br) * (r[p] - br) + (g[p] - bg) * (g[p] - bg) + (b[p] - bb) * (b[p] - bb)) / 1.732;
                    foreach (var t in tones) d = Math.Min(d, Math.Sqrt((r[p] - t[0]) * (r[p] - t[0]) + (g[p] - t[1]) * (g[p] - t[1]) + (b[p] - t[2]) * (b[p] - t[2])) / 1.732 + 0.02);
                    double m = Smooth((d - 0.07) / 0.13) * a[p];
                    mask[p] = m;
                    inside++; if (m > 0.5) covered++;
                }
            }
            // Next to nothing stands out (a photo-like or single-colour icon): use its light and shade instead.
            if (inside > 0 && covered < inside / 60)
            {
                double mean = 0; int n = 0;
                for (int p = 0; p < N * N; p++) if (a[p] > 0.5) { mean += 0.2126 * r[p] + 0.7152 * g[p] + 0.0722 * b[p]; n++; }
                mean /= Math.Max(1, n);
                for (int p = 0; p < N * N; p++) mask[p] = a[p] * Smooth((Math.Abs(0.2126 * r[p] + 0.7152 * g[p] + 0.0722 * b[p] - mean) - 0.05) / 0.15);
            }
        }

        // The tile's main colours (up to four), found by grouping the colours along its edges (k-means). A background
        // in two halves -- Finder's face -- gives two tones; a gradient gives a few along it.
        static List<double[]> Tones(int N, int e0, int e1, double[] r, double[] g, double[] b)
        {
            var samples = new List<double[]>();
            for (int i = e0; i <= e1; i += Math.Max(1, N / 100))
                foreach (var p in new[] { e0 * N + i, e1 * N + i, i * N + e0, i * N + e1 }) samples.Add(new[] { r[p], g[p], b[p] });
            var centres = new List<double[]>();
            for (int k = 0; k < 4 && k < samples.Count; k++) centres.Add((double[])samples[k * samples.Count / 4].Clone());
            var count = new int[centres.Count];
            for (int iter = 0; iter < 8; iter++)
            {
                var sum = new double[centres.Count, 3]; count = new int[centres.Count];
                foreach (var s2 in samples)
                {
                    int best = 0; double bd = double.MaxValue;
                    for (int c = 0; c < centres.Count; c++) { double dd = 0; for (int j = 0; j < 3; j++) dd += (s2[j] - centres[c][j]) * (s2[j] - centres[c][j]); if (dd < bd) { bd = dd; best = c; } }
                    for (int j = 0; j < 3; j++) sum[best, j] += s2[j];
                    count[best]++;
                }
                for (int c = 0; c < centres.Count; c++) if (count[c] > 0) for (int j = 0; j < 3; j++) centres[c][j] = sum[c, j] / count[c];
            }
            // a tone only counts if a good share of the edge has it (a symbol touching the edge doesn't)
            var tones = new List<double[]>();
            for (int c = 0; c < centres.Count; c++) if (count[c] >= samples.Count / 8) tones.Add(centres[c]);
            return tones;
        }

        static double[,] Strip(int N, int rad, Func<int, int> at, double[] r, double[] g, double[] b)
        {
            var raw = new double[N, 3]; var s = new double[N, 3];
            for (int i = 0; i < N; i++) { int p = at(i); raw[i, 0] = r[p]; raw[i, 1] = g[p]; raw[i, 2] = b[p]; }
            for (int i = 0; i < N; i++)
            {
                double sr = 0, sg = 0, sb = 0; int n = 0;
                for (int j = Math.Max(0, i - rad); j <= Math.Min(N - 1, i + rad); j++) { sr += raw[j, 0]; sg += raw[j, 1]; sb += raw[j, 2]; n++; }
                s[i, 0] = sr / n; s[i, 1] = sg / n; s[i, 2] = sb / n;
            }
            return s;
        }

        static double Smooth(double t) { t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * (3 - 2 * t); }
    }
}
