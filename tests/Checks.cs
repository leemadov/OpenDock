using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace OpenDock
{
    // Run with: build.bat check
    static class Checks
    {
        static void LayoutChecks()
        {
            // Negative screen origins (left/above primary), and several system scaling factors.
            foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                App.Dpi = dpi;
                var area = DisplayLayout.Dip(new Native.RECT { Left = -1920, Top = -1080, Right = 0, Bottom = 0 });
                if (Math.Abs(area.Width * dpi - 1920) > 0.01 || Math.Abs(area.Left * dpi + 1920) > 0.01) throw new Exception("display DIP conversion");
                var p = DisplayLayout.Clamp(new Point(100, 100), new Size(300, 200), area);
                if (!area.Contains(new Rect(p, new Size(300, 200)))) throw new Exception("popup escaped negative-origin monitor");
                foreach (int position in new[] { 0, 1, 2 })
                {
                    var original = new Point(240, 120);
                    var roundtrip = DisplayLayout.FromWindow(DisplayLayout.ToWindow(original, position, 1080, 320), position, 1080, 320);
                    if ((roundtrip - original).Length > 0.01) throw new Exception("dock coordinate roundtrip");
                    var popup = DisplayLayout.Beside(new Point(area.Left + 10, area.Top + 10), new Size(300, 200), position, area);
                    if (!area.Contains(new Rect(popup, new Size(300, 200)))) throw new Exception("side popup escaped monitor");
                }
            }
            App.Dpi = 1;
            if (DisplayLayout.ToWindow(new Point(80, 320), 1, 1080, 320).X != 0 || DisplayLayout.ToWindow(new Point(80, 320), 2, 1080, 320).X != 320) throw new Exception("side dock screen edge");
            double fit = DisplayLayout.FitScale(768, 1500, 260);
            if (fit >= 1 || (1500 + 260) * fit > 768 - 24 + 0.01) throw new Exception("crowded dock doesn't fit");
            if (Settings.LiveGlass) throw new Exception("capture-safe glass must be the default");
        }

        static void RenameChecks()
        {
            string dir = Path.Combine(Path.GetTempPath(), "OpenDock-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "before.txt"); File.WriteAllText(file, "keep these bytes");
                foreach (string bad in new[] { "", " ", ".", "..", "../escape", "folder\\escape", "x:y", "end.", "end ", "CON.txt", "NUL", "COM1.log", "LPT9" })
                {
                    bool rejected = false;
                    try { FinderWindow.RenamePath(file, bad); } catch (ArgumentException) { rejected = true; }
                    if (!rejected || !File.Exists(file)) throw new Exception("unsafe rename accepted: " + bad);
                }
                string after = FinderWindow.RenamePath(file, "after.txt");
                if (File.Exists(file) || File.ReadAllText(after) != "keep these bytes") throw new Exception("file rename lost data");
                after = FinderWindow.RenamePath(after, "AFTER.txt");
                if (!Directory.GetFiles(dir).Any(f => Path.GetFileName(f) == "AFTER.txt")) throw new Exception("case-only file rename failed");
                string occupied = Path.Combine(dir, "occupied.txt"); File.WriteAllText(occupied, "original destination");
                bool collision = false;
                try { FinderWindow.RenamePath(after, "occupied.txt"); } catch (IOException) { collision = true; }
                if (!collision || File.ReadAllText(occupied) != "original destination" || File.ReadAllText(after) != "keep these bytes") throw new Exception("rename overwrote destination");
                string folder = Path.Combine(dir, "folder"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "child.txt"), "child");
                string renamed = FinderWindow.RenamePath(folder, "renamed");
                renamed = FinderWindow.RenamePath(renamed, "RENAMED");
                if (!File.Exists(Path.Combine(renamed, "child.txt")) || !Directory.GetDirectories(dir).Any(d => Path.GetFileName(d) == "RENAMED")) throw new Exception("directory rename lost contents or casing");
                Directory.CreateDirectory(Path.Combine(dir, "occupied"));
                collision = false;
                try { FinderWindow.RenamePath(renamed, "occupied"); } catch (IOException) { collision = true; }
                if (!collision || !File.Exists(Path.Combine(renamed, "child.txt"))) throw new Exception("directory collision changed source");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void Expect(bool clutter, string name, string id, string target)
        {
            if (Apps.Clutter(name, id, target) != clutter) throw new Exception((clutter ? "should hide: " : "should keep: ") + name);
        }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--inspect") return MultiDisplayChecks.Inspect(int.Parse(args[1]));
            Expect(true, "Steam Support Center", "http://support.steampowered.com/", "http://support.steampowered.com/");
            Expect(true, "Thunar File Manager (distro)", "Microsoft.AutoGenerated.{X}", @"C:\Program Files\WSL\wslg.exe");
            Expect(true, "Developer Command Prompt for VS 2022", "Microsoft.AutoGenerated.{X}", @"C:\WINDOWS\system32\cmd.exe");
            Expect(true, "Services", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\services.msc", @"C:\WINDOWS\system32\services.msc");
            Expect(true, "Windows PowerShell (x86)", "x", @"C:\WINDOWS\syswow64\WindowsPowerShell\v1.0\powershell.exe");
            Expect(true, "Python 3.13 Module Docs (64-bit)", "x", "python.exe");
            Expect(true, "Logs and errors", "x", Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            Expect(true, "Nsight Redistributable", @"C:\x\setup.msi", @"C:\x\setup.msi");
            Expect(true, "Run", "Microsoft.Windows.Shell.RunDialog", "::{2559A1F3-21D7-11D4-BDAF-00C04F60B9F0}");
            Expect(true, "Anaconda Prompt", "x", @"""C:\WINDOWS\System32\cmd.exe""");
            Expect(true, "Old Game Shortcut", "x", @"C:\definitely\missing\game.exe");
            Expect(true, "TreeSize Free (Administrator)", "x", null);
            Expect(false, "Control Panel", "Microsoft.Windows.ControlPanel", "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}");
            Expect(false, "Command Prompt", "x", @"C:\WINDOWS\system32\cmd.exe");
            Expect(false, "Google Chrome", "Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
            Expect(false, "File Explorer", "Microsoft.Windows.Explorer", @"C:\WINDOWS\explorer.exe");
            Expect(false, "Aviassembly", "steam://rungameid/2660460", "steam://rungameid/2660460");
            Expect(false, "Calculator", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null);
            Expect(false, "Odd|id", "a|b<c", "a|b<c");

            // Colour profiles: Standard must leave colours alone; Grayscale must turn pure red into a grey.
            var std = ColorProfiles.Matrix(ColorProfiles.Presets[0]);
            for (int i = 0; i < 25; i++) if (Math.Abs(std[i] - (i % 6 == 0 ? 1 : 0)) > 1e-6) throw new Exception("Standard colour profile isn't the identity at " + i);
            var gray = ColorProfiles.Matrix(new ColorProfiles.Profile { Saturation = 0, Contrast = 1 });
            if (Math.Abs(gray[0] - gray[1]) > 1e-6 || Math.Abs(gray[1] - gray[2]) > 1e-6) throw new Exception("Grayscale should give red the same value in every channel");
            var oled = ColorProfiles.Matrix(ColorProfiles.Presets[2]);
            if (oled[20] >= 0) throw new Exception("OLED should push blacks deeper (negative offset)");
            LayoutChecks();
            RenameChecks();
            if (args.Contains("--ui") || args.Contains("--preview")) DesktopChecks.Run(args.Contains("--preview"));
            Console.WriteLine("all checks passed");
            return 0;
        }
    }
}
