using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace AutoShaper
{
    enum BotMode { PerfectPlay = 0, ComebackFarm = 1, AttemptFarm = 2 }

    enum Difficulty { Auto = 0, Normal = 1, NormalShort = 2, Hard = 3 }

    public class Settings
    {
        // Autoplay is always perfect play; the farm sets mode, full heals and attempts for each job.
        [XmlIgnore] public int Mode = (int)BotMode.PerfectPlay;
        [XmlIgnore] public int FullHeals = Rules.MaxFullHeals;
        [XmlIgnore] public int Attempts = Rules.MaxAttempts;
        public int StopScore = Rules.NormalCap;   // 0 = zero-score run; Hard is further limited to 260 at run time
        public bool Loop;                         // repeat runs while total score/shape achievements are locked
        public bool Uncapped;                     // danger zone: lifts the score caps for autoplay (never for the farm)
        public int MenuKey = 45, MenuModifiers = 0;     // insert: show/hide the menu
        public int PauseKey = 36, PauseModifiers = 0;   // home: start/stop

        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutoShaper"); } }
        static string FilePath { get { return Path.Combine(Folder, "settings.xml"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var s = File.OpenRead(FilePath)) return Validate((Settings)new XmlSerializer(typeof(Settings)).Deserialize(s));
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                using (var s = File.Create(tmp)) new XmlSerializer(typeof(Settings)).Serialize(s, this);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch { }
        }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        static Settings Validate(Settings s)
        {
            s.StopScore = Rules.Clamp(s.StopScore, 0, s.Uncapped ? Rules.UncappedMax : Rules.NormalCap);
            if (s.MenuKey < 0 || s.MenuKey > 259) s.MenuKey = 45;
            if (s.PauseKey < 0 || s.PauseKey > 259) s.PauseKey = 36;
            s.MenuModifiers &= 15; s.PauseModifiers &= 15;
            return s;
        }
    }

    // Score ceilings: the highest score milestone per mode in the Steam schema.
    // Normal (Short) starts at 160 and saves into the same high_score stat as Normal.
    static class Rules
    {
        public const int NormalCap = 420;   // HIGH SCORE XXVII
        public const int HardCap = 260;     // HIGH SCORE XXVI (HARD MODE)

        // Farming limits stop at what the achievements need, so leaderboards aren't inflated.
        public const int MaxAttempts = 30;      // CONSOLATION PRIZE
        public const int MaxFullHeals = 6;      // COMEBACK VI
        public const int MaxLoopRuns = 12;      // 12 x 420 covers TOTAL SCORE XXIII (5,000) from zero
        public const int UncappedMax = 99999;   // stop-score limit once the danger zone lifts the caps

        public static int Clamp(int v, int min, int max) { return Math.Max(min, Math.Min(max, v)); }

        public static int Cap(Difficulty d) { return d == Difficulty.Hard ? HardCap : NormalCap; }
        public static int StartScore(Difficulty d) { return d == Difficulty.NormalShort ? 160 : 0; }
        public static int StartSpeed(Difficulty d) { return d == Difficulty.Hard ? 350 : d == Difficulty.NormalShort ? 410 : 250; }

        public static string Name(Difficulty d)
        {
            switch (d) { case Difficulty.Normal: return "Normal"; case Difficulty.NormalShort: return "Normal (Short)"; case Difficulty.Hard: return "Hard"; }
            return "Auto";
        }

        public static Difficulty FromSpeed(double pxPerSecond)
        {
            if (pxPerSecond <= 0) return Difficulty.Auto;
            if (pxPerSecond < 300) return Difficulty.Normal;
            if (pxPerSecond < 380) return Difficulty.Hard;
            return Difficulty.NormalShort;
        }
    }

    // Game key bindings from %APPDATA%\WAVESHAPER\config.ini (GameMaker key codes are Windows VK codes).
    static class GameKeys
    {
        public static int[] Load()
        {
            var keys = new[] { 0, 'Q', 'A', 'W', 'S', 'R' };
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WAVESHAPER", "config.ini");
                if (!File.Exists(path)) return keys;
                bool inSection = false;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inSection = line.Equals("[KEYBOARD_CONTROLS]", StringComparison.OrdinalIgnoreCase); continue; }
                    if (!inSection || !line.StartsWith("KEY", StringComparison.OrdinalIgnoreCase)) continue;
                    int eq = line.IndexOf('=');
                    int idx;
                    if (eq < 4 || !int.TryParse(line.Substring(3, eq - 3), out idx) || idx < 1 || idx > 5) continue;
                    double v;
                    if (double.TryParse(line.Substring(eq + 1).Trim('"', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0 && v < 256)
                        keys[idx] = (int)v;
                }
            }
            catch { }
            return keys;
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        static string path;

        const int KeepLogs = 7, KeepSnapshots = 10;

        public static string Folder { get { return Path.Combine(Settings.Folder, "logs"); } }

        static void Prune(string pattern, int keep)
        {
            try
            {
                var files = new DirectoryInfo(Folder).GetFiles(pattern);
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (int i = keep; i < files.Length; i++) files[i].Delete();
            }
            catch { }
        }

        public static void Write(string msg)
        {
            lock (gate)
            {
                try
                {
                    if (path == null)
                    {
                        Directory.CreateDirectory(Folder);
                        Prune("autoshaper-*.log", KeepLogs - 1);
                        Prune("snapshot-*.*", KeepSnapshots * 2);
                        path = Path.Combine(Folder, "autoshaper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    }
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
