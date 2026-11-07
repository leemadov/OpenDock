using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // The macOS 27 features' arithmetic: the Liquid Glass slider, tiling, Mission Control's grid, the Genie path.
    static class MacOS27Checks
    {
        static void Need(bool ok, string what) { if (!ok) throw new Exception(what); }
        static bool Near(double a, double b) { return Math.Abs(a - b) < 0.6; }

        public static void Run()
        {
            // Liquid Glass: 50 keeps the standard tint, 0 is nearly clear, 100 nearly solid, and it only ever rises.
            Need(Theme.GlassAlpha(0x99, 50) == 0x99, "glass slider changes the standard look");
            Need(Theme.GlassAlpha(0x99, 0) < 0x20, "clear glass is still tinted");
            Need(Theme.GlassAlpha(0x99, 100) >= 240, "tinted glass lets too much through");
            Need(Theme.GlassAlpha(0xF7, 100) == 0xF7, "tinted glass made an opaque tint lighter");
            for (int t = 0, last = -1; t <= 100; t += 5) { int a = Theme.GlassAlpha(0x60, t); Need(a >= last, "glass slider is not monotonic"); last = a; }

            // Tiling: halves meet with one gap between them, everything stays inside the margins.
            var work = new Rect(0, 30, 1920, 1000);
            var left = Tiling.Area(work, Tiling.Spot.Left, 8, new Size(800, 600));
            var right = Tiling.Area(work, Tiling.Spot.Right, 8, new Size(800, 600));
            Need(Near(left.Left, 8) && Near(left.Top, 38) && Near(left.Bottom, 1022), "left half misplaced");
            Need(Near(right.Left - left.Right, 8) && Near(right.Right, 1912), "halves don't meet at one gap");
            Need(Near(left.Width, right.Width), "halves differ in size");
            var br = Tiling.Area(work, Tiling.Spot.BottomRight, 8, new Size(800, 600));
            Need(Near(br.Right, 1912) && Near(br.Bottom, 1022) && Near(br.Width, left.Width), "bottom-right quarter misplaced");
            var center = Tiling.Area(work, Tiling.Spot.Center, 8, new Size(800, 600));
            Need(Near(center.Left, 560) && Near(center.Top, 230) && Near(center.Width, 800), "centring changed the size or missed the middle");
            var big = Tiling.Area(work, Tiling.Spot.Center, 8, new Size(5000, 5000));
            Need(big.Width <= 1904 && big.Height <= 984, "a huge window centred off the screen");

            // Mission Control: no overlaps, inside the area, proportions kept, never enlarged.
            var area = new Rect(60, 90, 1800, 870);
            var sizes = new List<Size> { new Size(1200, 800), new Size(800, 600), new Size(1600, 900), new Size(500, 700), new Size(300, 200) };
            var grid = MissionControl.Layout(sizes, area, 36);
            Need(grid.Count == sizes.Count, "a window went missing from Mission Control");
            for (int i = 0; i < grid.Count; i++)
            {
                var r = grid[i];
                Need(r.Left >= area.Left - 0.5 && r.Right <= area.Right + 0.5 && r.Top >= area.Top - 0.5 && r.Bottom <= area.Bottom + 0.5, "a window spills out of Mission Control");
                Need(Math.Abs(r.Width / r.Height - sizes[i].Width / sizes[i].Height) < 0.01, "Mission Control squashed a window");
                Need(r.Width <= sizes[i].Width + 0.5, "Mission Control enlarged a window");
                for (int j = i + 1; j < grid.Count; j++) { var o = Rect.Intersect(r, grid[j]); Need(o.IsEmpty || o.Width * o.Height < 1, "Mission Control windows overlap"); }
            }
            var one = MissionControl.Layout(new List<Size> { new Size(800, 600) }, area, 36);
            Need(Near(one[0].Width, 800) && Near(one[0].Left + 400, area.Left + area.Width / 2), "a single window isn't centred at its own size");

            // Genie: starts as the window, ends inside the icon, and the bottom pinches toward the icon first.
            var win = new Rect(400, 200, 800, 600); var icon = new Rect(900, 1000, 60, 60);
            var s0 = WindowFx.GenieSlice(win, icon, 0, 0, 1);
            Need(Near(s0.Left, win.Left) && Near(s0.Top, win.Top) && Near(s0.Width, win.Width) && Near(s0.Height, win.Height), "genie doesn't start at the window");
            var s1 = WindowFx.GenieSlice(win, icon, 1, 0.9, 1);
            Need(s1.Left >= icon.Left - 0.5 && s1.Right <= icon.Right + 0.5 && s1.Top >= icon.Top - 0.5 && s1.Bottom <= icon.Bottom + 0.5, "genie doesn't end in the icon");
            var topRow = WindowFx.GenieSlice(win, icon, 0.4, 0, 0.05); var bottomRow = WindowFx.GenieSlice(win, icon, 0.4, 0.95, 1);
            Need(bottomRow.Width < topRow.Width, "genie pinches the wrong end");

            // Icon styles, on a test icon: a red tile with a white disc on it (the symbol).
            var dv = new System.Windows.Media.DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 40, 40)), null, new Rect(0, 0, 200, 200));
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(100, 100), 45, 45);
            }
            var art = new System.Windows.Media.Imaging.RenderTargetBitmap(200, 200, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            art.Render(dv); art.Freeze();
            Func<System.Windows.Media.Imaging.BitmapSource, int, int, byte[]> at = (img, x, y) => { var p = new byte[4]; img.CopyPixels(new Int32Rect(x, y, 1, 1), p, 4, 0); return p; };
            var blue = System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF);
            foreach (var dark in new[] { false, true })
            {
                // Tinted: the tile and its symbol are both shades of the one colour, and the symbol stands out.
                var t = IconLook.Style(art, 1, blue, dark);
                byte[] mid = at(t, 128, 128), side = at(t, 128, 50);
                Need(mid[0] >= mid[1] && mid[1] >= mid[2] && side[0] >= side[1] && side[1] >= side[2], "a tinted icon isn't all shades of blue");
                Need(Math.Abs((mid[0] + mid[1] + mid[2]) - (side[0] + side[1] + side[2])) > 60, "the tinted symbol doesn't stand out");
                // Clear: the glass lets things through; the symbol is solid white, and the glass is smoked (darker than
                // the symbol, never whitened) so the white reads over light wallpapers too.
                var c = IconLook.Style(art, 2, blue, dark);
                byte[] glyph = at(c, 128, 128), glass = at(c, 128, 50);
                Need(glyph[3] > 220 && glyph[2] > 200 && glyph[1] > 200 && glyph[0] > 200, "the clear icon's symbol isn't white");
                Need(glass[3] < 150, "the clear icon's glass isn't see-through");
                Need(glass[2] < glass[3] * 0.75, "the clear icon's glass is whitened, so a white symbol would vanish on it");
                // Liquid Glass in colour: the symbol stays as drawn (white here), the tile becomes see-through glass
                // that keeps the tile's own red.
                var g3 = IconLook.Style(art, 3, blue, dark);
                byte[] sym3 = at(g3, 128, 128), tile3 = at(g3, 128, 50);
                Need(sym3[3] > 220 && sym3[2] > 200 && sym3[1] > 200 && sym3[0] > 200, "the colour glass icon's symbol lost its own colour");
                Need(tile3[3] < 215 && tile3[3] > 60, "the colour glass icon's tile isn't see-through glass");
                Need(tile3[2] > tile3[1] * 2 && tile3[2] > tile3[0] * 2, "the colour glass icon's tile lost the icon's red");
            }

            // Wallpaper icon colours: every colour in the picture is found, even a small accent, in hue order.
            {
                int pw = 120, ph = 80; var pic = new byte[pw * ph * 4];
                for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++)
                {
                    int o = (y * pw + x) * 4; byte r, g, b;
                    if (x >= 54 && x < 62 && y >= 36 && y < 44) { r = 230; g = 0; b = 230; }      // a small magenta accent
                    else if (x < 60) { if (y < 40) { r = 220; g = 30; b = 30; } else { r = 30; g = 60; b = 220; } }
                    else { if (y < 40) { r = 40; g = 200; b = 60; } else { r = 240; g = 220; b = 20; } }
                    pic[o] = b; pic[o + 1] = g; pic[o + 2] = r; pic[o + 3] = 255;
                }
                var pal = IconLook.Extract(pic, pw, ph, 8);
                Func<double, bool> has = want => pal.Any(c => { double hh, ss, ll; IconLook.ToHsl(c, out hh, out ss, out ll); double dh = Math.Abs(hh - want); return Math.Min(dh, 360 - dh) < 14 && ss > 0.5; });
                Need(has(0) && has(130) && has(229) && has(54), "wallpaper colours: a big area's colour is missing");
                Need(has(300), "wallpaper colours: the small accent colour is missing");
                // hue order around the wheel, starting after its widest gap (green 130 -> blue 229 here), so the sweep goes
                // blue, magenta, red, yellow, green with a single wrap past red
                int wraps = 0;
                for (int i = 1; i < pal.Length; i++) { double h0, h1, s, l; IconLook.ToHsl(pal[i - 1], out h0, out s, out l); IconLook.ToHsl(pal[i], out h1, out s, out l); if (h1 < h0) wraps++; }
                double firstHue, fs, fl; IconLook.ToHsl(pal[0], out firstHue, out fs, out fl);
                Need(wraps <= 1 && Math.Abs(firstHue - 229) < 14, "wallpaper colours don't sweep around the colour wheel from its widest gap");
                foreach (var c in pal) { double hh, ss, ll; IconLook.ToHsl(c, out hh, out ss, out ll); Need(ll >= 0.33 && ll <= 0.65, "a wallpaper colour is too dark or too pale for an icon"); }
                var back = IconLook.FromHsl(210, 0.8, 0.5); double bh, bs, bl; IconLook.ToHsl(back, out bh, out bs, out bl);
                Need(Math.Abs(bh - 210) < 1.5 && Math.Abs(bs - 0.8) < 0.02 && Math.Abs(bl - 0.5) < 0.01, "HSL conversion doesn't round-trip");
            }

            // Liquid Glass lens (ported from Kyant's AndroidLiquidGlass): the rounded-rectangle distance and normal,
            // grey stays grey, the middle isn't bent, and near the rim the glass shows what's further in.
            Need(Math.Abs(Liquid.SdRoundRect(0, 0, 10, 10, 3) + 10) < 1e-9 && Math.Abs(Liquid.SdRoundRect(10, 0, 10, 10, 3)) < 1e-9 && Math.Abs(Liquid.SdRoundRect(12, 0, 10, 10, 3) - 2) < 1e-9, "rounded-rectangle distance is wrong");
            double gx, gy; Liquid.GradRoundRect(9, 0, 10, 10, 3, out gx, out gy);
            Need(Math.Abs(gx - 1) < 1e-9 && Math.Abs(gy) < 1e-9, "the right edge's normal doesn't point right");
            int W = 100, H = 40;
            var grey = new byte[W * H * 4]; for (int i = 0; i < grey.Length; i++) grey[i] = 128;
            var flat = Liquid.Lens(grey, W, H, new Rect(10, 5, 80, 30), 15, 10, 14, true);
            var fp = new byte[4]; flat.CopyPixels(new Int32Rect(78, 12, 1, 1), fp, 4, 0);
            Need(Math.Abs(fp[0] - 128) <= 1 && Math.Abs(fp[2] - 128) <= 1, "the lens changed a plain grey backdrop");
            var ramp = new byte[W * H * 4];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int o = (y * W + x) * 4; ramp[o] = ramp[o + 1] = ramp[o + 2] = (byte)(x * 2); ramp[o + 3] = 255; }
            var bent = Liquid.Lens(ramp, W, H, new Rect(10, 5, 80, 30), 15, 10, 14, false, 0, 1.0);
            var centre = new byte[4]; bent.CopyPixels(new Int32Rect(40, 15, 1, 1), centre, 4, 0);
            var rim = new byte[4]; bent.CopyPixels(new Int32Rect(78, 15, 1, 1), rim, 4, 0);
            Need(Math.Abs(centre[0] - (50 * 2 + 1)) <= 2, "the lens bends the middle of the glass");
            Need(rim[0] > 78 * 2 + 4, "near the rim the lens doesn't pull in what's outside the glass");

            // Settings search: the exact option first, then options starting with it; a search that only matches where
            // things live ("hot corner") lists the settings there, not every menu item inside them.
            {
                Func<string, string, string, string, SettingsWindow.HitKind, SettingsWindow.Hit> hit = (text, ctx, sec, pane, kind) =>
                    new SettingsWindow.Hit { Text = text, Context = ctx, Section = sec, Pane = pane, Kind = kind };
                var exact = hit("Magnification", null, "Dock", "Desktop & Dock", SettingsWindow.HitKind.Option);
                var longer = hit("Magnified size", null, "Dock", "Desktop & Dock", SettingsWindow.HitKind.Option);
                Need(SettingsWindow.Score(exact, "magnification") > SettingsWindow.Score(longer, "magnif"), "search doesn't put the exact option first");
                Need(SettingsWindow.Score(exact, "magnif") > SettingsWindow.Score(longer, "magnif"), "search doesn't prefer the shorter, closer option");
                Need(SettingsWindow.Score(hit("Liquid Glass (colors)", "Dock icon style", "Icons", "Appearance", SettingsWindow.HitKind.Choice), "liquid glass colors") >= 1000, "search can't find a menu choice by its exact name");
                Need(SettingsWindow.Score(hit("Top left", null, "Hot Corners", "Desktop & Dock", SettingsWindow.HitKind.Option), "hot corner") > 0, "search doesn't find settings by their section");
                Need(SettingsWindow.Score(hit("Desktop", "Top left", "Hot Corners", "Desktop & Dock", SettingsWindow.HitKind.Choice), "hot corner") == 0, "search lists every menu item of a matching section");
                Need(SettingsWindow.Score(exact, "zzqx") == 0, "search matches nonsense");
            }

            // Drag to tile: the screen's edges and corners (macOS 15), and Alt anywhere.
            {
                var scr = new Sys.Display { Bounds = new Native.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }, Work = new Native.RECT { Left = 0, Top = 30, Right = 1920, Bottom = 1000 } };
                Func<int, int, Native.POINT> pt = (x, y) => new Native.POINT { X = x, Y = y };
                Need(DragTile.Edge(scr, pt(0, 540)) == Tiling.Spot.Left && DragTile.Edge(scr, pt(1919, 540)) == Tiling.Spot.Right, "dragging to a side doesn't tile a half");
                Need(DragTile.Edge(scr, pt(960, 0)) == Tiling.Spot.Fill, "dragging to the top doesn't fill the screen");
                Need(DragTile.Edge(scr, pt(0, 20)) == Tiling.Spot.TopLeft && DragTile.Edge(scr, pt(1919, 1079)) == Tiling.Spot.BottomRight && DragTile.Edge(scr, pt(20, 0)) == Tiling.Spot.TopLeft, "corners don't tile quarters");
                Need(DragTile.Edge(scr, pt(960, 1079)) == null && DragTile.Edge(scr, pt(960, 540)) == null, "the bottom edge (the Dock's) or the middle tiles a window");
                Need(DragTile.Anywhere(scr, pt(100, 500)) == Tiling.Spot.Left && DragTile.Anywhere(scr, pt(960, 500)) == Tiling.Spot.Fill && DragTile.Anywhere(scr, pt(1800, 950)) == Tiling.Spot.BottomRight, "Alt-dragging picks the wrong tile");
            }

            Console.WriteLine("macOS 27: Liquid Glass slider, tiling, Mission Control layout, Genie path, icon styles, Liquid Glass lens, Settings search and drag-to-tile zones passed");
        }
    }
}
