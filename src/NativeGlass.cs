using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace OpenDock
{
    // Isolate the Windows accent policy: it is not a stable public contract, so fail back to wallpaper
    // rather than hiding windows from capture. The sampled compatibility mode remains explicitly opt-in.
    static class NativeGlass
    {
        [StructLayout(LayoutKind.Sequential)] struct Accent { public int State, Flags, Color, Animation; }
        [StructLayout(LayoutKind.Sequential)] struct Data { public int Attribute; public IntPtr Value; public UIntPtr Size; }
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate bool SetAttribute(IntPtr window, ref Data data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string module);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        static readonly SetAttribute set = Load();

        static SetAttribute Load()
        {
            var address = GetProcAddress(GetModuleHandle("user32.dll"), "SetWindowCompositionAttribute");
            return address == IntPtr.Zero ? null : (SetAttribute)Marshal.GetDelegateForFunctionPointer(address, typeof(SetAttribute));
        }

        public static bool Apply(IntPtr window, bool enabled)
        {
            if (window == IntPtr.Zero || set == null || SystemParameters.HighContrast) return false;
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Accent)));
            try
            {
                // Disable Windows 11's automatic backdrop, whose inactive tint can cover the live accent blur.
                int none = 1; Native.DwmSetWindowAttribute(window, 38, ref none, 4);
                // Flags/color must stay zero: adding a tint here turns blur nearly opaque on current Windows.
                Marshal.StructureToPtr(new Accent { State = enabled ? 3 : 0 }, memory, false);
                var data = new Data { Attribute = 19, Value = memory, Size = new UIntPtr((uint)Marshal.SizeOf(typeof(Accent))) };
                if (!set(window, ref data)) return false;
                Native.SetWindowDisplayAffinity(window, 0);
                return enabled;
            }
            catch (Exception ex) { App.Log(ex); return false; }
            finally { Marshal.FreeHGlobal(memory); }
        }

        public static void ConfigureClient(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var chrome = WindowChrome.GetWindowChrome(window);
            // A full extended frame paints an opaque inactive surface over the accent on Windows 11.
            if (chrome != null) chrome.GlassFrameThickness = new Thickness(0);
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null && source.CompositionTarget != null)
            {
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            }
            var margins = new Margins();
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }

        public static void Refresh(IntPtr window)
        {
            // Windows can cache an accent backdrop while it is covered by a layered icon window.
            // Reset only the compositor policy; no screen capture or capture exclusion is involved.
            Apply(window, false); Apply(window, true);
        }
    }

    // Separate the composited pane from the dock's large transparent animation/input canvas.
    // This HWND is clipped to the pane, stays immediately behind its dock, and never takes focus.
    class GlassPaneWindow
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WindowClass
        {
            public uint Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background; public string Menu, Name;
        }
        delegate IntPtr WindowProc(IntPtr h, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int extended, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr h, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "ShowWindow")] static extern bool ShowNative(IntPtr window, int command);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int width, int height);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
        static readonly WindowProc procedure = (h, m, w, l) => m == 0x0084 ? new IntPtr(-1) : DefWindowProc(h, m, w, l);
        static readonly ushort windowClass = Register();
        static ushort Register()
        {
            var value = new WindowClass { Procedure = procedure, Name = "OpenDock.NativeGlass" };
            return RegisterClass(ref value);
        }
        Rect last = Rect.Empty;
        int radius;
        IntPtr hwnd;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        public GlassPaneWindow() { refresh.Tick += delegate { if (IsVisible) NativeGlass.Refresh(hwnd); }; }
        public IntPtr Handle { get { return hwnd; } }
        public bool IsVisible { get { return hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd); } }
        public bool Available { get; private set; }
        public void Hide() { refresh.Stop(); if (hwnd != IntPtr.Zero) ShowNative(hwnd, 0); }
        public void Close() { refresh.Stop(); if (hwnd != IntPtr.Zero) DestroyWindow(hwnd); hwnd = IntPtr.Zero; Available = false; }

        public bool Prepare(Rect bounds)
        {
            if (hwnd == IntPtr.Zero)
            {
                // No WPF swap chain: an empty native surface lets DWM continuously composite the real windows below.
                if (windowClass == 0) return false;
                hwnd = CreateWindowEx(0x082000A8, "OpenDock.NativeGlass", "OpenDock glass pane", 0x80000000, (int)bounds.Left, (int)bounds.Top, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            Available = NativeGlass.Apply(hwnd, true);
            return Available;
        }

        public void Behind(IntPtr dock)
        {
            if (hwnd != IntPtr.Zero && IsVisible) Native.SetWindowPos(hwnd, dock, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void Place(IntPtr dock, Rect bounds, double corner, bool shown)
        {
            if (!Available || !shown || bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) { if (IsVisible) Hide(); return; }
            int r = Math.Max(0, (int)Math.Round(corner));
            bool resized = last.IsEmpty || Math.Abs(last.Width - bounds.Width) > 0.5 || Math.Abs(last.Height - bounds.Height) > 0.5 || radius != r;
            bool newlyShown = !IsVisible;
            if (newlyShown) { ShowNative(hwnd, 4); refresh.Start(); }
            if (newlyShown || last != bounds || resized)
            {
                Native.SetWindowPos(hwnd, dock, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top), (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height), Native.SWP_NOACTIVATE);
                if (resized)
                {
                    var region = CreateRoundRectRgn(0, 0, (int)Math.Ceiling(bounds.Width) + 1, (int)Math.Ceiling(bounds.Height) + 1, r * 2, r * 2);
                    if (region != IntPtr.Zero && SetWindowRgn(hwnd, region, true) == 0) Native.DeleteObject(region);
                }
                last = bounds; radius = r;
            }
        }
    }
}
