using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenDock
{
    // Closes OpenDock's menus (Background Apps, the dock's and the menu bar's) when you click anywhere outside them.
    // They open from windows that never take focus, so Windows doesn't dismiss them by itself. While a menu is
    // open, the mouse buttons are checked every 40ms; nothing runs when no menu is open.
    static class Outside
    {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT p);

        static readonly List<Control> open = new List<Control>();   // open ContextMenus, and MenuItems showing a submenu
        static readonly Dictionary<Window, Action> panels = new Dictionary<Window, Action>();   // open glass panels, and how to close them
        static readonly DispatcherTimer watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        static bool wasDown, clicked;

        // A tap quicker than the 40ms check would slip between two looks at the buttons, so while something is
        // open a low-level mouse hook also notes every press (and is removed the moment nothing is open).
        delegate IntPtr MouseProc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, MouseProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        static readonly MouseProc proc = (code, w, l) =>
        {
            int m = (int)w;
            if (code >= 0 && (m == 0x0201 /* LBUTTONDOWN */ || m == 0x0204 /* RBUTTONDOWN */)) clicked = true;
            return CallNextHookEx(hook, code, w, l);
        };
        static IntPtr hook;
        static void Listen(bool on)
        {
            if (on && hook == IntPtr.Zero) hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, proc, GetModuleHandle(null), 0);
            else if (!on && hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        }

        public static void Init()
        {
            watch.Tick += delegate { Tick(); };
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => Track((Control)s)));
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Track((Control)s); }));
        }

        // Glass panels (Control Center, Wi-Fi, Sound, Notification Center) close on a click anywhere else too, even
        // when Windows didn't let them take focus -- then they'd never hear that they lost it.
        public static void Watch(Window w, Action dismiss)
        {
            panels[w] = dismiss;
            wasDown = Down(); clicked = false;
            Listen(true);
            watch.Start();
        }

        public static bool MenuOpen { get { return open.Any(IsOpen); } }

        static bool Down() { return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0; }

        static bool IsOpen(Control c) { var cm = c as ContextMenu; return cm != null ? cm.IsOpen : ((MenuItem)c).IsSubmenuOpen; }

        static void Track(Control c)
        {
            if (!open.Contains(c)) open.Add(c);
            wasDown = Down(); clicked = false;   // the click that opened the menu doesn't count
            Listen(true);
            watch.Start();
        }

        static void Tick()
        {
            open.RemoveAll(c => !IsOpen(c));
            foreach (var w in panels.Keys.Where(w => !w.IsVisible).ToList()) panels.Remove(w);
            if (open.Count == 0 && panels.Count == 0) { watch.Stop(); Listen(false); clicked = false; return; }
            bool down = Down();
            bool press = (down && !wasDown) || clicked;
            clicked = false;
            if (press)
            {
                Native.POINT p;
                Native.GetCursorPos(out p);
                IntPtr under = Native.GetAncestor(WindowFromPoint(p), 2);
                bool inMenu = Inside(under);
                if (!inMenu)
                    foreach (var c in open.ToList())
                    {
                        var cm = c as ContextMenu;
                        if (cm != null) cm.IsOpen = false; else ((MenuItem)c).IsSubmenuOpen = false;
                    }
                // a click in a menu a panel opened (the timer's, the output picker) leaves the panel open
                if (!inMenu)
                    foreach (var pair in panels.ToList())
                        if (new WindowInteropHelper(pair.Key).Handle != under) pair.Value();
            }
            wasDown = down;
        }

        // Is the cursor over an open menu, or the menu bar title that opened one?
        static bool Inside(IntPtr under)
        {
            foreach (var c in open)
            {
                var roots = new List<Visual> { c };
                var mi = c as MenuItem;
                var popup = mi != null && mi.Template != null ? mi.Template.FindName("PART_Popup", mi) as Popup : null;
                if (popup != null && popup.Child != null) roots.Add(popup.Child);
                foreach (var v in roots)
                {
                    var src = PresentationSource.FromVisual(v) as HwndSource;
                    if (src != null && src.Handle == under) return true;
                }
            }
            return false;
        }
    }
}
