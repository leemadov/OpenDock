using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenDock
{
    // Live window previews above a dock icon, like hovering a taskbar button: one card per window with a live
    // DWM thumbnail; click to switch to it, × to close it.
    class PreviewWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct THUMB_PROPS { public int dwFlags; public Native.RECT rcDestination, rcSource; public byte opacity; public bool fVisible, fSourceClientAreaOnly; }
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out Native.SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMB_PROPS p);

        const double CardW = 236, ThumbH = 140;
        public DockItem Item;
        public Func<DockItem, bool> StillHovered = it => false;
        readonly StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
        readonly List<IntPtr> thumbs = new List<IntPtr>();
        readonly DispatcherTimer hideSoon = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        IntPtr hwnd;

        public PreviewWindow()
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; FontFamily = Theme.Font;
            SetResourceReference(BackgroundProperty, "WinBg");
            Glass.Attach(this, "WinGlass");
            Content = new Border { Padding = new Thickness(8), Child = row };
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd, true);
                int round = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);       // Windows 11 rounded corners + shadow
                int dark = Theme.Dark ? 1 : 0; Native.DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
            };
            hideSoon.Tick += delegate { hideSoon.Stop(); if (!IsMouseOver && !StillHovered(Item)) Close2(); };
            MouseLeave += delegate { hideSoon.Stop(); hideSoon.Start(); };
            MouseEnter += delegate { hideSoon.Stop(); };
        }

        public bool MouseInside
        {
            get
            {
                if (!IsVisible) return false;
                Native.POINT p; Native.GetCursorPos(out p);
                return DisplayLayout.WindowPixels(this).Contains(new Point(p.X, p.Y));
            }
        }

        // anchor: screen point (DIP) at the top centre of the dock icon
        public void ShowFor(DockItem item, List<IntPtr> windows, ImageSource icon, Point anchor, int position, Rect area, double scale = 1)
        {
            hideSoon.Stop();
            Unregister();
            row.Children.Clear();
            Item = item;
            row.Orientation = position == 0 ? Orientation.Horizontal : Orientation.Vertical;
            int capacity = position == 0 ? (int)((area.Width - 16) / CardW) : (int)((area.Height - 16) / (ThumbH + 46));
            foreach (var w in windows.Take(Math.Max(1, Math.Min(8, capacity)))) row.Children.Add(Card(w, icon));
            if (!IsVisible) Show();
            UpdateLayout();
            var at = DisplayLayout.Beside(anchor, new Size(ActualWidth, ActualHeight), position, area);
            DisplayLayout.Place(this, new Rect(at.X * scale, at.Y * scale, ActualWidth * scale, ActualHeight * scale));
            UpdateLayout();
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            foreach (Border card in row.Children) Attach(card);
        }

        FrameworkElement Card(IntPtr w, ImageSource icon)
        {
            var title = new TextBlock { Text = Native.WindowText(w), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");
            var close = new Button { Content = "✕", FontSize = 11, Width = 22, Height = 22, Padding = new Thickness(0), Style = (Style)Application.Current.Resources["Flat"], Opacity = 0, ToolTip = "Close window" };
            DockPanel.SetDock(close, Dock.Right);
            var head = new DockPanel { Height = 24, Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(close); head.Children.Add(title);
            // Minimized windows have no live picture; show the app icon instead, like the taskbar does.
            var host = new Grid { Width = CardW - 16, Height = ThumbH, Tag = w };
            if (Native.IsIconic(w)) host.Children.Add(new Image { Source = icon, Width = 64, Height = 64, Opacity = 0.8 });
            var body = new StackPanel();
            body.Children.Add(head); body.Children.Add(host);
            var card = new Border { Width = CardW, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = body, Tag = w, Cursor = Cursors.Hand };
            card.MouseEnter += delegate { card.SetResourceReference(Border.BackgroundProperty, "Hover"); close.Opacity = 1; };
            card.MouseLeave += delegate { card.Background = Brushes.Transparent; close.Opacity = 0; };
            card.MouseLeftButtonUp += delegate { Close2(); Native.Activate(w); };
            close.Click += (s, e) =>
            {
                Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                e.Handled = true;
                card.Visibility = Visibility.Collapsed;
                if (row.Children.Cast<UIElement>().All(c => c.Visibility != Visibility.Visible)) Close2();
                else Dispatcher.BeginInvoke(new Action(() => { foreach (Border c in row.Children) Attach(c); }), DispatcherPriority.Loaded);
            };
            return card;
        }

        // Places the live thumbnail of the card's window into its picture area, keeping the aspect ratio.
        void Attach(Border card)
        {
            var w = (IntPtr)card.Tag;
            var host = (Grid)((StackPanel)card.Child).Children[1];
            if (card.Visibility != Visibility.Visible || Native.IsIconic(w)) return;
            IntPtr thumb;
            if (DwmRegisterThumbnail(hwnd, w, out thumb) != 0) return;
            thumbs.Add(thumb);
            Native.SIZE src; DwmQueryThumbnailSourceSize(thumb, out src);
            var origin = host.TransformToAncestor(this).Transform(new Point(0, 0));
            double d = DisplayLayout.Scale(this), bw = host.Width * d, bh = host.Height * d;
            double s = src.cx > 0 && src.cy > 0 ? Math.Min(bw / src.cx, bh / src.cy) : 1;
            double tw = src.cx * s, th = src.cy * s, x = origin.X * d + (bw - tw) / 2, y = origin.Y * d + (bh - th) / 2;
            var p = new THUMB_PROPS
            {
                dwFlags = 0x1 | 0x4 | 0x8,   // destination, opacity, visible
                rcDestination = new Native.RECT { Left = (int)x, Top = (int)y, Right = (int)(x + tw), Bottom = (int)(y + th) },
                opacity = 255, fVisible = true
            };
            DwmUpdateThumbnailProperties(thumb, ref p);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.BeginInvoke(new Action(() => { if (IsVisible) { Unregister(); UpdateLayout(); foreach (Border card in row.Children) Attach(card); } }), DispatcherPriority.Loaded);
        }

        void Unregister()
        {
            foreach (var t in thumbs) DwmUnregisterThumbnail(t);
            thumbs.Clear();
        }

        public void MaybeHide() { if (!hideSoon.IsEnabled) hideSoon.Start(); }

        public void Close2()
        {
            hideSoon.Stop();
            Unregister();
            Item = null;
            Hide();
        }
    }
}
