using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AutoShaper
{
    // Temporary custom sprite for BRING YOUR OWN SPRITES. The game loads Sprites/spr_triangle_up_1.png
    // (working_directory-relative) when Custom Sprites is switched on and awards the achievement once
    // one loads. Existing sprites are never overwritten, and only files created here are removed
    // (verified by hash). A marker file lets the next launch clean up after a crash.
    static class SpriteDrop
    {
        const string FileName = "spr_triangle_up_1.png";
        static readonly List<string> created = new List<string>();
        static readonly List<string> createdDirs = new List<string>();

        static string Marker { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoShaper", "pending-sprite.txt"); } }

        public static string SaveAreaFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WAVESHAPER", "Sprites"); } }

        public static string InstallFolder(int processId)
        {
            try
            {
                string exe = ProcessPath(processId);
                return exe == null ? null : Path.Combine(Path.GetDirectoryName(exe), "Sprites");
            }
            catch { return null; }
        }

        // Places the sprite in `folder` unless a sprite with that name is already there.
        public static bool Place(string folder)
        {
            try
            {
                string path = Path.Combine(folder, FileName);
                if (File.Exists(path)) return true;       // an existing sprite will trigger the same load
                if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); createdDirs.Add(folder); }
                byte[] png = Png();
                File.WriteAllBytes(path, png);
                created.Add(path);
                File.AppendAllText(EnsureMarkerDir(), path + "|" + Hash(png) + Environment.NewLine);
                Log.Write("sprite placed: " + path);
                return true;
            }
            catch (Exception ex) { Log.Write("sprite placement failed in " + folder + ": " + ex.Message); return false; }
        }

        public static void CleanUp()
        {
            foreach (var path in created) DeleteIfOurs(path, Hash(Png()));
            created.Clear();
            foreach (var dir in createdDirs)
                try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
            createdDirs.Clear();
            try { if (File.Exists(Marker)) File.Delete(Marker); } catch { }
        }

        // Startup: remove a sprite left behind if the app was closed mid-job.
        public static void CleanUpStale()
        {
            try
            {
                if (!File.Exists(Marker)) return;
                foreach (var line in File.ReadAllLines(Marker))
                {
                    int bar = line.LastIndexOf('|');
                    if (bar > 0) DeleteIfOurs(line.Substring(0, bar), line.Substring(bar + 1));
                }
                File.Delete(Marker);
            }
            catch { }
        }

        static void DeleteIfOurs(string path, string hash)
        {
            try
            {
                if (File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash) { File.Delete(path); Log.Write("sprite removed: " + path); }
            }
            catch (Exception ex) { Log.Write("sprite removal failed: " + ex.Message); }
        }

        static string EnsureMarkerDir() { Directory.CreateDirectory(Path.GetDirectoryName(Marker)); return Marker; }

        static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", ""); }

        static byte[] cachedPng;

        // 32x32 up-triangle in the icon's style: grey plating, red neon edge, red eye. Generated once per
        // process so its hash is stable for cleanup.
        static byte[] Png()
        {
            if (cachedPng != null) return cachedPng;
            using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    var tri = new[] { new PointF(16, 1), new PointF(31, 31), new PointF(1, 31) };
                    using (var b = new SolidBrush(Color.FromArgb(200, 203, 208))) g.FillPolygon(b, tri);
                    using (var p = new Pen(Color.FromArgb(255, 30, 20), 2f)) g.DrawPolygon(p, tri);
                    using (var b = new SolidBrush(Color.FromArgb(235, 16, 16))) g.FillEllipse(b, 12.5f, 17.5f, 7, 7);
                    using (var b = new SolidBrush(Color.FromArgb(255, 220, 200))) g.FillEllipse(b, 14.5f, 19f, 2.5f, 2.5f);
                }
                using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); cachedPng = ms.ToArray(); }
            }
            return cachedPng;
        }

        // Custom Sprites as saved by the game in %APPDATA%\WAVESHAPER\config.ini ([CUSTOM] SPRITES).
        public static bool CustomSpritesOn()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WAVESHAPER", "config.ini");
                bool section = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { section = line.Equals("[CUSTOM]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (section && line.StartsWith("SPRITES=", StringComparison.OrdinalIgnoreCase)) return !line.Contains("\"0");
                }
            }
            catch { }
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static string ProcessPath(int pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
    }
}
