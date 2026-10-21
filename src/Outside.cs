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
        static readonly DispatcherTimer watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        static bool wasDown;

        public static void Init()
        {
            watch.Tick += delegate { Tick(); };
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, e) => Track((Control)s)));
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler((s, e) => { if (e.OriginalSource == s) Track((Control)s); }));
        }

        static bool Down() { return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0; }

        static bool IsOpen(Control c) { var cm = c as ContextMenu; return cm != null ? cm.IsOpen : ((MenuItem)c).IsSubmenuOpen; }

        static void Track(Control c)
        {
            if (!open.Contains(c)) open.Add(c);
            wasDown = Down();   // the click that opened the menu doesn't count
            watch.Start();
        }

        static void Tick()
        {
            open.RemoveAll(c => !IsOpen(c));
            if (open.Count == 0) { watch.Stop(); return; }
            bool down = Down();
            if (down && !wasDown && !Inside())
                foreach (var c in open.ToList())
                {
                    var cm = c as ContextMenu;
                    if (cm != null) cm.IsOpen = false; else ((MenuItem)c).IsSubmenuOpen = false;
                }
            wasDown = down;
        }

        // Is the cursor over an open menu, or the menu bar title that opened one?
        static bool Inside()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            IntPtr under = Native.GetAncestor(WindowFromPoint(p), 2);
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
