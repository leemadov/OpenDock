using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OpenDock
{
    // macOS' Cmd+Tab on Alt+Tab: one icon per app in a glass bar, most recent first. Keep Alt held and press Tab
    // (Shift+Tab or the arrows to go back), let go of Alt to switch; Q quits the highlighted app, Esc cancels.
    // The keys are watched by a low-level keyboard hook on its own thread, so a busy UI thread can't make Windows
    // drop the hook.
    static class AppSwitcher
    {
        [StructLayout(LayoutKind.Sequential)] struct KBD { public uint vk, scan, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
        delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, Proc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

        static Proc proc;      // kept alive for the hook
        static IntPtr hook;
        static volatile bool active;
        static Dispatcher ui;
        static SwitcherWindow window;

        public static void Start()
        {
            ui = Dispatcher.CurrentDispatcher;
            var t = new Thread(() =>
            {
                proc = Callback;
                hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, proc, GetModuleHandle(null), 0);
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            }) { IsBackground = true, Name = "OpenDock keys" };
            t.Start();
        }

        static bool Down(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // Never throws: an exception here would end the hook thread and take OpenDock with it.
        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            try { var r = Handle(code, w, l); if (r != IntPtr.Zero) return r; }
            catch (Exception ex) { active = false; App.Log(ex); }
            return CallNextHookEx(hook, code, w, l);
        }

        // Non-zero swallows the key.
        static IntPtr Handle(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0 && Settings.AppSwitcher)
            {
                var k = (KBD)Marshal.PtrToStructure(l, typeof(KBD));
                int msg = (int)w;
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                bool injected = (k.flags & 0x10) != 0, alt = (k.flags & 0x20) != 0;
                if (!injected)
                {
                    if (k.vk == 0x09 && down && alt && !Down(0x11) && !Down(0x5B) && !Down(0x5C))
                    {
                        if (!active)
                        {
                            active = true;
                            // An unassigned key between Alt's press and release stops the app opening its menu bar.
                            Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero); Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        }
                        int step = Down(0x10) ? -1 : 1;
                        Post(() => Step(step));
                        return (IntPtr)1;
                    }
                    if (active)
                    {
                        if ((k.vk == 0x12 || k.vk == 0xA4 || k.vk == 0xA5) && up) { active = false; Post(Commit); }
                        else if (k.vk == 0x1B && down) { active = false; Post(Cancel); return (IntPtr)1; }
                        else if (k.vk == 0x09) return (IntPtr)1;
                        else if ((k.vk == 0x25 || k.vk == 0x27) && down) { int step = k.vk == 0x25 ? -1 : 1; Post(() => Step(step)); return (IntPtr)1; }
                        else if (k.vk == 0x51 && down) { Post(QuitSelected); return (IntPtr)1; }
                    }
                }
            }
            return IntPtr.Zero;
        }

        static void Post(Action a) { if (ui != null) ui.BeginInvoke(new Action(() => { try { a(); } catch (Exception ex) { App.Log(ex); } })); }

        static void Step(int by)
        {
            if (window == null) window = new SwitcherWindow();
            if (!window.Running) window.Begin();
            window.Move(by);
        }

        static void Commit() { if (window != null && window.Running) window.Finish(true); }
        static void Cancel() { if (window != null && window.Running) window.Finish(false); }
        static void QuitSelected() { if (window != null && window.Running) window.QuitSelected(); }
        public static void Click() { active = false; }   // a click on an icon switches without waiting for Alt
    }

    class SwitcherWindow : Window
    {
        class AppEntry { public string Key, Name; public List<IntPtr> Windows = new List<IntPtr>(); public ImageSource Icon; public Border Cell; public Image Image; }

        readonly StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        readonly TextBlock caption = new TextBlock { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        readonly DispatcherTimer appear = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        List<AppEntry> apps = new List<AppEntry>();
        int selected;
        double cell = 104;
        public bool Running;

        public SwitcherWindow()
        {
            Title = "OpenDock App Switcher";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font;
            SetResourceReference(BackgroundProperty, "WinBg");
            caption.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            Glass.Attach(this, "WinGlass");
            var body = new StackPanel { Margin = new Thickness(12, 12, 12, 8) };
            body.Children.Add(row); body.Children.Add(caption);
            Content = body;
            SourceInitialized += delegate
            {
                var h = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(h, true);
                int round = 2; Native.DwmSetWindowAttribute(h, 33, ref round, 4);
                int dark = Theme.Dark ? 1 : 0; Native.DwmSetWindowAttribute(h, 20, ref dark, 4);
            };
            appear.Tick += delegate { appear.Stop(); if (Running) Present(); };
        }

        public void Begin()
        {
            Running = true;
            apps = Collect();
            selected = 0;
            row.Children.Clear();
            var area = DisplayLayout.ActiveWorkArea;
            cell = Math.Max(56, Math.Min(104, (area.Width - 80) / Math.Max(1, apps.Count)));
            foreach (var a in apps) row.Children.Add(Cell(a));
            appear.Start();   // a quick Alt+Tab switches without the bar flashing up
        }

        // Apps in the order you last used them: windows come front to back, the first window of an app places it.
        List<AppEntry> Collect()
        {
            var list = new List<AppEntry>();
            var finders = App.Finders.Where(f => f.IsVisible).Select(f => new WindowInteropHelper(f).Handle).ToList();
            var known = App.Wins.GroupBy(w => w.Hwnd).ToDictionary(g => g.Key, g => g.First());
            var dock = App.Docks.FirstOrDefault();
            Native.EnumWindows((h, l) =>
            {
                string key, name = null; ImageSource icon = null;
                WinInfo w;
                if (finders.Contains(h)) { key = "opendock:finder"; name = "Finder"; icon = Art.Finder; }
                else if (known.TryGetValue(h, out w)) key = w.Key ?? w.Exe;
                else return true;
                if (key == null) return true;
                var e = list.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (e == null)
                {
                    if (name == null && dock != null) dock.Describe(h, out name, out icon);
                    if (name == null && known.ContainsKey(h)) name = known[h].IsFrameHost ? known[h].Title : Apps.FriendlyName(known[h].Exe);
                    e = new AppEntry { Key = key, Name = name ?? Native.WindowText(h), Icon = icon };
                    if (icon == null && known.ContainsKey(h))
                    {
                        var info = known[h];
                        var entry = e;
                        string path = info.IsFrameHost && info.Aumid != null ? "shell:AppsFolder\\" + info.Aumid : info.Exe;
                        if (path != null) Icons.Get(path, true, img => { entry.Icon = img; if (entry.Image != null) entry.Image.Source = img; });
                    }
                    list.Add(e);
                }
                e.Windows.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        Border Cell(AppEntry a)
        {
            double icon = cell * 0.78;
            a.Image = new Image { Source = a.Icon, Width = icon, Height = icon };
            RenderOptions.SetBitmapScalingMode(a.Image, BitmapScalingMode.HighQuality);
            a.Cell = new Border { Width = cell, Height = cell, CornerRadius = new CornerRadius(cell * 0.2), Child = a.Image, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            a.Cell.MouseEnter += delegate { Select(apps.IndexOf(a)); };
            a.Cell.MouseLeftButtonUp += delegate { Select(apps.IndexOf(a)); AppSwitcher.Click(); Finish(true); };
            return a.Cell;
        }

        public void Move(int by)
        {
            if (apps.Count == 0) return;
            // The first Tab goes to the app you were in before, like macOS.
            Select(((selected + by) % apps.Count + apps.Count) % apps.Count);
        }

        void Select(int i)
        {
            if (i < 0 || i >= apps.Count) return;
            selected = i;
            for (int k = 0; k < apps.Count; k++)
            {
                if (k == i) apps[k].Cell.SetResourceReference(Border.BackgroundProperty, "SelBg");
                else apps[k].Cell.Background = Brushes.Transparent;
            }
            caption.Text = apps[i].Name;
            caption.MaxWidth = Math.Max(cell * 2, apps.Count * cell);
        }

        void Present()
        {
            Show();
            UpdateLayout();
            var area = DisplayLayout.ActiveBounds;
            Left = area.Left + (area.Width - ActualWidth) / 2;
            Top = area.Top + (area.Height - ActualHeight) / 2;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
        }

        public void Finish(bool go)
        {
            Running = false;
            appear.Stop();
            BeginAnimation(OpacityProperty, null);
            Hide();
            if (!go || selected < 0 || selected >= apps.Count) return;
            var a = apps[selected];
            // Bring the whole app forward: its other windows first, then the one you used last on top.
            var shown = a.Windows.Where(h => !Native.IsIconic(h)).ToList();
            if (shown.Count == 0) { AppEffects.Restore(a.Windows[0]); return; }   // out of the Dock, Genie backwards
            for (int i = shown.Count - 1; i > 0; i--) Native.SetWindowPos(shown[i], IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 0x4000);
            Native.Activate(shown[0]);
        }

        public void QuitSelected()
        {
            if (selected < 0 || selected >= apps.Count) return;
            var a = apps[selected];
            foreach (var h in a.Windows) Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            row.Children.Remove(a.Cell);
            apps.RemoveAt(selected);
            if (apps.Count == 0) { Finish(false); return; }
            Select(Math.Min(selected, apps.Count - 1));
        }
    }
}
