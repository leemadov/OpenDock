using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace OpenDock
{
    // macOS sheets: a dialog that slides down out of its window's toolbar instead of popping up as a separate
    // box, and slides back up when it's done. With no window to hang from (the menu bar, the Dock) it shows as a
    // macOS alert instead: centred on the screen, scaling in. Either way it blocks like MessageBox.Show, so it can
    // stand in for it anywhere.
    static class Sheet
    {
        const double Toolbar = 52;   // MacWindow's title-bar height: sheets come out from under it

        // A message with buttons; returns the index of the button pressed, or `cancel` for Esc.
        // The last button is the default (Enter), as on the Mac.
        public static int Ask(Window owner, string title, string message, string[] buttons, int cancel = 0)
        {
            int result = cancel;
            var body = new StackPanel();
            body.Children.Add(Label(title, 15, "Fg", FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(message)) { var m = Label(message, 13, "FgDim", FontWeights.Normal); m.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(m); }
            var row = Buttons(body);
            var w = new SheetWindow(owner, body);
            for (int i = 0; i < buttons.Length; i++)
            {
                int index = i;
                var b = Capsule(buttons[i], i == buttons.Length - 1);
                b.Click += delegate { result = index; w.Finish(true); };
                row.Children.Add(b);
            }
            w.ShowDialog();
            return result;
        }

        // An OK-only notice (for errors), in place of MessageBox.Show(text, caption).
        public static void Tell(Window owner, string title, string message)
        {
            Ask(owner, title, message, new[] { "OK" });
        }

        // Yes/no; true when the confirming button (the default) was pressed.
        public static bool Confirm(Window owner, string title, string message, string yes)
        {
            return Ask(owner, title, message, new[] { "Cancel", yes }) == 1;
        }

        // A text field (e.g. Rename). `accept` returns an error to show under the field, or null when it took
        // the text. `select` is how much of the text starts selected.
        public static bool Prompt(Window owner, string title, string text, string action, Func<string, string> accept, int select = -1)
        {
            bool ok = false;
            var body = new StackPanel();
            body.Children.Add(Label(title, 15, "Fg", FontWeights.SemiBold));
            var input = new TextBox { Text = text, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8, 6, 8, 6), FontSize = 14, BorderThickness = new Thickness(1) };
            input.SetResourceReference(Control.BackgroundProperty, "Field"); input.SetResourceReference(Control.ForegroundProperty, "Fg");
            input.SetResourceReference(Control.BorderBrushProperty, "Accent"); input.SetResourceReference(TextBoxBase.CaretBrushProperty, "Fg");
            body.Children.Add(input);
            var error = Label("", 12, "FgDim", FontWeights.Normal); error.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)); error.Margin = new Thickness(0, 6, 0, 0); error.Visibility = Visibility.Collapsed;
            body.Children.Add(error);
            var row = Buttons(body);
            var w = new SheetWindow(owner, body);
            var cancel = Capsule("Cancel", false); cancel.Click += delegate { w.Finish(false); };
            var go = Capsule(action, true);
            go.Click += delegate
            {
                string problem;
                try { problem = accept(input.Text); } catch (Exception ex) { problem = ex.Message; }
                if (problem == null) { ok = true; w.Finish(true); return; }
                error.Text = problem; error.Visibility = Visibility.Visible; input.Focus();
            };
            input.TextChanged += delegate { error.Visibility = Visibility.Collapsed; };
            row.Children.Add(cancel); row.Children.Add(go);
            w.Shown += delegate { input.Focus(); input.Select(0, select < 0 ? input.Text.Length : Math.Min(select, input.Text.Length)); };
            w.ShowDialog();
            return ok;
        }

        // A hidden field for a passcode (digits only, shown as dots). `check` returns an error to show, or null to
        // accept. Returns what was entered, or null when cancelled.
        public static string Secret(Window owner, string title, string message, string action, Func<string, string> check)
        {
            string result = null;
            var body = new StackPanel();
            body.Children.Add(Label(title, 15, "Fg", FontWeights.SemiBold));
            if (!string.IsNullOrEmpty(message)) { var m = Label(message, 13, "FgDim", FontWeights.Normal); m.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(m); }
            var input = new PasswordBox { MaxLength = Passcode.MaxLength, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(8, 6, 8, 6), FontSize = 16, BorderThickness = new Thickness(1) };
            input.SetResourceReference(Control.BackgroundProperty, "Field"); input.SetResourceReference(Control.ForegroundProperty, "Fg");
            input.SetResourceReference(Control.BorderBrushProperty, "Accent");
            input.PreviewTextInput += (s, e) => { e.Handled = e.Text.Any(c => c < '0' || c > '9'); };   // digits only
            DataObject.AddPastingHandler(input, (s, e) => e.CancelCommand());
            body.Children.Add(input);
            var error = Label("", 12, "FgDim", FontWeights.Normal); error.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)); error.Margin = new Thickness(0, 6, 0, 0); error.Visibility = Visibility.Collapsed;
            body.Children.Add(error);
            var row = Buttons(body);
            var w = new SheetWindow(owner, body);
            var cancel = Capsule("Cancel", false); cancel.Click += delegate { w.Finish(false); };
            var go = Capsule(action, true);
            go.Click += delegate
            {
                string problem = check == null ? null : check(input.Password);
                if (problem == null) { result = input.Password; w.Finish(true); return; }
                error.Text = problem; error.Visibility = Visibility.Visible; input.Clear(); input.Focus();
            };
            input.PasswordChanged += delegate { error.Visibility = Visibility.Collapsed; };
            row.Children.Add(cancel); row.Children.Add(go);
            w.Shown += delegate { input.Focus(); };
            w.ShowDialog();
            return result;
        }

        static TextBlock Label(string s, double size, string brush, FontWeight weight)
        {
            var t = new TextBlock { Text = s, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, FontFamily = Theme.Font };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        static StackPanel Buttons(StackPanel body)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            body.Children.Add(row);
            return row;
        }

        static Style capsule;
        // macOS 26 alert buttons: capsules; the default one filled with the accent colour.
        static Button Capsule(string text, bool primary)
        {
            if (capsule == null)
                capsule = (Style)XamlReader.Parse(@"<Style TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'><Setter.Value>
    <ControlTemplate TargetType='Button'>
      <Grid>
        <Border CornerRadius='14' Background='{TemplateBinding Background}'/>
        <Border x:Name='Shade' CornerRadius='14' Background='#000000' Opacity='0'/>
        <ContentPresenter Margin='18,6,18,6' HorizontalAlignment='Center' VerticalAlignment='Center' TextBlock.Foreground='{TemplateBinding Foreground}'/>
      </Grid>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Shade' Property='Opacity' Value='0.06'/></Trigger>
        <Trigger Property='IsPressed' Value='True'><Setter TargetName='Shade' Property='Opacity' Value='0.16'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate>
  </Setter.Value></Setter>
</Style>");
            var b = new Button { Content = new TextBlock { Text = text, FontSize = 13, FontFamily = Theme.Font, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal },
                Style = capsule, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsDefault = primary };
            if (primary) { b.SetResourceReference(Control.BackgroundProperty, "Accent"); b.Foreground = Brushes.White; }
            else { b.SetResourceReference(Control.BackgroundProperty, "Capsule"); b.SetResourceReference(Control.ForegroundProperty, "Fg"); }
            return b;
        }

        // The sheet itself: a borderless see-through window sitting just under the owner's toolbar. The card slides
        // down inside it, clipped at the top edge, so it looks like it comes out from under the toolbar.
        class SheetWindow : Window
        {
            readonly Border card;
            readonly TranslateTransform slide = new TranslateTransform();
            readonly ScaleTransform grow = new ScaleTransform(1, 1);
            readonly bool attached;
            bool closing, done;
            public event EventHandler Shown;

            const double Pad = 24;   // room for the shadow

            public SheetWindow(Window owner, UIElement body)
            {
                WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
                ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.Height; Width = 420 + Pad * 2;
                Topmost = owner == null; FontFamily = Theme.Font;
                attached = owner != null && owner.IsVisible && owner.WindowState == WindowState.Normal && owner.ActualWidth >= 360;
                if (owner != null && owner.IsVisible) Owner = owner;
                WindowStartupLocation = WindowStartupLocation.Manual;

                card = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = new Thickness(22, 20, 20, 16), Child = body,
                    Margin = new Thickness(Pad, attached ? 0 : Pad, Pad, Pad),
                    Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.28 } };
                card.SetResourceReference(Border.BackgroundProperty, "Glass");
                card.SetResourceReference(Border.BorderBrushProperty, "CapsuleEdge");
                if (attached) card.RenderTransform = slide;
                else { card.RenderTransform = grow; card.RenderTransformOrigin = new Point(0.5, 0.5); card.Opacity = 0; }
                var clip = new Grid { ClipToBounds = true };
                clip.Children.Add(card);
                Content = clip;

                PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; Finish(false); } };
                Loaded += delegate { Place(); Open(); };
                Closing += (s, e) => { if (!done) { e.Cancel = true; Finish(false); } };
            }

            void Place()
            {
                if (attached)
                {
                    Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                    Top = Owner.Top + Toolbar;
                }
                else
                {
                    var area = SystemParameters.WorkArea;
                    Left = area.Left + (area.Width - ActualWidth) / 2;
                    Top = area.Top + area.Height * 0.28 - ActualHeight / 2;
                }
            }

            void Open()
            {
                // Liquid Glass: what's behind the card's place (the card is still out of sight), under a light tint
                try
                {
                    UpdateLayout();
                    double dpi = App.Dpi;
                    var at = PointToScreen(new Point(card.Margin.Left, card.Margin.Top));
                    var glass = Liquid.Card((int)at.X, (int)at.Y, (int)(card.ActualWidth * dpi), (int)(card.ActualHeight * dpi), 18 * dpi, dpi);
                    if (glass != null) { card.Background = Glass.Slice(glass, new Rect(0, 0, 1, 1), (Brush)Application.Current.Resources["MenuGlass"]); Liquid.Ink(card, Liquid.Brightness(glass)); }
                }
                catch (Exception ex) { App.Log(ex); }
                if (attached)
                {
                    slide.Y = -ActualHeight;
                    slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-ActualHeight, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
                }
                else
                {
                    var t = TimeSpan.FromMilliseconds(220);
                    var e = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
                    grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.88, 1, t) { EasingFunction = e });
                    grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1, t) { EasingFunction = e });
                    card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
                }
                if (Shown != null) Shown(this, EventArgs.Empty);
            }

            // Slides (or fades) away, then closes with the given result.
            public void Finish(bool result)
            {
                if (closing) return;
                closing = true;
                var t = TimeSpan.FromMilliseconds(attached ? 220 : 140);
                var a = attached
                    ? new DoubleAnimation(0, -ActualHeight, t) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } }
                    : new DoubleAnimation(1, 0, t);
                a.Completed += delegate { done = true; DialogResult = result; };
                if (attached) slide.BeginAnimation(TranslateTransform.YProperty, a);
                else card.BeginAnimation(OpacityProperty, a);
            }
        }
    }
}
