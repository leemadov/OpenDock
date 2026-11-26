using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    static class NativeGlassChecks
    {
        static byte[] Pixels(BitmapSource image)
        {
            var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            return bytes;
        }

        static void Compare(BitmapSource managed, BitmapSource native)
        {
            if (managed.PixelWidth != native.PixelWidth || managed.PixelHeight != native.PixelHeight
                || managed.Format != native.Format || managed.DpiX != native.DpiX || managed.DpiY != native.DpiY)
                throw new Exception("C++ Liquid Glass bitmap metadata differs");
            var expected = Pixels(managed);
            var actual = Pixels(native);
            for (int i = 0; i < expected.Length; i++)
                if (expected[i] != actual[i])
                    throw new Exception("C++ Liquid Glass differs at byte " + i + ": " + expected[i] + " vs " + actual[i]);
        }

        public static int Run()
        {
            var one = new byte[] { 12, 34, 56, 255 };
            var probe = new byte[4];
            if (!NativeLiquid.Lens(one, probe, 1, 1, 1, 1, new Rect(0, 0, 1, 1), 0, 1, 0, false, 0, 1, 0, 1, 0)
                || !one.SequenceEqual(probe)
                || !NativeLiquid.Smear(one, (byte[])one.Clone(), 1, 1, 0, 1, 1))
                throw new Exception("C++ Liquid Glass DLL is missing or incompatible");

            var random = new Random(2701);
            var backdrop = new byte[1440 * 180 * 4];
            random.NextBytes(backdrop);
            int cases = 0;
            foreach (var bounds in new[] { new Rect(13, 24, 880, 71), new Rect(-9.75, 142.2, 73.5, 53.9),
                new Rect(15.5, 16.125, 39.5, 39.5), new Rect(1420, -8, 53, 85), new Rect(0, 0, 1, 1) })
            foreach (double radius in new[] { 0, 8.75, bounds.Height / 2 })
            foreach (double falloff in new[] { 0, 2.5 })
            foreach (bool disperses in new[] { false, true })
            {
                double depth = disperses ? 0.35 : 0, glint = disperses ? 0 : 1;
                var reference = Liquid.RenderLens(backdrop, 1440, 180, bounds, radius, 22.7, 37, disperses, depth, 1.5, 1, glint, falloff, false);
                var candidate = Liquid.RenderLens(backdrop, 1440, 180, bounds, radius, 22.7, 37, disperses, depth, 1.5, 1, glint, falloff, true);
                Compare(reference, candidate);
                foreach (double blur in new[] { 1.5, 3.4 })
                    Compare(Liquid.RenderSmear(reference, radius, 22.7, blur, false),
                            Liquid.RenderSmear(candidate, radius, 22.7, blur, true));
                cases++;
            }

            var transparent = new byte[29 * 37 * 4];
            random.NextBytes(transparent);
            var image = BitmapSource.Create(29, 37, 96, 96, PixelFormats.Bgra32, null, transparent, 29 * 4);
            Compare(Liquid.RenderSmear(image, 9.25, 7, 4, false), Liquid.RenderSmear(image, 9.25, 7, 4, true));
            Console.WriteLine("Byte-for-byte parity across {0} lens cases and {1} smear cases.", cases, cases * 2 + 1);

            var pane = new Rect(51.25, 67, 832, 68);
            Func<bool, BitmapSource> render = native => Liquid.RenderSmear(
                Liquid.RenderLens(backdrop, 1440, 180, pane, 23, 24, 33, false, 0, 1.5, 1, 1, 2.5, native),
                23, 24, 1.5, native);
            render(false);
            render(true);
            const int runs = 30;
            var managedTimes = new double[runs];
            var nativeTimes = new double[runs];
            var timer = new Stopwatch();
            for (int i = 0; i < runs; i++)
            {
                timer.Restart();
                render(false);
                managedTimes[i] = timer.Elapsed.TotalMilliseconds;
                timer.Restart();
                render(true);
                nativeTimes[i] = timer.Elapsed.TotalMilliseconds;
            }
            Array.Sort(managedTimes);
            Array.Sort(nativeTimes);
            Console.WriteLine("832 x 68 pane, median lens + smear: C# {0:F2} ms, C++ {1:F2} ms.",
                (managedTimes[runs / 2 - 1] + managedTimes[runs / 2]) / 2,
                (nativeTimes[runs / 2 - 1] + nativeTimes[runs / 2]) / 2);
            return 0;
        }
    }
}
