using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace OpenDock
{
    // The optional native DLL accelerates the same pixel pipeline on supported architectures.
    static class NativeLiquid
    {
        const string Library = "GlassDock.LiquidGlass.dll";
        static volatile bool unavailable;
        delegate void RenderRows(IntPtr src, IntPtr dst, int begin, int end);

        [StructLayout(LayoutKind.Sequential)]
        struct LensOptions
        {
            public double X, Y, Width, Height, Radius, Band, Amount, Depth, Saturation, Bevel, Glint, Falloff;
            public int Dispersion;
        }

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        static extern void GlassLensRows(IntPtr src, IntPtr dst, int sw, int sh, int w,
            ref LensOptions lens, int begin, int end);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory)]
        static extern void GlassSmearRows(IntPtr src, IntPtr dst, int w, int h, double radius,
            double band, int blur, int begin, int end);

        static bool InRange(double value, double low, double high) { return value >= low && value <= high; }

        public static bool Lens(byte[] src, byte[] dst, int sw, int sh, int w, int h, Rect r,
            double radius, double band, double amount, bool dispersion, double depth, double saturation,
            double bevel, double glint, double falloff)
        {
            if (unavailable || src == null || sw <= 0 || sh <= 0 || (long)sw * sh > src.Length / 4
                || !InRange(r.X, -1e8, 1e8) || !InRange(r.Y, -1e8, 1e8)
                || !InRange(r.Width, 1e-8, 1e8) || !InRange(r.Height, 1e-8, 1e8)
                || !InRange(radius, -1e8, 1e8) || !InRange(band, -1e8, 1e8) || !InRange(amount, -1e8, 1e8)
                || !InRange(depth, -1e8, 1e8) || !InRange(saturation, -1e8, 1e8) || !InRange(bevel, -1e8, 1e8)
                || !InRange(glint, -1e8, 1e8) || !InRange(falloff, -1e8, 1e8))
                return false;

            var options = new LensOptions
            {
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Radius = radius,
                Band = band, Amount = amount, Depth = depth, Saturation = saturation, Bevel = bevel,
                Glint = glint, Falloff = falloff, Dispersion = dispersion ? 1 : 0
            };
            return Run(src, dst, w, h, (input, output, begin, end) =>
            {
                var rowOptions = options;
                GlassLensRows(input, output, sw, sh, w, ref rowOptions, begin, end);
            });
        }

        public static bool Smear(byte[] src, byte[] dst, int w, int h, double radius, double band, int blur)
        {
            if (unavailable || !InRange(radius, 0, Math.Min(w, h) / 2.0) || !InRange(band, 1, 1e8)
                || blur < 1 || blur > 256)
                return false;

            return Run(src, dst, w, h, (input, output, begin, end) =>
                GlassSmearRows(input, output, w, h, radius, band, blur, begin, end));
        }

        static bool Run(byte[] src, byte[] dst, int w, int h, RenderRows render)
        {
            var input = GCHandle.Alloc(src, GCHandleType.Pinned);
            GCHandle output = default(GCHandle);
            try
            {
                output = GCHandle.Alloc(dst, GCHandleType.Pinned);
                IntPtr from = input.AddrOfPinnedObject(), to = output.AddrOfPinnedObject();
                int first = (long)w * h < 8192 ? h : 1;
                // Resolve the entry point on this thread so unavailable DLLs take the managed path.
                try { render(from, to, 0, first); }
                catch (DllNotFoundException) { unavailable = true; return false; }
                catch (BadImageFormatException) { unavailable = true; return false; }
                catch (EntryPointNotFoundException) { unavailable = true; return false; }

                if (first < h)
                {
                    int rowsPerBatch = Math.Max(4, h / Environment.ProcessorCount);
                    Parallel.For(0, (h - first - 1) / rowsPerBatch + 1, batch =>
                    {
                        int begin = first + batch * rowsPerBatch;
                        render(from, to, begin, Math.Min(h, begin + rowsPerBatch));
                    });
                }
                return true;
            }
            finally
            {
                if (output.IsAllocated) output.Free();
                input.Free();
            }
        }
    }
}
