using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace OpenDock
{
    partial class SettingsWindow
    {
        // Settings > General > Mac Setup: pick what changes, apply it in one go, undo it all with one button.
        void MacSetupPane(StackPanel body)
        {
            body.Children.Add(Heading("Make Windows feel like a Mac"));
            var intro = Text("Choose what GlassDock changes. Everything here can be undone with Restore Windows Defaults, which puts back exactly what you had before.", 13, "FgDim");
            intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(4, 0, 4, 10);
            body.Children.Add(intro);

            var steps = MacSetup.Steps();
            var chosen = steps.ToDictionary(s => s.Key, s => s.Default);
            var group = Group(body);
            foreach (var s in steps)
            {
                var step = s;
                AddSwitch(group, step.Title, chosen[step.Key], v => chosen[step.Key] = v, false);
                var detail = Text(step.Detail, 12, "FgDim"); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(48, -6, 0, 6);
                group.Children.Add(detail);
            }

            body.Children.Add(Heading("Appearance"));
            var looks = Group(body);
            var look = new ComboBox { MinWidth = 180, ItemsSource = new[] { "Keep as it is", "Light", "Dark" }, SelectedIndex = 0 };
            var row = new DockPanel(); DockPanel.SetDock(look, Dock.Right);
            row.Children.Add(look); row.Children.Add(Text("Windows apps and GlassDock", 13, "Fg"));
            Row(looks, row);

            var status = Text(MacSetup.CanRestore ? "Mac Setup has been applied. You can change it again, or restore Windows' defaults." : "Nothing has been changed yet.", 13, "FgDim");
            status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(4, 14, 4, 8);
            body.Children.Add(status);

            var buttons = new WrapPanel();
            var restore = Pill("Restore Windows Defaults", null); restore.IsEnabled = MacSetup.CanRestore;
            var apply = Pill("Set Up", null);
            buttons.Children.Add(apply); buttons.Children.Add(restore);
            body.Children.Add(buttons);

            apply.Click += delegate
            {
                try
                {
                    bool? dark = look.SelectedIndex == 1 ? false : look.SelectedIndex == 2 ? (bool?)true : null;
                    MacSetup.Apply(steps.Where(s => chosen[s.Key]), dark);
                    status.Text = "Done. Your PC is set up like a Mac. Restore Windows Defaults undoes all of it.";
                    restore.IsEnabled = true;
                }
                catch (Exception ex) { App.Log(ex); status.Text = "Couldn't finish setting up: " + ex.Message; }
            };
            restore.Click += delegate
            {
                try { MacSetup.Restore(); status.Text = "Windows' own settings are back, as they were before Mac Setup."; restore.IsEnabled = false; }
                catch (Exception ex) { App.Log(ex); status.Text = "Couldn't restore: " + ex.Message; }
            };

            var note = Text("GlassDock uses only its own artwork: no Apple fonts, icons, sounds or wallpapers.", 12, "FgDim");
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(4, 16, 4, 4);
            body.Children.Add(note);
        }
    }
}
