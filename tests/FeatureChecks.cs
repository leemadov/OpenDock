using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    static class FeatureChecks
    {
        static Updater.Release Release(string tag, bool preview = false)
        {
            return new Updater.Release { tag_name = tag, prerelease = preview, assets = new[] { new Updater.Asset { name = "OpenDock.exe", size = 1024,
                browser_download_url = "https://github.com/leemadov/OpenDock/releases/download/" + tag + "/OpenDock.exe", digest = "sha256:" + new string('a', 64) } } };
        }

        public static void Run()
        {
            var version = (AssemblyInformationalVersionAttribute)Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).Single();
            if (version.InformationalVersion != Updater.CurrentTag) throw new Exception("binary and updater versions differ");
            if (Updater.CompareVersions("v0.4.0-preview.10", "v0.4.0-preview.2") <= 0 || Updater.CompareVersions("v0.4.0", "v0.4.0-preview.10") <= 0 || Updater.CompareVersions("v0.1.9", "v0.4.0-preview.1") >= 0 || Updater.CompareVersions("v0.4.0+build", "0.4.0") != 0) throw new Exception("update version ordering");
            var stable = Release("v0.4.0"); var preview = Release("v0.5.0-preview.1", true); var draft = Release("v9.0.0"); draft.draft = true;
            var hostile = Release("v8.0.0"); hostile.assets[0].browser_download_url = "https://example.com/OpenDock.exe";
            string json = new JavaScriptSerializer().Serialize(new[] { preview, stable, draft, hostile, Release("v0.1.0") });
            if (Updater.SelectRelease(json, false).tag_name != stable.tag_name || Updater.SelectRelease(json, true).tag_name != preview.tag_name || Updater.SelectRelease(json, false, "v1.0.0") != null) throw new Exception("update channel, draft or downgrade filtering");
            var badHash = Release("v4.0.0"); badHash.assets[0].digest = null;
            if (Updater.SelectRelease(new JavaScriptSerializer().Serialize(new[] { badHash }), true) != null) throw new Exception("unsigned digest accepted");
            var bytes = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location); string digest = "sha256:" + Updater.Hash(bytes);
            Updater.Verify(bytes, digest);
            bool rejected = false;
            try { Updater.Verify(bytes, "sha256:" + new string('0', 64)); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("bad update checksum accepted");
            rejected = false;
            try { Updater.Verify(new byte[1024], "sha256:" + Updater.Hash(new byte[1024])); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new Exception("non-executable update accepted");

            string directory = Path.Combine(Path.GetTempPath(), "OpenDock-features-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var originalIcons = CustomIcons.Map.ToList();
            try
            {
                string source = Path.Combine(directory, "source.png"), output = Path.Combine(directory, "normalized.png");
                var image = new DrawingVisual(); using (var dc = image.RenderOpen()) dc.DrawRectangle(Brushes.Red, null, new System.Windows.Rect(0, 0, 1024, 512));
                var bitmap = new RenderTargetBitmap(1024, 512, 96, 96, PixelFormats.Pbgra32); bitmap.Render(image);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(source)) encoder.Save(file);
                string before = Updater.Hash(File.ReadAllBytes(source));
                CustomIcons.Normalize(source, output);
                using (var file = File.OpenRead(output))
                {
                    var normalized = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                    if (normalized.PixelWidth != 512 || normalized.PixelHeight != 256) throw new Exception("icon aspect ratio or resolution changed");
                }
                if (before != Updater.Hash(File.ReadAllBytes(source))) throw new Exception("custom icon modified its original image");
                rejected = false; try { CustomIcons.Normalize(source, output); } catch (IOException) { rejected = true; }
                if (!rejected) throw new Exception("custom icon overwrote an existing file");
                string key = "app:name|with=delimiters";
                CustomIcons.Map.Clear(); CustomIcons.Map[key] = "owned.png";
                string mapping = CustomIcons.SaveLines().Single().Substring(5); CustomIcons.Map.Clear();
                if (!CustomIcons.Read(mapping) || CustomIcons.Map[key] != "owned.png" || CustomIcons.Read(Convert.ToBase64String(Encoding.UTF8.GetBytes("bad")) + "|..\\escape.png") || CustomIcons.Read("broken|file.png")) throw new Exception("custom icon mapping validation");
                string target = Path.Combine(directory, "OpenDock.exe"); File.WriteAllText(target, "original");
                string stage = Updater.Stage(bytes, digest, target);
                if (Updater.Hash(File.ReadAllBytes(stage)) != digest.Substring(7) || File.ReadAllText(target) != "original") throw new Exception("update staging changed installed file");
            }
            finally
            {
                CustomIcons.Map.Clear(); foreach (var pair in originalIcons) CustomIcons.Map[pair.Key] = pair.Value;
                Directory.Delete(directory, true);
            }
        }

        public static void Handoff()
        {
            string directory = Path.Combine(Path.GetTempPath(), "OpenDock helper ' [fixture] " + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string target = Path.Combine(directory, "OpenDock ' fixture.exe"), candidate = Path.Combine(directory, "candidate.exe");
                CompileFixture(target, "old"); CompileFixture(candidate, "new");
                var previous = File.ReadAllBytes(target); var next = File.ReadAllBytes(candidate);
                string result = Path.Combine(directory, "result.txt");
                // A wrong staged checksum must leave the installed executable byte-for-byte intact.
                string staged = Updater.Stage(next, "sha256:" + Updater.Hash(next), target);
                RunHelper(Updater.HelperScript(int.MaxValue, staged, target, "sha256:" + new string('0', 64), result), directory, false);
                WaitForFixture(directory);
                if (Updater.Hash(File.ReadAllBytes(target)) != Updater.Hash(previous)) throw new Exception("failed update changed installed executable");
                File.Delete(Path.Combine(directory, "started.txt"));
                staged = Updater.Stage(next, "sha256:" + Updater.Hash(next), target);
                RunHelper(Updater.HelperScript(int.MaxValue, staged, target, "sha256:" + Updater.Hash(next), result), directory, true);
                WaitForFixture(directory);
                if (Updater.Hash(File.ReadAllBytes(target)) != Updater.Hash(next) || Updater.Hash(File.ReadAllBytes(target + ".previous")) != Updater.Hash(previous)) throw new Exception("update or previous-build backup differed");
                File.Delete(Path.Combine(directory, "started.txt"));
                staged = Updater.Stage(previous, "sha256:" + Updater.Hash(previous), target);
                RunHelper(Updater.HelperScript(int.MaxValue, staged, target, "sha256:" + Updater.Hash(previous), result), directory, true);
                WaitForFixture(directory);
                if (Updater.Hash(File.ReadAllBytes(target)) != Updater.Hash(previous) || Updater.Hash(File.ReadAllBytes(target + ".previous")) != Updater.Hash(next)) throw new Exception("rollback didn't preserve both builds");
                Console.WriteLine("updater: rejected checksum, atomic replacement, restart and rollback verified on isolated executables");
            }
            finally { Directory.Delete(directory, true); }
        }

        static void CompileFixture(string destination, string marker)
        {
            using (var compiler = new Microsoft.CSharp.CSharpCodeProvider())
            {
                var options = new CompilerParameters(new[] { "System.dll" }, destination, false) { GenerateExecutable = true };
                string source = "using System; using System.IO; using System.Diagnostics; using System.Threading; class Fixture { static void Main() { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"started.txt\"), Process.GetCurrentProcess().Id.ToString()); Thread.Sleep(3500); } }";
                var result = compiler.CompileAssemblyFromSource(options, source);
                if (result.Errors.HasErrors) throw new Exception("Couldn't compile isolated update fixture: " + result.Errors[0]);
            }
        }

        static void RunHelper(string script, string directory, bool success)
        {
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            using (var process = Process.Start(new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                if (!process.WaitForExit(15000) || (process.ExitCode == 0) != success) throw new Exception("update helper failed: " + (File.Exists(Path.Combine(directory, "result.txt")) ? File.ReadAllText(Path.Combine(directory, "result.txt")) : "no result"));
            }
        }

        static void WaitForFixture(string directory)
        {
            string marker = Path.Combine(directory, "started.txt"); int attempts = 0;
            while (!File.Exists(marker) && attempts++ < 50) Thread.Sleep(100);
            if (!File.Exists(marker)) throw new Exception("updated fixture didn't restart: " + File.ReadAllText(Path.Combine(directory, "result.txt")));
            try { using (var process = Process.GetProcessById(int.Parse(File.ReadAllText(marker)))) if (!process.WaitForExit(10000)) throw new Exception("fixture did not exit"); }
            catch (ArgumentException) { } // It already exited.
        }
    }
}
