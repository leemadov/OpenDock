using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OpenDock
{
    static class Updater
    {
        public const string CurrentTag = "v0.3.0-preview.1";
        public const string ReleasesPage = "https://github.com/leemadov/OpenDock/releases";
        const int MaximumBuild = 32 * 1024 * 1024;
        const string AssetPrefix = "https://github.com/leemadov/OpenDock/releases/download/";
        static readonly Regex versionPattern = new Regex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

        public class Asset
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
        public class Release
        {
            public string tag_name { get; set; }
            public string name { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public Asset[] assets { get; set; }
            public Asset Build { get { return (assets ?? new Asset[0]).FirstOrDefault(a => ValidAsset(tag_name, a)); } }
        }

        static Match Version(string tag)
        {
            if (string.IsNullOrEmpty(tag) || tag.Length > 128) throw new ArgumentException("Invalid release version.");
            var match = versionPattern.Match(tag);
            int number;
            if (!match.Success || Enumerable.Range(1, 3).Any(i => !int.TryParse(match.Groups[i].Value, out number)) ||
                (match.Groups[4].Success && match.Groups[4].Value.Split('.').Any(s => s.Length == 0))) throw new ArgumentException("Invalid release version.");
            return match;
        }

        public static int CompareVersions(string first, string second)
        {
            var a = Version(first); var b = Version(second);
            for (int i = 1; i <= 3; i++) { int order = int.Parse(a.Groups[i].Value).CompareTo(int.Parse(b.Groups[i].Value)); if (order != 0) return order; }
            string ap = a.Groups[4].Value, bp = b.Groups[4].Value;
            if (ap.Length == 0 || bp.Length == 0) return ap.Length == bp.Length ? 0 : ap.Length == 0 ? 1 : -1;
            var ax = ap.Split('.'); var bx = bp.Split('.');
            for (int i = 0; i < Math.Min(ax.Length, bx.Length); i++)
            {
                long an, bn; bool numericA = long.TryParse(ax[i], out an), numericB = long.TryParse(bx[i], out bn);
                int order = numericA && numericB ? an.CompareTo(bn) : numericA != numericB ? (numericA ? -1 : 1) : StringComparer.Ordinal.Compare(ax[i], bx[i]);
                if (order != 0) return order;
            }
            return ax.Length.CompareTo(bx.Length);
        }

        public static bool ValidAsset(string tag, Asset asset)
        {
            if (asset == null || asset.name != "OpenDock.exe" || asset.size < 512 || asset.size > MaximumBuild ||
                asset.digest == null || !Regex.IsMatch(asset.digest, @"^sha256:[0-9a-fA-F]{64}$")) return false;
            Uri uri;
            return Uri.TryCreate(asset.browser_download_url, UriKind.Absolute, out uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
                string.IsNullOrEmpty(uri.UserInfo) && uri.AbsoluteUri == AssetPrefix + Uri.EscapeDataString(tag ?? "") + "/OpenDock.exe";
        }

        public static Release SelectRelease(string json, bool includePreview, string current = CurrentTag)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024 };
            var releases = serializer.Deserialize<Release[]>(json) ?? new Release[0];
            Release best = null;
            foreach (var release in releases)
            {
                if (release == null || release.draft || (!includePreview && release.prerelease) || release.Build == null) continue;
                try
                {
                    if (!includePreview && Version(release.tag_name).Groups[4].Success) continue;
                    if (CompareVersions(release.tag_name, current) > 0 && (best == null || CompareVersions(release.tag_name, best.tag_name) > 0)) best = release;
                }
                catch (ArgumentException) { }
            }
            return best;
        }

        static async Task<byte[]> Read(Uri uri, int limit, CancellationToken cancel, IProgress<int> progress = null)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent = "OpenDock/" + CurrentTag; request.Accept = "application/vnd.github+json";
            request.AllowAutoRedirect = true;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using (timeout.Token.Register(request.Abort))
                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream())
                {
                    if (response.ContentLength > limit) throw new InvalidDataException("The download is larger than expected.");
                    var buffer = new byte[32768]; int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        if (output.Length + count > limit) throw new InvalidDataException("The download is larger than expected.");
                        output.Write(buffer, 0, count);
                        if (progress != null && response.ContentLength > 0) progress.Report((int)(output.Length * 100 / response.ContentLength));
                    }
                    return output.ToArray();
                }
            }
        }

        public static async Task<Release> Check(bool includePreview, CancellationToken cancel)
        {
            var bytes = await Read(new Uri("https://api.github.com/repos/leemadov/OpenDock/releases?per_page=30"), 2 * 1024 * 1024, cancel).ConfigureAwait(false);
            return SelectRelease(Encoding.UTF8.GetString(bytes), includePreview);
        }

        public static string Hash(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        public static void Verify(byte[] bytes, string digest)
        {
            if (bytes == null || bytes.Length < 512 || bytes.Length > MaximumBuild || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("The downloaded file is not a Windows executable.");
            int pe = BitConverter.ToInt32(bytes, 0x3c);
            if (pe < 0 || pe > bytes.Length - 4 || bytes[pe] != 'P' || bytes[pe + 1] != 'E' || bytes[pe + 2] != 0 || bytes[pe + 3] != 0) throw new InvalidDataException("The downloaded executable header is invalid.");
            if (!string.Equals("sha256:" + Hash(bytes), digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download checksum did not match GitHub. Nothing was installed.");
        }

        static string Location { get { return Assembly.GetExecutingAssembly().Location; } }
        public static string Previous(string target)
        {
            return Directory.GetFiles(Path.GetDirectoryName(target), Path.GetFileName(target) + ".backup-*").Concat(File.Exists(target+".previous")?new[]{target+".previous"}:new string[0])
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        public static bool CanRollback { get { try { return Previous(Location) != null; } catch { return false; } } }

        public static async Task Install(Release release, CancellationToken cancel, IProgress<int> progress)
        {
            var asset = release.Build;
            if (asset == null || CompareVersions(release.tag_name, CurrentTag) <= 0) throw new InvalidDataException("This release has no valid newer build.");
            var bytes = await Read(new Uri(asset.browser_download_url), MaximumBuild, cancel, progress).ConfigureAwait(false);
            if (bytes.LongLength != asset.size) throw new InvalidDataException("The download size did not match GitHub.");
            Verify(bytes, asset.digest);
            cancel.ThrowIfCancellationRequested();
            BeginHandoff(bytes, asset.digest, Location, Process.GetCurrentProcess().Id);
        }

        public static void Rollback()
        {
            string previous = Previous(Location);
            if (previous == null) throw new FileNotFoundException("No previous build is available.");
            if (new FileInfo(previous).Length > MaximumBuild) throw new InvalidDataException("The previous build is larger than expected.");
            var bytes = File.ReadAllBytes(previous); string digest = "sha256:" + Hash(bytes); Verify(bytes, digest);
            BeginHandoff(bytes, digest, Location, Process.GetCurrentProcess().Id);
        }

        static string Quote(string text) { return "'" + text.Replace("'", "''") + "'"; }

        public static string HelperScript(int process, string staged, string target, string digest, string result)
        {
            if (!Regex.IsMatch(digest ?? "", @"^sha256:[0-9a-fA-F]{64}$")) throw new ArgumentException("Invalid expected checksum.");
            // Every path is a literal. The helper only waits; it never force-kills another process.
            return @"$ErrorActionPreference = 'Stop'
$stage = " + Quote(Path.GetFullPath(staged)) + @"
$target = " + Quote(Path.GetFullPath(target)) + @"
$result = " + Quote(Path.GetFullPath(result)) + @"
$backup = $target + '.previous'
$replaced = $false
function Hash([string]$path) {
  $hash = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($path)
  try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
  finally { $stream.Dispose(); $hash.Dispose() }
}
function Restart {
  $info = New-Object Diagnostics.ProcessStartInfo
  $info.FileName = $target
  $info.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
  return [Diagnostics.Process]::Start($info)
}
try {
  if ([IO.Path]::GetDirectoryName($stage) -ne [IO.Path]::GetDirectoryName($target)) { throw 'Update paths must share a directory.' }
  $old = $null
  try { $old = [Diagnostics.Process]::GetProcessById(" + process + @") } catch [ArgumentException] { }
  if ($null -ne $old -and !$old.WaitForExit(30000)) { throw 'OpenDock did not exit in time.' }
  if ((Hash $stage) -ne " + Quote(digest.Substring(7)) + @") { throw 'Staged checksum mismatch.' }
  [IO.File]::Replace($stage, $target, $backup)
  $replaced = $true
  if ((Hash $target) -ne " + Quote(digest.Substring(7)) + @") { throw 'Installed checksum mismatch.' }
  $started = Restart
  if ($started.WaitForExit(2500)) { throw 'Updated build exited during startup.' }
  [IO.File]::WriteAllText($result, 'Update installed; previous build retained.')
} catch {
  $failure = $_.Exception.Message
  if ($replaced -and [IO.File]::Exists($backup)) { [IO.File]::Copy($backup, $target, $true) }
  if ($null -eq $old -or $old.HasExited) { try { $null = Restart } catch { } }
  [IO.File]::WriteAllText($result, 'Update failed: ' + $failure)
  exit 1
}
";
        }

        public static string Stage(byte[] bytes, string digest, string target)
        {
            Verify(bytes, digest);
            string directory = Path.GetDirectoryName(Path.GetFullPath(target));
            string staged = Path.Combine(directory, ".OpenDock-update-" + Guid.NewGuid().ToString("N") + ".exe");
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write)) { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            return staged;
        }

        public static Process BeginHandoff(byte[] bytes, string digest, string target, int process)
        {
            string staged = Stage(bytes, digest, target);
            string updates = Path.Combine(Settings.DataDir, "Updates"); Directory.CreateDirectory(updates);
            string result = Path.Combine(updates, "last-result.txt");
            string script = HelperScript(process, staged, target, digest, result);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            return Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }

        public static string LastResult
        {
            get { try { string path = Path.Combine(Settings.DataDir, "Updates", "last-result.txt"); return File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return ""; } }
        }
    }
}
