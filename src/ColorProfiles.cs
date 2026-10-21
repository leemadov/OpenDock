using System;
using System.Runtime.InteropServices;

namespace OpenDock
{
    // Screen colour profiles (Vivid, OLED, Studio...). Windows' magnification API can run one 5×5 colour matrix
    // over everything on screen (it's what Windows' own colour filters use), so a profile is just a matrix built
    // from saturation, contrast, brightness and warmth. It only lasts while OpenDock runs: quit, and the screen
    // is back to normal. Full-screen exclusive games and protected video aren't affected.
    static class ColorProfiles
    {
        public class Profile
        {
            public string Name, About;
            public double Saturation, Contrast, Brightness, Warmth;   // 1 = unchanged; brightness -0.2..0.2; warmth -1 (cool) .. 1 (warm)
        }

        public static readonly Profile[] Presets =
        {
            new Profile { Name = "Standard", About = "Your display as it is", Saturation = 1, Contrast = 1 },
            new Profile { Name = "Vivid", About = "Richer, punchier colours", Saturation = 1.35, Contrast = 1.05 },
            new Profile { Name = "OLED", About = "Deeper blacks and extra contrast", Saturation = 1.15, Contrast = 1.18, Brightness = -0.03, Warmth = -0.1 },
            new Profile { Name = "Studio", About = "Calm, natural colour for photos and design", Saturation = 0.94, Contrast = 1, Warmth = 0.12 },
            new Profile { Name = "Cinema", About = "Warm, filmic contrast", Saturation = 1.05, Contrast = 1.1, Brightness = -0.02, Warmth = 0.3 },
            new Profile { Name = "Night", About = "Much warmer, easy on the eyes", Saturation = 1, Contrast = 1, Warmth = 0.85 },
            new Profile { Name = "Grayscale", About = "No colour at all", Saturation = 0, Contrast = 1 },
        };
        public const int CustomIndex = -1;   // Settings.ColorProfile value for the user's own profile

        public static Profile Custom()
        {
            return new Profile { Name = "Custom", Saturation = Settings.CustomSaturation / 100.0, Contrast = Settings.CustomContrast / 100.0,
                                 Brightness = Settings.CustomBrightness / 100.0, Warmth = Settings.CustomWarmth / 100.0 };
        }

        public static Profile Current() { int i = Settings.ColorProfile; return i == CustomIndex ? Custom() : Presets[i >= 0 && i < Presets.Length ? i : 0]; }

        // Row-vector convention (Windows' MAGCOLOREFFECT): out[col] = sum(in[row] * m[row, col]) + m[4, col].
        public static float[] Matrix(Profile p)
        {
            double[] lum = { 0.2126, 0.7152, 0.0722 };
            double t = Math.Max(-1, Math.Min(1, p.Warmth));
            // warm: less blue (and a little green); cool: less red
            double[] gain = t >= 0 ? new[] { 1.0, 1 - 0.06 * t, 1 - 0.22 * t } : new[] { 1 + 0.16 * t, 1 + 0.05 * t, 1.0 };
            var m = new float[25];
            for (int col = 0; col < 3; col++)
            {
                for (int row = 0; row < 3; row++)
                {
                    double sat = (1 - p.Saturation) * lum[row] + (row == col ? p.Saturation : 0);
                    m[row * 5 + col] = (float)(sat * p.Contrast * gain[col]);
                }
                m[4 * 5 + col] = (float)((0.5 * (1 - p.Contrast) + p.Brightness) * gain[col]);   // contrast pivots on mid-grey
            }
            m[3 * 5 + 3] = 1;   // alpha passes through
            m[4 * 5 + 4] = 1;
            return m;
        }

        [StructLayout(LayoutKind.Sequential)] struct MAGCOLOREFFECT { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] m; }
        [DllImport("Magnification.dll")] static extern bool MagInitialize();
        [DllImport("Magnification.dll")] static extern bool MagUninitialize();
        [DllImport("Magnification.dll")] static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT e);

        static bool started;

        // Applies the chosen profile (Standard switches the effect off entirely, so it costs nothing).
        public static void Apply()
        {
            try
            {
                var p = Current();
                bool plain = p.Saturation == 1 && p.Contrast == 1 && p.Brightness == 0 && p.Warmth == 0;
                if (plain) { if (started) { var id = new MAGCOLOREFFECT { m = Matrix(p) }; MagSetFullscreenColorEffect(ref id); MagUninitialize(); started = false; } return; }
                if (!started) started = MagInitialize();
                var e = new MAGCOLOREFFECT { m = Matrix(p) };
                if (!MagSetFullscreenColorEffect(ref e)) App.Log("Colour profile: Windows refused the colour effect");
            }
            catch (Exception ex) { App.Log(ex); }
        }

        public static void Stop()
        {
            if (!started) return;
            try { var id = new MAGCOLOREFFECT { m = Matrix(Presets[0]) }; MagSetFullscreenColorEffect(ref id); MagUninitialize(); } catch { }
            started = false;
        }
    }
}
