using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media.Imaging;

namespace OpenDock
{
    static class CustomIcons
    {
        internal static readonly Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string DirectoryPath { get { return Path.Combine(Settings.DataDir, "Icons"); } }

        public static IEnumerable<string> SaveLines()
        {
            return Map.Select(pair => "icon=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key)) + "|" + pair.Value);
        }

        public static bool Read(string line)
        {
            try
            {
                int split = line.IndexOf('|');
                if (split < 1) return false;
                string name = line.Substring(split + 1);
                if (Path.GetFileName(name) != name || !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
                string key = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(0, split)));
                if (string.IsNullOrEmpty(key)) return false;
                Map[key] = name; return true;
            }
            catch (ArgumentException) { return false; }
            catch (FormatException) { return false; }
        }

        public static string Get(string key)
        {
            string name;
            if (key == null || !Map.TryGetValue(key, out name)) return null;
            string path = Path.Combine(DirectoryPath, name);
            return File.Exists(path) ? path : null;
        }

        // Decode to bounded dimensions, preserve transparency/aspect ratio, then own a normalized PNG copy.
        public static void Normalize(string source, string destination)
        {
            if (new FileInfo(source).Length > 20 * 1024 * 1024) throw new ArgumentException("Choose an image smaller than 20 MB.");
            BitmapImage image;
            using (var input = File.OpenRead(source))
            {
                var frame = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None).Frames[0];
                int width = frame.PixelWidth, height = frame.PixelHeight;
                if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 64000000) throw new ArgumentException("Choose an image no larger than 64 megapixels.");
                double scale = Math.Min(1, 512.0 / Math.Max(width, height));
                input.Position = 0;
                image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = input; image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * scale)); image.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * scale));
                image.EndInit(); image.Freeze();
            }
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) png.Save(output);
        }

        public static void Set(string key, string source)
        {
            Directory.CreateDirectory(DirectoryPath);
            string name = Guid.NewGuid().ToString("N") + ".png";
            Normalize(source, Path.Combine(DirectoryPath, name));
            string previous; bool hadPrevious = Map.TryGetValue(key, out previous);
            int revision = Settings.IconRevision;
            try { Map[key] = name; Settings.IconRevision++; Settings.Apply(); }
            catch { if (hadPrevious) Map[key] = previous; else Map.Remove(key); Settings.IconRevision = revision; throw; }
        }

        public static void Reset(string key)
        {
            string previous;
            if (!Map.TryGetValue(key, out previous)) return;
            int revision = Settings.IconRevision;
            try { Map.Remove(key); Settings.IconRevision++; Settings.Apply(); }
            catch { Map[key] = previous; Settings.IconRevision = revision; throw; }
        }
    }
}
