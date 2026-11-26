using System;
using System.Linq;
using System.Security.Cryptography;

namespace OpenDock
{
    // The lock screen's passcode, for PCs without Windows Hello (or for anyone who'd rather tap one in, as on an
    // iPhone). Only a salted, slow hash of it is kept (PBKDF2-SHA256), never the code itself, so the settings file
    // doesn't give it away. It guards GlassDock's lock screen, which is a privacy screen over your desktop, not
    // Windows' own secure lock: Ctrl+Alt+Del still reaches Windows, and too many wrong tries hand over to Windows' lock.
    static class Passcode
    {
        const int Iterations = 120000;
        public const int MinLength = 4, MaxLength = 6;

        // "v1:<iterations>:<length>:<salt hex>:<hash hex>"
        static string[] Parts { get { var p = (Settings.LockPasscode ?? "").Split(':'); return p.Length == 5 && p[0] == "v1" ? p : null; } }

        public static bool IsSet { get { return Parts != null; } }
        public static int Length { get { var p = Parts; int n; return p != null && int.TryParse(p[2], out n) ? n : 0; } }

        public static bool Valid(string code)
        {
            return code != null && code.Length >= MinLength && code.Length <= MaxLength && code.All(c => c >= '0' && c <= '9');
        }

        public static void Set(string code)
        {
            if (!Valid(code)) throw new ArgumentException("A passcode is " + MinLength + " to " + MaxLength + " digits.");
            var salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            Settings.LockPasscode = "v1:" + Iterations + ":" + code.Length + ":" + Hex(salt) + ":" + Hex(Hash(code, salt, Iterations));
            Settings.Save();
        }

        public static void Clear() { Settings.LockPasscode = ""; Settings.Save(); }

        public static bool Check(string code)
        {
            var p = Parts;
            int iterations;
            if (p == null || code == null || !int.TryParse(p[1], out iterations)) return false;
            byte[] salt = Bytes(p[3]), want = Bytes(p[4]);
            if (salt == null || want == null) return false;
            var got = Hash(code, salt, iterations);
            int diff = got.Length ^ want.Length;   // every byte compared, so timing says nothing about how close it was
            for (int i = 0; i < Math.Min(got.Length, want.Length); i++) diff |= got[i] ^ want[i];
            return diff == 0;
        }

        static byte[] Hash(string code, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(code, salt, iterations, HashAlgorithmName.SHA256)) return kdf.GetBytes(32);
        }

        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }

        static byte[] Bytes(string hex)
        {
            if (hex == null || hex.Length % 2 != 0) return null;
            try { return Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray(); }
            catch { return null; }
        }
    }
}
