using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace AutoShaper
{
    // Read-only view of Steam's local stats cache for WAVESHAPER (appcache\stats): the achievement
    // schema plus the signed-in account's stat values and unlock bits. Nothing is written and Steam
    // is never called; if the files are missing or unreadable, Load returns null.
    sealed class SteamStats
    {
        public const int AppId = 562260;

        public sealed class Achievement
        {
            public string Name, Title, Stat;   // Stat is the progress stat, or null for event achievements
            public long Threshold;
            public bool Unlocked;
        }

        public readonly List<Achievement> Achievements = new List<Achievement>();
        public readonly Dictionary<string, long> Stats = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public DateTime Written;   // when Steam last wrote the account's stats file

        public int UnlockedCount { get { int n = 0; foreach (var a in Achievements) if (a.Unlocked) n++; return n; } }
        public long Stat(string name) { long v; return Stats.TryGetValue(name, out v) ? v : 0; }
        public Achievement Find(string name) { foreach (var a in Achievements) if (a.Name == name) return a; return null; }

        public static string UserFile()
        {
            string dir = StatsFolder();
            if (dir == null) return null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object v = k == null ? null : k.GetValue("ActiveUser");
                    if (v is int && (int)v != 0)
                    {
                        string f = Path.Combine(dir, "UserGameStats_" + (uint)(int)v + "_" + AppId + ".bin");
                        if (File.Exists(f)) return f;
                    }
                }
            }
            catch { }
            // Steam not running: fall back to the most recently written account file.
            string best = null; DateTime when = DateTime.MinValue;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "UserGameStats_*_" + AppId + ".bin"))
                    if (File.GetLastWriteTimeUtc(f) > when) { when = File.GetLastWriteTimeUtc(f); best = f; }
            }
            catch { }
            return best;
        }

        public static string SteamRoot()
        {
            foreach (var r in Roots()) if (Directory.Exists(Path.Combine(r, "steamapps"))) return r;
            return null;
        }

        static string StatsFolder()
        {
            foreach (var r in Roots())
            {
                string d = Path.Combine(r, "appcache", "stats");
                if (File.Exists(Path.Combine(d, "UserGameStatsSchema_" + AppId + ".bin"))) return d;
            }
            return null;
        }

        static List<string> Roots()
        {
            var roots = new List<string>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = k == null ? null : k.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p)) roots.Add(p.Replace('/', '\\'));
                }
            }
            catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            return roots;
        }

        public static SteamStats Load()
        {
            try
            {
                string dir = StatsFolder(), user = UserFile();
                if (dir == null || user == null) return null;
                var schemaRoot = Kv.Read(Path.Combine(dir, "UserGameStatsSchema_" + AppId + ".bin"));
                var schema = schemaRoot.Children.Count > 0 ? schemaRoot.Children[0].Get("stats") : null;
                var cache = Kv.Read(user).Get("cache");
                if (schema == null || cache == null) return null;

                var s = new SteamStats { Written = File.GetLastWriteTimeUtc(user) };
                foreach (var stat in schema.Children)
                {
                    var mine = cache.Get(stat.Key);
                    long value = 0;
                    if (mine != null && mine.Str("data") != null) long.TryParse(mine.Str("data"), out value);
                    var bits = stat.Get("bits");
                    if (bits == null) { if (stat.Str("name") != null) s.Stats[stat.Str("name")] = value; continue; }
                    foreach (var bit in bits.Children)
                    {
                        int index; if (!int.TryParse(bit.Key, out index)) continue;
                        var a = new Achievement { Name = bit.Str("name"), Unlocked = ((value >> index) & 1) != 0 };
                        var display = bit.Get("display"); var title = display == null ? null : display.Get("name");
                        a.Title = title == null ? a.Name : (title.Str("english") ?? a.Name);
                        var prog = bit.Get("progress");
                        if (prog != null && prog.Get("value") != null)
                        {
                            a.Stat = prog.Get("value").Str("operand1");
                            long.TryParse(prog.Str("max_val"), out a.Threshold);
                        }
                        s.Achievements.Add(a);
                    }
                }
                return s.Achievements.Count > 0 ? s : null;
            }
            catch (Exception ex) { Log.Write("steam stats unreadable: " + ex.Message); return null; }
        }

        // Minimal binary KeyValues reader (Steam's appcache format).
        sealed class Kv
        {
            public string Key, Value; public readonly List<Kv> Children = new List<Kv>();
            public Kv Get(string k) { foreach (var c in Children) if (c.Key == k) return c; return null; }
            public string Str(string k) { var c = Get(k); return c == null ? null : c.Value; }

            public static Kv Read(string path)
            {
                byte[] b; using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { b = new byte[fs.Length]; fs.Read(b, 0, b.Length); }
                int p = 0; var root = new Kv { Key = "" }; Fill(root, b, ref p); return root;
            }
            static string S(byte[] b, ref int p) { int s = p; while (p < b.Length && b[p] != 0) p++; return Encoding.UTF8.GetString(b, s, p++ - s); }
            static void Fill(Kv node, byte[] b, ref int p)
            {
                while (p < b.Length)
                {
                    byte t = b[p++];
                    if (t == 8 || t == 11) return;
                    var c = new Kv { Key = S(b, ref p) };
                    switch (t)
                    {
                        case 0: Fill(c, b, ref p); break;
                        case 1: c.Value = S(b, ref p); break;
                        case 2: case 4: case 6: c.Value = BitConverter.ToInt32(b, p).ToString(); p += 4; break;
                        case 3: c.Value = BitConverter.ToSingle(b, p).ToString(System.Globalization.CultureInfo.InvariantCulture); p += 4; break;
                        case 7: case 10: c.Value = BitConverter.ToUInt64(b, p).ToString(); p += 8; break;
                        default: throw new InvalidDataException("unknown KeyValues type " + t);
                    }
                    node.Children.Add(c);
                }
            }
        }
    }
}
