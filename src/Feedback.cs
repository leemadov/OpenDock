using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Media;

namespace OpenDock
{
    // "Share your feedback": a small window where anyone can tell the developer what they think. Sending opens a
    // ready-written GitHub issue in the browser (labelled "feedback"), so the developer sees it on the project's
    // Issues page and the person reads exactly what's sent before it goes -- nothing is sent behind their back.
    // The GlassDock and Windows version can be included (on by default); no name, email, PC name or files ever are.
    class FeedbackWindow : MacWindow
    {
        static FeedbackWindow open;

        // The request belongs to one release: this one. It's asked once, as a notification after updating, and the
        // menu entries only exist while this version is installed -- the next release drops it unless it names it.
        public const string ForVersion = "v0.4.15";
        public static bool Active { get { return Updater.CurrentTag == ForVersion; } }

        public static void Offer()
        {
            if (!Active || Settings.FeedbackAsked == ForVersion) return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
            t.Tick += delegate
            {
                t.Stop();
                Settings.FeedbackAsked = ForVersion; Settings.Save();
                Notify.Post(new Note { App = "GlassDock", Title = "Please share your feedback", Body = "How is " + ForVersion + "? Tell the developer what you think. Click to write a note.", Glyph = "", Open = ShowIt });
            };
            t.Start();
        }
        const string Issues = "https://github.com/leemadov/OpenDock/issues/new";
        const int MaxChars = 3000;   // keeps the web address a browser will take

        public static void ShowIt()
        {
            if (!Active) return;
            if (open != null && open.IsLoaded)
            {
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }
            open = new FeedbackWindow();
            open.Show();
        }

        readonly TextBox message = new TextBox();
        readonly ComboBox kind = new ComboBox();
        readonly CheckBox details = new CheckBox();
        readonly TextBlock note = Text("", 12, "FgDim");

        FeedbackWindow() : base(true)
        {
            Title = "Share Your Feedback";
            Width = 520; Height = 540; MinWidth = 440; MinHeight = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Closed += delegate { if (open == this) open = null; };

            var page = new StackPanel { Margin = new Thickness(28, 0, 28, 24) };
            Layout.Children.Add(page);
            Layout.Children.Add(TrafficLights());

            var head = Text("Please share your feedback", 20, "Fg", FontWeights.Bold);
            head.Margin = new Thickness(0, 58, 0, 6);
            page.Children.Add(head);
            var intro = Text("An idea, a bug, something you love or something that annoys you: it all helps. Be as short or as detailed as you like.", 13, "FgDim");
            intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(0, 0, 0, 14);
            page.Children.Add(intro);

            kind.ItemsSource = new[] { "An idea", "Something isn't working", "Something I love", "Something else" };
            kind.SelectedIndex = 0; kind.Margin = new Thickness(0, 0, 0, 10); kind.HorizontalAlignment = HorizontalAlignment.Left; kind.MinWidth = 220;
            WindowChrome.SetIsHitTestVisibleInChrome(kind, true);
            page.Children.Add(kind);

            message.AcceptsReturn = true; message.TextWrapping = TextWrapping.Wrap; message.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            message.Height = 170; message.Padding = new Thickness(10, 8, 10, 8); message.FontSize = 13.5; message.MaxLength = MaxChars;
            message.SetResourceReference(Control.BackgroundProperty, "Field"); message.SetResourceReference(Control.ForegroundProperty, "Fg");
            message.SetResourceReference(TextBox.CaretBrushProperty, "Fg"); message.SetResourceReference(Control.BorderBrushProperty, "MenuSep");
            message.TextChanged += delegate { note.Text = ""; };
            WindowChrome.SetIsHitTestVisibleInChrome(message, true);
            page.Children.Add(message);

            details.Content = Text("Include my GlassDock and Windows version", 12.5, "Fg");
            details.IsChecked = true; details.Margin = new Thickness(0, 10, 0, 0);
            details.Style = (Style)Application.Current.Resources["Switch"];
            details.HorizontalAlignment = HorizontalAlignment.Stretch;
            WindowChrome.SetIsHitTestVisibleInChrome(details, true);
            page.Children.Add(details);

            var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
            var send = new Button { Content = Text("Send Feedback", 13.5, "Fg", FontWeights.SemiBold), Padding = new Thickness(22, 7, 22, 8), MinHeight = 34, Style = (Style)Application.Current.Resources["Pill"] };
            send.SetResourceReference(Control.BackgroundProperty, "SelBg");
            send.Click += delegate { Send(); };
            WindowChrome.SetIsHitTestVisibleInChrome(send, true);
            DockPanel.SetDock(send, Dock.Right);
            row.Children.Add(send);
            note.TextWrapping = TextWrapping.Wrap; note.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(note);
            page.Children.Add(row);

            var fine = Text("Sending opens your web browser with your message ready on GitHub, where you can read it before you submit. A free GitHub account is needed to submit.", 11.5, "FgDim");
            fine.TextWrapping = TextWrapping.Wrap; fine.Margin = new Thickness(0, 14, 0, 0);
            page.Children.Add(fine);
            Loaded += delegate { message.Focus(); };
        }

        static string Address(string tag, string first, string body)
        {
            return Issues + "?labels=feedback&title=" + Uri.EscapeDataString("Feedback (" + tag + "): " + first) + "&body=" + Uri.EscapeDataString(body);
        }

        void Send()
        {
            string text = (message.Text ?? "").Trim();
            if (text.Length == 0) { note.Text = "Write a message first."; message.Focus(); return; }
            string[] tags = { "idea", "problem", "praise", "other" };
            string tag = tags[Math.Max(0, kind.SelectedIndex)];
            string first = text.Replace("\r", " ").Replace("\n", " ");
            if (first.Length > 60) first = first.Substring(0, 57).TrimEnd() + "...";
            var body = text;
            if (details.IsChecked == true)
                body += "\n\n---\nGlassDock " + Updater.CurrentTag + ", Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " (64-bit)" : "");
            string url = Address(tag, first, body);
            bool cut = false;
            // a browser takes about 8,000 characters of web address: long messages (and ones in scripts that take many
            // characters each once encoded) are shortened to fit, and the full text stays on the clipboard
            while (url.Length > 7000 && body.Length > 40) { body = body.Substring(0, (int)(body.Length * 0.85)); cut = true; url = Address(tag, first, body); }
            try { Clipboard.SetText(text); } catch { }   // a copy of what was written, in case the browser page gets lost
            Launcher.Shell(url);
            note.Text = cut ? "Opened in your browser. It was too long to fit, so the end is cut; your full message is on the clipboard to paste." : "Opened in your browser. Thank you!";
        }
    }
}
