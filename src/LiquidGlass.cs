using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace OpenDock
{
    // Liquid Glass, ported from Kyant's AndroidLiquidGlass ("backdrop", Apache-2.0): the same signed-distance
    // rounded rectangle, the same circular lens profile and the same seven-sample dispersion, done on the CPU
    // from a capture of what's behind the glass.
    //
    //  - Vibrancy: what's behind is 1.5x more saturated.
    //  - Lens: within `height` of the edge, each pixel looks inward along the edge's normal by
    //    amount * (1 - sqrt(1 - x^2)), x = 1 - depth/height -- nothing in the middle, a strong bend at the rim,
    //    like the curved edge of a thick lens. Normals are taken on a rounder shape (1.5x the corner radius) so
    //    corners bend smoothly; `depth` adds a pull toward the centre.
    //  - Dispersion: seven samples spread along the bend (red, orange, yellow, green, cyan, blue, purple),
    //    strongest at the corners, mixed back into RGB with the library's weights.
    static class Liquid
    {
        // `src` is a Bgr32 capture (sw x sh); `r`, `radius`, `height` and `amount` are in its pixels.
        // `bevel` (0..1) lights the rim as the rounded side of a thick slab -- a flat top whose edge curves down
        // like a quarter circle (macOS's Dock): the upper curve catches the light, the lower one falls into shade,
        // a glint runs where the curve leaves the flat top, and the very edge glows (glass seen edge-on).
        // `glint` scales that glint: on a long bar it is a fine line; on a small tile its curved corners squeeze it into
        // two bright spots, so icons go without.
        public static BitmapSource Lens(byte[] src, int sw, int sh, Rect r, double radius, double height, double amount, bool dispersion, double depth = 0, double saturation = 1.5, double bevel = 0, double glint = 1, double falloff = 0)
        {
            return RenderLens(src, sw, sh, r, radius, height, amount, dispersion, depth, saturation, bevel, glint, falloff, true);
        }

        internal static BitmapSource RenderLens(byte[] src, int sw, int sh, Rect r, double radius, double height, double amount, bool dispersion, double depth, double saturation, double bevel, double glint, double falloff, bool useNative)
        {
            int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
            var dst = new byte[checked(w * h * 4)];
            if (useNative && NativeLiquid.Lens(src, dst, sw, sh, w, h, r, radius, height, amount, dispersion, depth, saturation, bevel, glint, falloff))
                return GlassBitmap(dst, w, h);
            double hx = r.Width / 2, hy = r.Height / 2, cx = r.X + hx, cy = r.Y + hy;
            radius = Math.Max(0, Math.Min(radius, Math.Min(hx, hy)));
            double gradRadius = Math.Min(radius * 1.5, Math.Min(hx, hy));
            height = Math.Max(0.5, height);
            System.Threading.Tasks.Parallel.For(0, h, j =>
            {
                var c = new double[3];
                for (int i = 0; i < w; i++)
                {
                    double px = r.X + i + 0.5, py = r.Y + j + 0.5, ux = px - cx, uy = py - cy;
                    double sx = px, sy = py, dx = 0, dy = 0, rimX = 0, nx = 0, ny = 0;
                    // The flat middle of the pane (most of a wide one): straight inside the rounded rectangle's cross, at
                    // least `height` from its edges -- not bent, so the distance maths is skipped (same result)
                    double ax = Math.Abs(ux), ay = Math.Abs(uy);
                    bool flat = ax <= hx - height && ay <= hy - height && (ax <= hx - radius || ay <= hy - radius);
                    double sd = flat ? -height - 1 : SdRoundRect(ux, uy, hx, hy, radius);
                    bool bent = !flat && -sd < height;
                    if (bent)
                    {
                        sd = Math.Min(sd, 0);
                        double x = 1 + sd / height;                       // 1 at the rim, 0 where the bend ends
                        // Real glass: the curved rim bends light from just *outside* the glass into view, so what's behind
                        // the edge wraps around it (Apple's look). The library's circular profile gives the sharp pull at
                        // the very rim; a gentler x^2 term carries the bend further in, as on Apple's thick glass.
                        // `falloff` > 0: a smooth bend instead, strongest at the rim and easing off evenly toward the
                        // centre (x^falloff) -- the iPhone's round keys, with no sudden ring inside the glass
                        double d = falloff > 0 ? (falloff == 2.5 ? x * x * Math.Sqrt(x) : falloff == 2 ? x * x : Math.Pow(x, falloff)) * amount
                            : (0.6 * (1 - Math.Sqrt(Math.Max(0, 1 - x * x))) + 0.4 * x * x) * amount;
                        double gx, gy;
                        GradRoundRect(ux, uy, hx, hy, gradRadius, out gx, out gy);
                        rimX = x; nx = gx; ny = gy;
                        if (depth != 0) { double l = Math.Sqrt(ux * ux + uy * uy); if (l > 1e-6) { gx += depth * ux / l; gy += depth * uy / l; } }
                        double gl = Math.Sqrt(gx * gx + gy * gy);
                        if (gl > 1e-6) { gx /= gl; gy /= gl; }
                        sx = px + d * gx; sy = py + d * gy;
                        if (dispersion)
                        {
                            double k = (ux * uy) / (hx * hy);                // strongest toward the corners
                            dx = d * gx * k; dy = d * gy * k;
                        }
                    }
                    if (bent && dispersion && (dx != 0 || dy != 0))
                    {
                        // red, orange, yellow, green, cyan, blue, purple -- weights from the library
                        double rr = 0, gg = 0, bb = 0;
                        rr += S(src, sw, sh, sx + dx, sy + dy, 2) / 3.5;
                        rr += S(src, sw, sh, sx + dx * 2 / 3, sy + dy * 2 / 3, 2) / 3.5; gg += S(src, sw, sh, sx + dx * 2 / 3, sy + dy * 2 / 3, 1) / 7.0;
                        rr += S(src, sw, sh, sx + dx / 3, sy + dy / 3, 2) / 3.5; gg += S(src, sw, sh, sx + dx / 3, sy + dy / 3, 1) / 3.5;
                        gg += S(src, sw, sh, sx, sy, 1) / 3.5;
                        gg += S(src, sw, sh, sx - dx / 3, sy - dy / 3, 1) / 3.5; bb += S(src, sw, sh, sx - dx / 3, sy - dy / 3, 0) / 3.0;
                        bb += S(src, sw, sh, sx - dx * 2 / 3, sy - dy * 2 / 3, 0) / 3.0;
                        rr += S(src, sw, sh, sx - dx, sy - dy, 2) / 7.0; bb += S(src, sw, sh, sx - dx, sy - dy, 0) / 3.0;
                        c[0] = bb; c[1] = gg; c[2] = rr;
                    }
                    else Sample3(src, sw, sh, sx, sy, c);
                    // vibrancy: the library's colour matrix, luminance weights 0.213 / 0.715 / 0.072
                    double lum = 0.213 * c[2] + 0.715 * c[1] + 0.072 * c[0];
                    int o = (j * w + i) * 4;
                    double b0 = lum + (c[0] - lum) * saturation, b1 = lum + (c[1] - lum) * saturation, b2 = lum + (c[2] - lum) * saturation;
                    if (bevel > 0 && bent && rimX > 0)
                    {
                        double add, mul;
                        Bevel(rimX, nx, ny, glint, out add, out mul);
                        add *= bevel; mul = 1 - (1 - mul) * bevel;
                        // screen-style: light added in proportion to the room left, so over a bright backdrop the rim
                        // brightens a little instead of burning out to white
                        b0 *= mul; b1 *= mul; b2 *= mul;
                        b0 += add * (1 - Math.Min(255, b0) / 255); b1 += add * (1 - Math.Min(255, b1) / 255); b2 += add * (1 - Math.Min(255, b2) / 255);
                    }
                    dst[o] = Clamp(b0);
                    dst[o + 1] = Clamp(b1);
                    dst[o + 2] = Clamp(b2);
                    dst[o + 3] = 255;
                }
            });
            return GlassBitmap(dst, w, h);
        }

        static BitmapSource GlassBitmap(byte[] pixels, int w, int h)
        {
            var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            b.Freeze();
            return b;
        }

        // The slab's rounded side, lit. `x` runs 0 (where the flat top ends) to 1 (the outer edge) across the rim;
        // (nx, ny) is the edge's outward direction on screen (y down). The side is a quarter circle, so its surface
        // tilts out by asin(x). Light comes from above and a little left; a weaker second light from below right
        // gives the bottom edge the reflection thick glass shows there. Returns light to add (0..255) and a factor
        // to darken by.
        static readonly double[] KeyLight = Normalize(-0.3, -0.85, 0.55), FillLight = Normalize(0.35, 0.8, 0.5);
        static readonly double[] KeyHalf = Normalize(KeyLight[0], KeyLight[1], KeyLight[2] + 1), FillHalf = Normalize(FillLight[0], FillLight[1], FillLight[2] + 1);

        static double[] Normalize(double x, double y, double z) { double l = Math.Sqrt(x * x + y * y + z * z); return new[] { x / l, y / l, z / l }; }

        static void Bevel(double x, double nx, double ny, double glint, out double add, out double mul)
        {
            double s = Math.Min(1, x), cz = Math.Sqrt(Math.Max(0, 1 - s * s));
            double Nx = nx * s, Ny = ny * s, Nz = cz;                           // the curved side's surface normal
            // diffuse: compared with the flat top, so the top stays as it is and only the curve changes
            double lit = (Nx * KeyLight[0] + Ny * KeyLight[1] + Nz * KeyLight[2]) - KeyLight[2];
            // the glint where the curve catches the light, and the second light's softer one
            double k = Math.Max(0, Nx * KeyHalf[0] + Ny * KeyHalf[1] + Nz * KeyHalf[2]);
            double f = Math.Max(0, Nx * FillHalf[0] + Ny * FillHalf[1] + Nz * FillHalf[2]);
            // k^60 and f^40 by repeated squaring (and nothing at all where the light isn't near: 0.6^40 is 1e-9) -- the
            // same numbers as Math.Pow without the cost, for every pixel of every rim
            double k2 = k * k, k4 = k2 * k2, k8 = k4 * k4, k16 = k8 * k8, k32 = k16 * k16;
            double f2 = f * f, f4 = f2 * f2, f8 = f4 * f4, f16 = f8 * f8, f32 = f16 * f16;
            double spec = ((k < 0.6 ? 0 : k32 * k16 * k8 * k4) * 0.7 + (f < 0.6 ? 0 : f32 * f8) * 0.3) * glint;
            // the edge itself, seen edge-on through the glass, glows a little all round
            double s2 = s * s, fresnel = s2 * s2 * s2 * 0.22;
            add = (Math.Max(0, lit) * 0.45 + spec + fresnel) * 255;
            mul = 1 - Math.Max(0, -lit) * 0.45;                                  // the side turned from the light, in shade
        }

        // Signed distance to a rounded rectangle centred on the origin (negative inside).
        public static double SdRoundRect(double x, double y, double hx, double hy, double radius)
        {
            double qx = Math.Abs(x) - (hx - radius), qy = Math.Abs(y) - (hy - radius);
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return Math.Sqrt(ox * ox + oy * oy) - radius + Math.Min(Math.Max(qx, qy), 0);
        }

        // Its outward normal (the library's gradSdRoundedRect).
        public static void GradRoundRect(double x, double y, double hx, double hy, double radius, out double gx, out double gy)
        {
            double qx = Math.Abs(x) - (hx - radius), qy = Math.Abs(y) - (hy - radius);
            if (qx >= 0 || qy >= 0)
            {
                double mx = Math.Max(qx, 0), my = Math.Max(qy, 0), l = Math.Sqrt(mx * mx + my * my);
                if (l < 1e-9) { gx = 0; gy = 0; return; }
                gx = Math.Sign(x) * mx / l; gy = Math.Sign(y) * my / l;
            }
            else
            {
                double sx = qx >= qy ? 1 : 0;
                gx = Math.Sign(x) * sx; gy = Math.Sign(y) * (1 - sx);
            }
        }

        // Beyond the captured picture the glass sees it mirrored, not the edge pixel smeared into streaks.
        static double Mirror(double v, int n)
        {
            if (n <= 1) return 0.5;
            double period = 2.0 * n;
            v = v % period; if (v < 0) v += period;
            return v < n ? v : period - v;
        }

        // The three colour channels at once: one lookup of the four neighbouring pixels, not three.
        static void Sample3(byte[] s, int w, int h, double x, double y, double[] c)
        {
            if (x < 0 || x > w) x = Mirror(x, w);
            if (y < 0 || y > h) y = Mirror(y, h);
            x -= 0.5; y -= 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int x1 = Math.Max(0, Math.Min(w - 1, x0 + 1)), y1 = Math.Max(0, Math.Min(h - 1, y0 + 1));
            x0 = Math.Max(0, Math.Min(w - 1, x0)); y0 = Math.Max(0, Math.Min(h - 1, y0));
            int i00 = (y0 * w + x0) * 4, i01 = (y0 * w + x1) * 4, i10 = (y1 * w + x0) * 4, i11 = (y1 * w + x1) * 4;
            for (int k = 0; k < 3; k++)
            {
                double a = s[i00 + k], b = s[i01 + k], d = s[i10 + k], e = s[i11 + k];
                double top = a + (b - a) * fx, bottom = d + (e - d) * fx;
                c[k] = top + (bottom - top) * fy;
            }
        }

        static double S(byte[] s, int w, int h, double x, double y, int c)
        {
            if (x < 0 || x > w) x = Mirror(x, w);
            if (y < 0 || y > h) y = Mirror(y, h);
            x -= 0.5; y -= 0.5;
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            int x1 = Math.Max(0, Math.Min(w - 1, x0 + 1)), y1 = Math.Max(0, Math.Min(h - 1, y0 + 1));
            x0 = Math.Max(0, Math.Min(w - 1, x0)); y0 = Math.Max(0, Math.Min(h - 1, y0));
            double a = s[(y0 * w + x0) * 4 + c], b = s[(y0 * w + x1) * 4 + c], d = s[(y1 * w + x0) * 4 + c], e = s[(y1 * w + x1) * 4 + c];
            double top = a + (b - a) * fx, bottom = d + (e - d) * fx;
            return top + (bottom - top) * fy;
        }

        static byte Clamp(double v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v + 0.5); }

        // How strong the lens is for a piece of glass `size` DIPs across its short side -- the library's own
        // choices scaled: 12/24 dp on a 48 dp button, 24/24 dp on a 64 dp bar.
        // The iPhone recipe (tuned against Apple's passcode keys): the bend is strongest at the very rim and eases
        // off evenly toward the middle (x^Falloff), pulling a little further than before, and the rim band is
        // smeared slightly. Every Liquid Glass surface uses it, so they all bend the same way.
        public const double Falloff = 2.5, Pull = 1.35, RimSmear = 1.5;

        // The rim band of a lens picture smeared a little, as liquid glass does: a soft blur that's full at the edge
        // and fades to nothing `band` pixels in, so the middle stays sharp. `radius` is the shape's corner radius in
        // pixels (half the height for a capsule or a circle); `blur` is in pixels.
        public static BitmapSource Smear(BitmapSource lens, double radius, double band, double blur)
        {
            return RenderSmear(lens, radius, band, blur, true);
        }

        internal static BitmapSource RenderSmear(BitmapSource lens, double radius, double band, double blur, bool useNative)
        {
            // Only the band at the rim is touched (the middle is left as it is), rows in parallel, each pixel there
            // averaged over a small square around it -- a few milliseconds even for the Dock's long pane.
            BitmapSource src = lens.Format == PixelFormats.Bgra32 ? lens : new FormatConvertedBitmap(lens, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4, rad = Math.Max(1, (int)Math.Round(blur));
            if (w < 4 || h < 4 || band < 1) return lens;
            var a = new byte[stride * h]; src.CopyPixels(a, stride, 0);
            var o = (byte[])a.Clone();
            double hx = w / 2.0, hy = h / 2.0;
            radius = Math.Max(0, Math.Min(radius, Math.Min(hx, hy)));
            if (useNative && NativeLiquid.Smear(a, o, w, h, radius, band, rad))
                return GlassBitmap(o, w, h);
            int b0 = (int)Math.Ceiling(band) + 1;
            System.Threading.Tasks.Parallel.For(0, h, y =>
            {
                // a row inside the band's reach from the top or bottom is done whole; any other row only near its ends
                bool full = y < b0 || y >= h - b0 || y < radius + b0 || y >= h - radius - b0;
                for (int x = 0; x < w; x++)
                {
                    if (!full && x >= b0 && x < w - b0) { x = w - b0 - 1; continue; }
                    double inside = -SdRoundRect(x + 0.5 - hx, y + 0.5 - hy, hx, hy, radius);
                    if (inside >= band) continue;
                    double k = Math.Min(1, 1 - inside / band);
                    if (k <= 0) continue;
                    k = k * k * (3 - 2 * k);
                    int s0 = 0, s1 = 0, s2 = 0, s3 = 0, n = 0;
                    for (int dy = -rad; dy <= rad; dy++)
                    {
                        int yy = Math.Min(h - 1, Math.Max(0, y + dy));
                        for (int dx = -rad; dx <= rad; dx++)
                        {
                            int j = yy * stride + Math.Min(w - 1, Math.Max(0, x + dx)) * 4;
                            s0 += a[j]; s1 += a[j + 1]; s2 += a[j + 2]; s3 += a[j + 3]; n++;
                        }
                    }
                    int i = y * stride + x * 4;
                    o[i] = (byte)(a[i] + (s0 / n - a[i]) * k); o[i + 1] = (byte)(a[i + 1] + (s1 / n - a[i + 1]) * k);
                    o[i + 2] = (byte)(a[i + 2] + (s2 / n - a[i + 2]) * k); o[i + 3] = (byte)(a[i + 3] + (s3 / n - a[i + 3]) * k);
                }
            });
            return GlassBitmap(o, w, h);
        }

        // Liquid Glass for a card in a window of its own (Spotlight, sheets, Move & Resize, Dock stacks): the screen
        // around the card right now (x, y, w, h: the card in physical pixels, its window already kept out of screen
        // capture), bent with the shared recipe. Null when nothing usable could be captured.
        public static BitmapSource Card(int x, int y, int w, int h, double radiusPx, double dpi)
        {
            if (w < 4 || h < 4 || Glass.Lifted) return null;
            double lh, la; Strength(Math.Min(w, h) / dpi, out lh, out la); lh *= dpi; la *= dpi * Pull;
            int m = (int)Math.Ceiling(la) + 2;   // the rim pulls the scene from just outside the card into view
            byte[] px; int pw, ph;
            if (!Grab(x - m, y - m, w + 2 * m, h + 2 * m, out px, out pw, out ph)) return null;
            return Lensed(px, pw, ph, new Rect(m, m, w, h), radiusPx, lh, la, dpi);
        }

        // A screen rectangle as Bgr32 pixels; false if it couldn't be captured or came back black (a failed capture).
        public static bool Grab(int x, int y, int w, int h, out byte[] px, out int pw, out int ph)
        {
            px = null; pw = ph = 0;
            try
            {
                var shot = Icons.CaptureScreen(x, y, w, h);
                if (shot == null) return false;
                var conv = new FormatConvertedBitmap(shot, PixelFormats.Bgr32, null, 0);
                pw = conv.PixelWidth; ph = conv.PixelHeight; px = new byte[pw * ph * 4]; conv.CopyPixels(px, pw * 4, 0);
                return Glass.BlackShare(px) < 0.6;
            }
            catch { return false; }
        }

        // The shared recipe on a picture already taken: `r` the card within it, `lh` / `la` the bend's band and pull.
        public static BitmapSource Lensed(byte[] px, int pw, int ph, Rect r, double radiusPx, double lh, double la, double dpi)
        {
            return Smear(Lens(px, pw, ph, r, radiusPx, lh, la, false, 0, 1.5, 0, 1, Falloff), radiusPx, lh, RimSmear * dpi);
        }

        // How bright a (BGR/BGRA) picture is, 0..1, from a sparse sample -- cheap enough to ask every frame.
        public static double Brightness(byte[] px)
        {
            if (px == null || px.Length < 4) return 0.5;
            double sum = 0; int n = 0;
            for (int i = 0; i + 2 < px.Length; i += 4 * 37) { sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]; n++; }
            return n == 0 ? 0.5 : sum / n / 255;
        }

        public static double Brightness(BitmapSource img)
        {
            if (img == null) return 0.5;
            try
            {
                var src = img.Format == PixelFormats.Bgra32 || img.Format == PixelFormats.Bgr32 ? img : new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
                var px = new byte[src.PixelWidth * src.PixelHeight * 4]; src.CopyPixels(px, src.PixelWidth * 4, 0);
                return Brightness(px);
            }
            catch { return 0.5; }
        }

        // Readable ink on glass, whatever the theme: over a bright backdrop the text and marks inside `root` turn
        // dark, over a dark one light (only the ones that use the theme's Fg / FgDim / MenuFg / MenuFgDim). The
        // change fades over a quarter second, and it only flips well past the middle, so it never flickers.
        public static void Ink(FrameworkElement root, double brightness)
        {
            if (root == null) return;
            bool? darkInk = brightness > 0.64 ? true : brightness < 0.4 ? (bool?)false : null;
            object was = root.Tag as string == "ink-dark" ? (object)true : root.Tag as string == "ink-light" ? (object)false : null;
            bool want = darkInk ?? (was as bool? ?? !Theme.Dark);
            root.Tag = want ? "ink-dark" : "ink-light";
            Color fg = want ? Color.FromArgb(0xE6, 0, 0, 0) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
            Color dim = want ? Color.FromArgb(0x8C, 0, 0, 0) : Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF);
            foreach (var pair in new[] { Tuple.Create("Fg", fg), Tuple.Create("MenuFg", fg), Tuple.Create("FgDim", dim), Tuple.Create("MenuFgDim", dim) })
            {
                var b = root.Resources[pair.Item1] as SolidColorBrush;
                if (b == null || b.IsFrozen)
                {
                    var start = b != null ? b.Color : (Application.Current.TryFindResource(pair.Item1) as SolidColorBrush ?? new SolidColorBrush(pair.Item2)).Color;
                    b = new SolidColorBrush(start);
                    root.Resources[pair.Item1] = b;
                }
                if (b.Color != pair.Item2) b.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(pair.Item2, TimeSpan.FromMilliseconds(250)));
            }
        }

        public static void Strength(double size, out double height, out double amount)
        {
            height = Math.Min(30, size * 0.3);
            amount = Math.Min(40, size * 0.45);
        }
    }

    // A spring, for WPF animations: the library's spring(dampingRatio 0.5, stiffness 300) -- quick, with one soft
    // overshoot. Play it over 0.6 s; by then it has settled.
    class Spring : EasingFunctionBase
    {
        public double Damping = 0.5, Stiffness = 300, Seconds = 0.6;
        // the curve below is the spring itself, start to finish: WPF's default (EaseOut) would play it backwards,
        // wobbling first and snapping at the end
        public Spring() { EasingMode = EasingMode.EaseIn; }
        protected override double EaseInCore(double t)
        {
            double w0 = Math.Sqrt(Stiffness), z = Damping, wd = w0 * Math.Sqrt(1 - z * z), s = t * Seconds;
            return 1 - Math.Exp(-z * w0 * s) * (Math.Cos(wd * s) + z * w0 / wd * Math.Sin(wd * s));
        }
        protected override Freezable CreateInstanceCore() { return new Spring(); }
    }

    // One piece of glass in a panel: its own refracted slice of the backdrop under a tint, a specular rim that
    // catches the light at two opposite corners, a soft sheen across the top, a faint inner shadow. Touched, it
    // behaves like the library's liquid buttons: pressed, it swells a little and a soft light follows the pointer;
    // dragged, it leans and stretches toward the pointer on an elastic band, then springs back.
    class GlassTile : Border
    {
        public Rect At;
        public readonly double Radius;
        public readonly Grid Body = new Grid();
        public readonly ScaleTransform Zoom = new ScaleTransform(1, 1);
        readonly TranslateTransform lean = new TranslateTransform();
        public Brush Glass;
        public bool Active, Quiet, Interactive;
        public Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);
        public TextBlock Icon;
        readonly Border hover, glow;
        readonly RadialGradientBrush light = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute };
        bool painted, shownActive, pressed;
        Point start;

        public GlassTile(Rect at, double radius)
        {
            At = at; Radius = radius; Width = at.Width; Height = at.Height;
            CornerRadius = new CornerRadius(radius);
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "CcRim");
            var tg = new TransformGroup(); tg.Children.Add(Zoom); tg.Children.Add(lean);
            RenderTransform = tg; RenderTransformOrigin = new Point(0.5, 0.5);
            var inner = new CornerRadius(Math.Max(0, radius - 1));
            var sheen = new Border { CornerRadius = inner, IsHitTestVisible = false };
            sheen.SetResourceReference(BackgroundProperty, "CcSheen");
            // the library's inner shadow: a soft darkening just inside the top edge, as if the rim stood proud
            var shade = new Border { CornerRadius = inner, IsHitTestVisible = false,
                Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(26, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), Math.Min(0.45, 14 / Math.Max(1, at.Height))) }, new Point(0, 0), new Point(0, 1)) };
            // the glass's thickness, as on GlassDock's icons: a band inside the edge, lit at the top, shadowed at the bottom
            var band = new Border { CornerRadius = inner, IsHitTestVisible = false, BorderThickness = new Thickness(Math.Max(2, Math.Min(at.Width, at.Height) * 0.045)),
                BorderBrush = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(36, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7), new GradientStop(Color.FromArgb(46, 0, 0, 0), 1) }, new Point(0, 0), new Point(0, 1)) };
            // reflections: a soft glint of window light near the top-left corner, and a thin bright streak following
            // the curve of the top edge, as light catches on curved glass
            double sw = Math.Min(at.Width, at.Height);
            var glint = new Border { CornerRadius = inner, IsHitTestVisible = false, Background = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, Center = new Point(sw * 0.32, sw * 0.18), GradientOrigin = new Point(sw * 0.28, sw * 0.12),
                RadiusX = Math.Min(at.Width * 0.45, sw * 0.9), RadiusY = sw * 0.38,
                GradientStops = new GradientStopCollection { new GradientStop(Color.FromArgb(70, 255, 255, 255), 0), new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.55), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }
            } };
            var streak = new Border { CornerRadius = inner, IsHitTestVisible = false, BorderThickness = new Thickness(0, Math.Max(1.2, sw * 0.03), 0, 0), Margin = new Thickness(sw * 0.08, 1.5, sw * 0.08, 0),
                BorderBrush = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.25),
                    new GradientStop(Color.FromArgb(28, 255, 255, 255), 0.6), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) }, new Point(0, 0), new Point(1, 0)) };
            hover = new Border { CornerRadius = inner, Opacity = 0, IsHitTestVisible = false };
            hover.SetResourceReference(BackgroundProperty, "CcHover");
            // the interactive highlight: white 8% all over plus a 15% glow, 1.5x the tile's short side across, under the pointer
            light.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0.5));
            light.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
            light.RadiusX = light.RadiusY = Math.Min(at.Width, at.Height) * 1.5;
            var glowLayers = new Grid();
            glowLayers.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) });
            glowLayers.Children.Add(new Border { Background = light });
            glow = new Border { CornerRadius = inner, Opacity = 0, IsHitTestVisible = false, Child = glowLayers, ClipToBounds = true };
            var g = new Grid();
            // (no glint: the radial white glow near the top-left corner looked like a smudge; the rim light stays)
            g.Children.Add(sheen); g.Children.Add(shade); g.Children.Add(streak); g.Children.Add(hover); g.Children.Add(glow); g.Children.Add(Body);
            Child = g;
            MouseEnter += delegate { hover.Opacity = 1; };
            MouseLeave += delegate { hover.Opacity = 0; Release(); };
            PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (!Interactive) return;
                pressed = true; start = e.GetPosition(this);
                light.Center = light.GradientOrigin = start;
                double grow = 1 + 4 / Math.Max(1, Math.Min(At.Width, At.Height));
                Animate(Zoom, ScaleTransform.ScaleXProperty, grow); Animate(Zoom, ScaleTransform.ScaleYProperty, grow);
                glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
            };
            PreviewMouseMove += (s, e) =>
            {
                if (!pressed) return;
                var p = e.GetPosition(this);
                light.Center = light.GradientOrigin = p;
                // lean toward the pointer on an elastic band (tanh), stretching along the pull
                double ox = p.X - start.X, oy = p.Y - start.Y, maxOffset = Math.Min(At.Width, At.Height), maxDim = Math.Max(At.Width, At.Height);
                lean.BeginAnimation(TranslateTransform.XProperty, null); lean.BeginAnimation(TranslateTransform.YProperty, null);
                lean.X = maxOffset * Math.Tanh(0.05 * ox / maxOffset); lean.Y = maxOffset * Math.Tanh(0.05 * oy / maxOffset);
                double grow = 1 + 4 / Math.Max(1, maxOffset), stretch = 4 / Math.Max(1, At.Height), angle = Math.Atan2(oy, ox);
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, null); Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                Zoom.ScaleX = grow + stretch * Math.Abs(Math.Cos(angle) * ox / maxDim) * Math.Min(1, At.Width / At.Height);
                Zoom.ScaleY = grow + stretch * Math.Abs(Math.Sin(angle) * oy / maxDim) * Math.Min(1, At.Height / At.Width);
            };
            PreviewMouseLeftButtonUp += delegate { Release(); };
        }

        void Release()
        {
            if (!pressed) return;
            pressed = false;
            Animate(Zoom, ScaleTransform.ScaleXProperty, 1); Animate(Zoom, ScaleTransform.ScaleYProperty, 1);
            Animate(lean, TranslateTransform.XProperty, 0); Animate(lean, TranslateTransform.YProperty, 0);
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
        }

        static void Animate(Animatable target, DependencyProperty p, double to)
        {
            target.BeginAnimation(p, new DoubleAnimation(to, TimeSpan.FromMilliseconds(600)) { EasingFunction = new Spring() });
        }

        public void ScaleTo(double to, int ms, bool spring)
        {
            IEasingFunction ease = spring ? (IEasingFunction)new Spring() : new CubicEase { EasingMode = EasingMode.EaseOut };
            var time = TimeSpan.FromMilliseconds(spring ? 600 : ms);
            Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, time) { EasingFunction = ease });
            Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, time) { EasingFunction = ease });
        }

        public void Paint(bool light)
        {
            Background = !Active ? Glass : light ? new SolidColorBrush(Accent) : GlassPanel.White;
            if (Icon != null)
            {
                if (!Active) Icon.ClearValue(TextBlock.ForegroundProperty);
                else Icon.Foreground = light ? Brushes.White : new SolidColorBrush(Accent);
            }
            // A switch flipping: a quick squeeze and a wobble back, like a drop of liquid.
            if (painted && !Quiet && Active != shownActive)
            {
                var time = TimeSpan.FromMilliseconds(600);
                Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1, time) { EasingFunction = new Spring() });
                Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1, time) { EasingFunction = new Spring() });
            }
            painted = true; shownActive = Active;
        }
    }

    // A Liquid Glass panel under the menu bar -- Control Center, Wi-Fi, Sound, Notification Center. Its pieces
    // float over whatever is behind, each refracting its own part of it. It springs open from its menu bar item,
    // and a click anywhere else shrinks it away. One is open at a time, like macOS menus.
    abstract class GlassPanel : Window
    {
        protected const double U = 64, G = 12, Pad = 30;   // the transparent margin holds the whole soft shadow
        public static readonly Brush White = Theme.B("#F2FFFFFF");
        protected static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF), Orange = Color.FromRgb(0xFF, 0x9F, 0x0A),
            Purple = Color.FromRgb(0x5E, 0x5C, 0xE6), Red = Color.FromRgb(0xFF, 0x3B, 0x30);
        protected const string PlayShape = "M0,0 L10,6 L0,12 Z", PauseShape = "M0,0 H3.6 V12 H0 Z M6.4,0 H10 V12 H6.4 Z",
            PreviousShape = "M11,0 L5,6 L11,12 Z M6,0 L0,6 L6,12 Z", NextShape = "M0,0 L6,6 L0,12 Z M5,0 L11,6 L5,12 Z";

        protected readonly Canvas canvas = new Canvas();
        protected readonly List<GlassTile> tiles = new List<GlassTile>();
        protected bool light;   // a bright backdrop (a white window behind): whiter glass and dark text, as macOS does
        readonly DropShadowEffect shadow = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Opacity = 0.3, Color = Colors.Black };
        static GlassPanel current;
        FrameworkElement anchor;
        DateTime hiddenAt;
        bool closing;

        protected GlassPanel(string title, double width)
        {
            Title = title;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = true;
            FontFamily = Theme.Font;
            Width = width; Height = 2 * Pad;
            SetResourceReference(ForegroundProperty, "CcFg");
            Palette(false);
            canvas.Effect = shadow;
            Content = canvas;
            Deactivated += delegate { if (!Outside.MenuOpen) Dismiss(false); };   // a menu it opened keeps it open
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Dismiss(false); };
            SourceInitialized += delegate { Native.MakeToolWindow(new WindowInteropHelper(this).Handle, false); };
        }

        // Builds what depends on the current state and sets the panel's height; runs before the backdrop is taken.
        protected virtual void Prepare() { }
        // Shows live values (switches, levels); runs once the panel knows whether it's over something bright.
        protected abstract void Refresh();

        // Opens under `from` (its menu bar item; null lines it up with the screen's right edge), or closes it.
        public void ToggleFrom(FrameworkElement from)
        {
            if (closing) return;                                                // this click is the one closing it
            if (IsVisible) { Dismiss(false); return; }
            if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 350) return;   // ...or the one that just closed it
            if (current != null && current != this) current.Dismiss(true);
            current = this;
            anchor = from;
            foreach (var t in tiles) t.Quiet = true;
            Prepare();
            Place();
            Frost();
            Refresh();
            Show();
            Native.Activate(new WindowInteropHelper(this).Handle);
            Outside.Watch(this, () => Dismiss(false));
            AnimateIn();
        }

        public void Dismiss(bool instant)
        {
            if (!IsVisible || closing) return;
            closing = true;
            Action hide = () => { Hide(); hiddenAt = DateTime.UtcNow; closing = false; };
            if (instant) hide(); else AnimateOut(hide);
        }

        // Rebuilds the panel while it's open (new scan results, a row opening up) and re-takes its backdrop.
        protected void Relayout(Action build)
        {
            build();
            if (IsVisible && !closing) { Frost(); Refresh(); }
        }

        void Place()
        {
            var b = DisplayLayout.Dip(DisplayLayout.Selected.Bounds);
            double right = b.Right - 8;   // where the glass's right edge goes
            if (anchor != null && anchor.IsVisible)
                try { right = anchor.PointToScreen(new Point(anchor.ActualWidth, 0)).X / App.Dpi + 4; } catch { }
            right = Math.Min(right, b.Right - 8);
            Left = Math.Max(b.Left + 8 - Pad, right + Pad - Width);
            Top = b.Top + (Settings.ShowMenuBar ? MenuBarWindow.H : 0) + 6 - Pad;
        }

        // One capture as it opens (it doesn't move while open), refracted separately for every piece of glass.
        void Frost()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            const int scale = 1;   // full resolution and only a light frost: Apple's glass shows the backdrop crisply enough to see it bend
            double d = App.Dpi, k = d / scale;
            byte[] px = null; int pw = 0, ph = 0;
            if (Glass.Exclude(hwnd))
            {
                long sum = 0;
                var shot = Glass.Behind((int)Math.Round(Left * d), (int)Math.Round(Top * d), (int)Math.Round(Width * d), (int)Math.Round(Height * d), FrostBlur, ref sum, scale);
                if (shot != null) { pw = shot.PixelWidth; ph = shot.PixelHeight; px = new byte[pw * ph * 4]; shot.CopyPixels(px, pw * 4, 0); }
            }
            Palette(px != null ? Bright(px) : Wallpaper.TopIsLight);
            var tint = (Brush)Resources["CcTint"];
            foreach (var t in tiles)
            {
                t.Glass = px != null ? TileGlass(px, pw, ph, t.At, t.Radius, k, tint) : Glass.WallpaperPane(tint, new Rect(Left + t.At.X, Top + t.At.Y, t.At.Width, t.At.Height));
                t.Paint(light);
            }
        }

        // macOS 26's Control Center glass is clear and thick: barely frosted, a deep bend at the rim that magnifies
        // what's behind it, vivid colour, no rainbow fringe to speak of.
        public const int FrostBlur = 2;                                   // box-blur radius (3 passes) of the capture
        const double LensDepth = 1, LensPull = 1, Vibrancy = 1.5;    // on top of Liquid.Strength's rule

        // One tile's glass from the panel's capture (`px`, `k` capture px per DIP): its slice of the backdrop, bent, under the tint.
        public static Brush TileGlass(byte[] px, int pw, int ph, Rect r, double radius, double k, Brush tint)
        {
            double lh, la;
            Liquid.Strength(Math.Min(r.Width, r.Height), out lh, out la);
            var lens = Liquid.Lens(px, pw, ph, new Rect(r.X * k, r.Y * k, r.Width * k, r.Height * k), radius * k, lh * LensDepth * k, la * LensPull * Liquid.Pull * k, false, 0, Vibrancy, 0, 1, Liquid.Falloff);
            lens = Liquid.Smear(lens, radius * k, lh * LensDepth * k, Liquid.RimSmear * k);
            return Glass.Slice(lens, new Rect(0, 0, 1, 1), tint);
        }

        // Mostly bright, or a big bright patch (a white window under part of it): white text would get lost there.
        internal static bool Bright(byte[] px)
        {
            double sum = 0; int n = 0, bright = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double l = 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
                sum += l; n++;
                if (l > 200) bright++;
            }
            return n > 0 && (sum / n > 150 || bright > n * 0.35);
        }

        // Every colour in a panel comes from here, so one switch flips the whole thing between the two looks.
        void Palette(bool bright)
        {
            light = bright;
            string[] keys = { "CcFg", "CcDim", "CcInner", "CcTrack", "CcFill", "CcHover", "CcTint", "CcRow" };
            // the tint (index 6) is light: macOS 26's Control Center lets the backdrop through nearly untouched
            string[] dark = { "#F2FFFFFF", "#B3FFFFFF", "#33FFFFFF", "#40FFFFFF", "#F2FFFFFF", "#14FFFFFF", "#38000000", "#1FFFFFFF" };
            string[] pale = { "#E6000000", "#8C000000", "#17000000", "#21000000", "#80000000", "#0D000000", "#26000000", "#12000000" };   // light glass stays see-through
            for (int i = 0; i < keys.Length; i++) Resources[keys[i]] = Theme.B(bright ? pale[i] : dark[i]);
            Resources["CcTint"] = Theme.Tint(bright ? pale[6] : dark[6]);   // the Liquid Glass slider
            // The rim catches light at the top-left and bottom-right corners; the sheen fades down from the top.
            Resources["CcRim"] = Gradient(new Point(0, 0), new Point(1, 1), bright
                ? new[] { "#E6FFFFFF", "#66FFFFFF", "#0AFFFFFF", "#66FFFFFF", "#D9FFFFFF" }
                : new[] { "#B3FFFFFF", "#40FFFFFF", "#05FFFFFF", "#40FFFFFF", "#A6FFFFFF" }, new[] { 0, 0.22, 0.5, 0.78, 1 });
            // a light sheen only along the top: a strong one washes clear glass milky
            Resources["CcSheen"] = Gradient(new Point(0, 0), new Point(0, 1), bright
                ? new[] { "#33FFFFFF", "#0DFFFFFF", "#00FFFFFF" } : new[] { "#14FFFFFF", "#06FFFFFF", "#00FFFFFF" }, new[] { 0, 0.4, 1.0 });
            shadow.Opacity = bright ? 0.16 : 0.3;
        }

        static Brush Gradient(Point a, Point b, string[] colors, double[] at)
        {
            var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
            for (int i = 0; i < colors.Length; i++) g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colors[i]), at[i]));
            g.Freeze();
            return g;
        }

        // ---------- animation ----------

        Point Origin { get { return new Point(Width - Pad, Pad); } }   // the corner nearest the menu bar item
        static double Distance(GlassTile t, Point p) { return (new Point(t.At.X + t.At.Width / 2, t.At.Y + t.At.Height / 2) - p).Length; }

        // The pieces spring out one after another, the nearest to the menu bar first.
        void AnimateIn()
        {
            double last = 0;
            foreach (var t in tiles)
            {
                double wait = Distance(t, Origin) * 0.3;
                last = Math.Max(last, wait);
                var delay = TimeSpan.FromMilliseconds(wait);
                var ease = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
                t.Opacity = 0; t.Zoom.ScaleX = t.Zoom.ScaleY = 0.6;
                t.Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
                t.Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(460)) { BeginTime = delay, EasingFunction = ease });
                t.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { BeginTime = delay });
            }
            var opened = tiles.ToList();
            Later(last + 480, () => { foreach (var t in opened) t.Quiet = false; });
        }

        // ...and shrink back toward it, the farthest first.
        void AnimateOut(Action done)
        {
            double far = tiles.Count == 0 ? 0 : tiles.Max(t => Distance(t, Origin));
            foreach (var t in tiles)
            {
                var delay = TimeSpan.FromMilliseconds((far - Distance(t, Origin)) * 0.12);
                var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
                var time = TimeSpan.FromMilliseconds(170);
                t.Zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.82, time) { BeginTime = delay, EasingFunction = ease });
                t.Zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.82, time) { BeginTime = delay, EasingFunction = ease });
                t.BeginAnimation(OpacityProperty, new DoubleAnimation(0, time) { BeginTime = delay, EasingFunction = ease });
            }
            Later(far * 0.12 + 190, done);
        }

        // ---------- building blocks ----------

        protected GlassTile Add(GlassTile t)
        {
            Canvas.SetLeft(t, t.At.X); Canvas.SetTop(t, t.At.Y);
            canvas.Children.Add(t);
            tiles.Add(t);
            return t;
        }

        protected void ClearTiles()
        {
            foreach (var t in tiles) canvas.Children.Remove(t);
            tiles.Clear();
        }

        // A switch inside a wide tile: white with a coloured icon over dark glass, filled with the colour over light.
        protected void Dot(Border dot, TextBlock glyph, bool on, Color color)
        {
            if (!on) { dot.SetResourceReference(Border.BackgroundProperty, "CcInner"); glyph.ClearValue(TextBlock.ForegroundProperty); return; }
            dot.Background = light ? new SolidColorBrush(color) : White;
            glyph.Foreground = light ? Brushes.White : new SolidColorBrush(color);
        }

        protected static TextBlock Glyph(string glyph, double size)
        {
            return new TextBlock { Text = glyph, FontFamily = Theme.Glyphs, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        protected static TextBlock Text(string text, double size, FontWeight weight, bool dim = false)
        {
            var t = new TextBlock { Text = text ?? "", FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (dim) t.SetResourceReference(TextBlock.ForegroundProperty, "CcDim");
            return t;
        }

        protected static TextBlock Section(string text)
        {
            var t = Text(text, 11.5, FontWeights.SemiBold, true);
            t.Margin = new Thickness(10, 4, 10, 4);
            return t;
        }

        protected static TextBlock Note(string text)
        {
            var t = Text(text, 12.5, FontWeights.Normal, true);
            t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None;
            t.Margin = new Thickness(10, 8, 10, 8);
            return t;
        }

        // A round icon for a list row: filled with its colour when it's the chosen one.
        protected static Border Badge(string glyph, bool on, Color color)
        {
            var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Center };
            var g = Glyph(glyph, 13);
            if (on) { b.Background = new SolidColorBrush(color); g.Foreground = Brushes.White; }
            else b.SetResourceReference(Border.BackgroundProperty, "CcInner");
            b.Child = g;
            return b;
        }

        // A list row inside a tile: a round icon, a title with a status under it, something on the right.
        protected static Border Row(FrameworkElement icon, string title, string sub, FrameworkElement right, Action click)
        {
            var d = new DockPanel { Margin = new Thickness(8, 0, 10, 0) };
            if (icon != null) { icon.Margin = new Thickness(0, 0, 10, 0); DockPanel.SetDock(icon, Dock.Left); d.Children.Add(icon); }
            if (right != null) { right.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Text(title, 13, FontWeights.Medium));
            if (!string.IsNullOrEmpty(sub)) words.Children.Add(Text(sub, 11.5, FontWeights.Normal, true));
            d.Children.Add(words);
            var row = new Border { Height = 40, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = d };
            if (click != null)
            {
                row.Cursor = Cursors.Hand;
                row.MouseEnter += delegate { row.SetResourceReference(Border.BackgroundProperty, "CcRow"); };
                row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                row.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            }
            return row;
        }

        // A solid play / pause / skip button.
        protected static Border Transport(string shape, double height, Action click)
        {
            // Both sides set from the shape's own proportions: with only a height, a star-sized column could clip it.
            var data = Geometry.Parse(shape);
            var path = new System.Windows.Shapes.Path { Data = data, Stretch = Stretch.Fill, Height = height, Width = height * data.Bounds.Width / data.Bounds.Height,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "CcFg");
            var b = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, MinWidth = height + 16, Height = height + 14, Child = path };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        protected static void Later(double ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            t.Tick += delegate { t.Stop(); a(); };
            t.Start();
        }
    }

    // A macOS switch: a capsule that fills with blue, the knob sliding across on a spring.
    class Switch2 : Border
    {
        readonly Border knob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
        readonly TranslateTransform slide = new TranslateTransform();
        bool on;
        public event Action<bool> Changed;

        public Switch2()
        {
            Width = 40; Height = 24; CornerRadius = new CornerRadius(12); Cursor = Cursors.Hand; VerticalAlignment = VerticalAlignment.Center;
            knob.RenderTransform = slide;
            knob.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 };
            Child = knob;
            Set(false, false);
            MouseLeftButtonUp += (s, e) => { e.Handled = true; Set(!on, true); if (Changed != null) Changed(on); };
        }

        public void Set(bool value, bool animate)
        {
            on = value;
            double to = on ? 16 : 0;
            if (animate) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(340)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            else { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = to; }
            if (on) Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)); else SetResourceReference(BackgroundProperty, "CcInner");
        }
    }
}
