using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace OpenDock
{
    partial class SettingsWindow
    {
        void Updates(StackPanel body)
        {
            body.Children.Add(Heading("OpenDock Updates"));
            var group = Group(body);
            InfoRow(group, "Installed version", Updater.CurrentTag);
            var status = Text("Check GitHub for a newer build.", 13, "FgDim"); status.TextWrapping = TextWrapping.Wrap;
            status.Margin = new Thickness(0, 8, 0, 10);
            var check = Pill("Check for Updates", null);
            var install = Pill("Download and Install", null); install.IsEnabled = false;
            var rollback = Pill("Restore Previous Build", null); rollback.IsEnabled = Updater.CanRollback;
            var controls = new WrapPanel(); controls.Children.Add(check); controls.Children.Add(install); controls.Children.Add(rollback);
            Row(group, status); Row(group, controls);
            Updater.Release candidate = null;
            AddSwitch(group, "Include preview releases", Settings.IncludePreviewUpdates, value =>
            {
                Settings.IncludePreviewUpdates = value; candidate = null; install.IsEnabled = false;
            });
            var description = Text("Updates download from this project's GitHub releases and verify the file's SHA-256 before installation. OpenDock restarts, retaining its settings and a copy of the previous executable.", 12, "FgDim");
            description.TextWrapping = TextWrapping.Wrap; description.Margin = new Thickness(4, 12, 4, 4); body.Children.Add(description);
            if (!string.IsNullOrEmpty(Updater.LastResult))
            {
                var result = Text(Updater.LastResult, 12, "FgDim"); result.TextWrapping = TextWrapping.Wrap; body.Children.Add(result);
            }
            body.Children.Add(Pill("View Releases on GitHub", () => Launcher.Shell(Updater.ReleasesPage)));
            var cancel = new CancellationTokenSource();
            RoutedEventHandler unloaded = null;
            unloaded = delegate { cancel.Cancel(); body.Unloaded -= unloaded; };
            body.Unloaded += unloaded;
            check.Click += async delegate
            {
                check.IsEnabled = false; install.IsEnabled = false; status.Text = "Checking GitHub…";
                try
                {
                    bool requestedPreview=Settings.IncludePreviewUpdates;
                    candidate = await Updater.Check(requestedPreview, cancel.Token);
                    if (cancel.IsCancellationRequested) return;
                    if(requestedPreview!=Settings.IncludePreviewUpdates) { candidate=null; status.Text="Update channel changed. Check again."; return; }
                    status.Text = candidate == null ? "You're up to date." : "Available: " + candidate.tag_name;
                    install.IsEnabled = candidate != null;
                }
                catch (Exception ex) { if (!cancel.IsCancellationRequested) status.Text = "Couldn't check for updates: " + ex.Message; }
                finally { if (!cancel.IsCancellationRequested) check.IsEnabled = true; }
            };
            install.Click += async delegate
            {
                if (candidate == null) return;
                check.IsEnabled = install.IsEnabled = rollback.IsEnabled = false;
                try
                {
                    Settings.Save();
                    var progress = new Progress<int>(value => { if (!cancel.IsCancellationRequested) status.Text = "Downloading… " + value + "%"; });
                    await Updater.Install(candidate, cancel.Token, progress);
                    App.Quit();
                }
                catch (Exception ex)
                {
                    if (!cancel.IsCancellationRequested) { status.Text = "Couldn't install: " + ex.Message; check.IsEnabled = install.IsEnabled = true; rollback.IsEnabled = Updater.CanRollback; }
                }
            };
            rollback.Click += delegate
            {
                try { Settings.Save(); Updater.Rollback(); App.Quit(); }
                catch (Exception ex) { status.Text = "Couldn't restore the previous build: " + ex.Message; }
            };
        }
    }
}
