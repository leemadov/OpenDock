using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    // Physically traced Liquid Glass, ported from Lee's liquid_glass.cpp (the renderer that matched Apple's slider and
    // toggle clip): every pixel of the glass sends a ray through a real lens --
    //   front surface (domed, with a rounded rim) -> refract (n = 1.5) + Fresnel reflection of a studio sky
    //   inside the glass                          -> march to the hollowed back surface or the side wall
    //   back / side surface                       -> refract out (total internal reflection handled)
    // and the ray lands on the backdrop (a captured picture) lying a little way behind the glass. The bright floor
    // bounce in the sky is what lights the rim; grazing angles darken the very edge; a touch of "milk" frosts it.
    // Here the outline is GlassDock's rounded rectangle and all sizes are in pixels, scaled from the knob's half height
    // (the C++ toggle's proportions). Work happens in linear light, as in the original.
    static class Lens3D
    {
        const double Ior = 1.5, F0 = 0.04, Coat = 0.6, Clarity = 1.06, EdgeDark = 0.6;
        static readonly double[] ToLinear = new double[256];
        static Lens3D() { for (int i = 0; i < 256; i++) { double v = i / 255.0; ToLinear[i] = v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); } }
        static byte ToSrgb(double v) { v = v < 0 ? 0 : v > 1 ? 1 : v; v = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055; return (byte)(v * 255 + 0.5); }

        // `src` a Bgr32/Bgra32 picture (sw x sh) of what's behind; `r` the glass within it; `radius` its corners.
        // milk 0..~0.15 frosts it (more toward the bottom), glow lifts it a little.
        public static BitmapSource Render(byte[] src, int sw, int sh, Rect r, double radius, double milk = 0.025, double glow = 0)
        {
            int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
            double hx = r.Width / 2, hy = r.Height / 2, cx = r.X + hx, cy = r.Y + hy;
            radius = Math.Max(0, Math.Min(radius, Math.Min(hx, hy)));
            double b = Math.Min(hx, hy);                              // the C++ toggle's half height is 2.0 units
            double T = 0.9 * b, dome = 0.06 * b, hollow = 0.21 * b, bevel = 0.17 * b;
            double plane = T / 2 + 0.85 * b;                          // the backdrop, behind the glass
            var dst = new byte[w * h * 4];
            System.Threading.Tasks.Parallel.For(0, h, j =>
            {
                for (int i = 0; i < w; i++)
                {
                    // 2x2 supersampling: clean edges
                    double R = 0, G = 0, B = 0, A = 0;
                    for (int sj = 0; sj < 2; sj++)
                        for (int si = 0; si < 2; si++)
                        {
                            double px = r.X + i + (si + 0.5) / 2, py = r.Y + j + (sj + 0.5) / 2;
                            double sd = Liquid.SdRoundRect(px - cx, py - cy, hx, hy, radius);
                            if (sd >= 0) continue;
                            double cr, cg, cb;
                            Shade(src, sw, sh, px, py, cx, cy, hx, hy, radius, b, T, dome, hollow, bevel, plane, milk, glow, out cr, out cg, out cb);
                            R += cr; G += cg; B += cb; A += 1;
                        }
                    int o = (j * w + i) * 4;
                    if (A == 0) continue;
                    double a = A / 4;
                    dst[o] = (byte)(ToSrgb(B / A) * a); dst[o + 1] = (byte)(ToSrgb(G / A) * a); dst[o + 2] = (byte)(ToSrgb(R / A) * a); dst[o + 3] = (byte)(a * 255 + 0.5);
                }
            });
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, dst, w * 4);
            bmp.Freeze();
            return bmp;
        }

        // ---- the glass's shape, in a frame where X runs right, Y runs into the screen and Z runs up (Z = -screen y)

        static void Local(double px, double py, double cx, double cy, double hx, double hy, double radius, double b, out double s, out double e)
        {
            double sd = Liquid.SdRoundRect(px - cx, py - cy, hx, hy, radius);
            e = Math.Max(0, -sd);                                     // distance in from the edge
            s = 1 - Math.Min(1, e / b);                               // 0 in the middle, 1 at the rim
        }

        static double Front(double px, double py, double cx, double cy, double hx, double hy, double radius, double b, double T, double dome, double bevel)
        {
            double s, e; Local(px, py, cx, cy, hx, hy, radius, b, out s, out e);
            double y = -T / 2 - dome * (1 - s * s);
            if (e < bevel) y += bevel - Math.Sqrt(Math.Max(0, bevel * bevel - (bevel - e) * (bevel - e)));   // the rounded rim
            return y;
        }

        static double Back(double px, double py, double cx, double cy, double hx, double hy, double radius, double b, double T, double hollow)
        {
            double s, e; Local(px, py, cx, cy, hx, hy, radius, b, out s, out e);
            return T / 2 - hollow * (1 - s * s);
        }

        static bool Inside(double px, double py, double cx, double cy, double hx, double hy, double radius)
        {
            return Liquid.SdRoundRect(px - cx, py - cy, hx, hy, radius) < 0;
        }

        // the sky the glass reflects: dark overhead, a bright floor bounce
        static void Sky(double dz, out double v, out double vb)
        {
            double m = (1 - dz) / 2, p = m * m;
            const double top = 0.02, hor = 0.12, bot = 2.0;
            v = p < 0.25 ? top + (hor - top) * p / 0.25 : hor + (bot - hor) * (p - 0.25) / 0.75;
            vb = p < 0.25 ? top * 1.05 + (hor * 1.04 - top * 1.05) * p / 0.25 : hor * 1.04 + (bot - hor * 1.04) * (p - 0.25) / 0.75;
        }

        static bool Refract(double ix, double iy, double iz, double nx, double ny, double nz, double eta, out double ox, out double oy, out double oz)
        {
            double ci = -(ix * nx + iy * ny + iz * nz), k = 1 - eta * eta * (1 - ci * ci);
            if (k < 0) { double d = 2 * (ix * nx + iy * ny + iz * nz); ox = ix - nx * d; oy = iy - ny * d; oz = iz - nz * d; return false; }
            double f = eta * ci - Math.Sqrt(k);
            ox = ix * eta + nx * f; oy = iy * eta + ny * f; oz = iz * eta + nz * f;
            return true;
        }

        static void Norm(ref double x, ref double y, ref double z) { double l = Math.Sqrt(x * x + y * y + z * z); if (l > 0) { x /= l; y /= l; z /= l; } }

        // what's behind at screen point (sx, sy), in linear light (the picture mirrored beyond its edges)
        static void Behind(byte[] src, int sw, int sh, double sx, double sy, out double r, out double g, out double bl)
        {
            sx = Mirror(sx, sw); sy = Mirror(sy, sh);
            int x0 = Math.Min(sw - 1, Math.Max(0, (int)Math.Floor(sx - 0.5))), y0 = Math.Min(sh - 1, Math.Max(0, (int)Math.Floor(sy - 0.5)));
            int x1 = Math.Min(sw - 1, x0 + 1), y1 = Math.Min(sh - 1, y0 + 1);
            double fx = Math.Max(0, Math.Min(1, sx - 0.5 - x0)), fy = Math.Max(0, Math.Min(1, sy - 0.5 - y0));
            Func<int, int, int, double> at = (x, y, c) => ToLinear[src[(y * sw + x) * 4 + c]];
            Func<int, double> mix = c => (at(x0, y0, c) * (1 - fx) + at(x1, y0, c) * fx) * (1 - fy) + (at(x0, y1, c) * (1 - fx) + at(x1, y1, c) * fx) * fy;
            bl = mix(0); g = mix(1); r = mix(2);
        }

        static double Mirror(double v, int n)
        {
            if (n <= 1) return 0.5;
            double period = 2.0 * n; v = ((v % period) + period) % period;
            return v > n ? period - v : v;
        }

        static void Shade(byte[] src, int sw, int sh, double px, double py, double cx, double cy, double hx, double hy, double radius,
            double b, double T, double dome, double hollow, double bevel, double plane, double milk, double glow, out double outR, out double outG, out double outB)
        {
            const double hh = 0.5;   // finite-difference step (px)
            Func<double, double, double> front = (x, y) => Front(x, y, cx, cy, hx, hy, radius, b, T, dome, bevel);
            Func<double, double, double> back = (x, y) => Back(x, y, cx, cy, hx, hy, radius, b, T, hollow);
            double yf = front(px, py);
            // front normal (X, Y, Z-up); screen y grows downward, so d/dZ = -d/dy
            double nx = (front(px + hh, py) - front(px - hh, py)) / (2 * hh), nz = (front(px, py - hh) - front(px, py + hh)) / (2 * hh), ny = -1;   // d/dZ = -d/dy
            Norm(ref nx, ref ny, ref nz);
            double ci = Math.Max(0, -ny);   // the ray I = (0, 1, 0): cos of its angle to the surface
            double F = F0 + (1 - F0) * Math.Pow(1 - ci, 5), Rf = Math.Min(1, F * (1 + Coat));
            // reflection of the sky
            double rdz = 0 - nz * (2 * ny);   // reflect (0,1,0) about N: z component
            double sv, svb; Sky(rdz, out sv, out svb);
            double dx, dy, dz;
            Refract(0, 1, 0, nx, ny, nz, 1 / Ior, out dx, out dy, out dz);
            double Px = px, Py = yf, Pz = -py;   // (X, depth, Z-up)
            double tr = 0, tg = 0, tb = 0;
            for (int bounce = 0; bounce < 3; bounce++)
            {
                double len = (T + dome + 0.5 * b) / Math.Max(0.2, Math.Abs(dy)), step = len / 24, tt = 0;
                int hit = 0;
                for (int k = 0; k < 32; k++)
                {
                    double qx = Px + dx * (tt + step), qy = Py + dy * (tt + step), qz = Pz + dz * (tt + step);
                    if (!Inside(qx, -qz, cx, cy, hx, hy, radius)) { hit = 2; break; }
                    if (qy >= back(qx, -qz)) { hit = 1; break; }
                    if (dy < 0 && qy <= front(qx, -qz)) { hit = 3; break; }
                    tt += step;
                }
                double lo = tt, hi = tt + step;
                for (int k = 0; k < 8; k++)
                {
                    double mid = (lo + hi) / 2, mx = Px + dx * mid, my = Py + dy * mid, mz = Pz + dz * mid;
                    bool outside = !Inside(mx, -mz, cx, cy, hx, hy, radius) || my >= back(mx, -mz) || (dy < 0 && my <= front(mx, -mz));
                    if (outside) hi = mid; else lo = mid;
                }
                double Qx = Px + dx * hi, Qy = Py + dy * hi, Qz = Pz + dz * hi;
                double ox, oy, oz;   // outward normal at the exit
                if (hit == 1) { ox = -(back(Qx + hh, -Qz) - back(Qx - hh, -Qz)) / (2 * hh); oy = 1; oz = (back(Qx, -Qz - hh) - back(Qx, -Qz + hh)) / (2 * hh) * -1; }
                else if (hit == 2) { double gx, gy; Liquid.GradRoundRect(Qx - cx, -Qz - cy, hx, hy, radius, out gx, out gy); ox = gx; oy = 0; oz = -gy; }
                else { ox = (front(Qx + hh, -Qz) - front(Qx - hh, -Qz)) / (2 * hh); oy = -1; oz = (front(Qx, -Qz - hh) - front(Qx, -Qz + hh)) / (2 * hh); }
                Norm(ref ox, ref oy, ref oz);
                double ex, ey, ez;
                if (Refract(dx, dy, dz, -ox, -oy, -oz, Ior, out ex, out ey, out ez))
                {
                    // out of the glass: on to the backdrop plane behind it
                    double sx, sy;
                    if (ey < 1e-4) { sx = Qx + ex * 6 * b; sy = -(Qz + ez * 6 * b); }
                    else { double t = (plane - Qy) / ey; sx = Qx + ex * t; sy = -(Qz + ez * t); }
                    Behind(src, sw, sh, sx, sy, out tr, out tg, out tb);
                    break;
                }
                dx = ex; dy = ey; dz = ez; Px = Qx; Py = Qy; Pz = Qz;   // total internal reflection: keep going inside
                if (bounce == 2) { double v, vb; Sky(dz, out v, out vb); tr = tg = v * 0.5; tb = vb * 0.5; }
            }
            double cr = tr * (1 - Rf) * Clarity + sv * Rf, cg = tg * (1 - Rf) * Clarity + sv * Rf, cbl = tb * (1 - Rf) * Clarity + svb * Rf;
            // frosting, stronger toward the bottom; a little glow
            double s0, e0; Local(px, py, cx, cy, hx, hy, radius, b, out s0, out e0);
            double vrel = (py - cy) / hy;                              // -1 top .. 1 bottom
            double milkMask = 0.3 + 0.7 * Math.Pow(Math.Max(0, Math.Min(1, (vrel + 1) / 2)), 1.5);
            double mk = Math.Max(0, Math.Min(1, milk * milkMask * 3));
            cr += (0.62 - cr) * mk + glow; cg += (0.66 - cg) * mk + glow; cbl += (0.64 - cbl) * mk + glow;
            // the very edge, seen at a grazing angle, darkens
            double facing = 1 - ci, t2 = Math.Max(0, Math.Min(1, (facing - 0.72) / (0.95 - 0.72))), m = t2 * t2 * (3 - 2 * t2) * EdgeDark;
            outR = cr + (0.12 - cr) * m; outG = cg + (0.12 - cg) * m; outB = cbl + (0.13 - cbl) * m;
        }
    }
}
