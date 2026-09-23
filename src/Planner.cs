using System;
using System.Collections.Generic;

namespace AutoShaper
{
    enum JobKind { HowToPlay, Sprites, PerfectNormal, PerfectHard, ComebackNormal, ComebackHard, ZeroScore, Totals, Attempts }

    sealed class Job
    {
        public JobKind Kind;
        public Difficulty Difficulty = Difficulty.Normal;
        public BotMode Mode = BotMode.PerfectPlay;
        public int StopScore, FullHeals = Rules.MaxFullHeals, Attempts = 1;
        public int Unlocks;          // locked achievements this job works towards
        public string Label = "";

        public Settings ToSettings(Settings baseSettings)
        {
            var s = baseSettings.Clone();
            s.Mode = (int)Mode; s.StopScore = StopScore; s.FullHeals = FullHeals; s.Attempts = Attempts;
            s.Loop = false;
            s.Uncapped = false;   // the farm always keeps the caps
            return s;
        }
    }

    // Turns the account's stats into an ordered job list. Every job is sized to what is still locked:
    // score caps stay at the top milestones and totals/attempts stop where their last achievement unlocks.
    static class Planner
    {
        static readonly string[] Shapes = { "shape_1_count", "shape_2_count", "shape_3_count", "shape_4_count" };
        const string HowToPlay = "ACHIEVEMENT_RTFM", ZeroScore = "ACHIEVEMENT_SCORE_0", Sprites = "ACHIEVEMENT_SPRITE";

        public static List<Job> Plan(SteamStats s, ICollection<JobKind> skip)
        {
            var jobs = new List<Job>();
            Add(jobs, skip, JobKind.HowToPlay, Locked(s, HowToPlay), j => { j.Label = "how to play (rtfm)"; });
            Add(jobs, skip, JobKind.Sprites, Locked(s, Sprites), j => { j.Label = "custom sprite (byos)"; });
            Add(jobs, skip, JobKind.PerfectNormal, Count(s, "high_score", "high_streak"), j => { j.StopScore = Rules.NormalCap; j.Label = "normal · play to " + Rules.NormalCap; });
            Add(jobs, skip, JobKind.PerfectHard, Count(s, "hm_high_score", "hm_high_streak"), j => { j.Difficulty = Difficulty.Hard; j.StopScore = Rules.HardCap; j.Label = "hard · play to " + Rules.HardCap; });
            Add(jobs, skip, JobKind.ComebackNormal, Count(s, "full_heals"), j => { j.Mode = BotMode.ComebackFarm; j.StopScore = Rules.NormalCap; j.Label = "normal · comeback ×" + Rules.MaxFullHeals; });
            Add(jobs, skip, JobKind.ComebackHard, Count(s, "hm_full_heals"), j => { j.Mode = BotMode.ComebackFarm; j.Difficulty = Difficulty.Hard; j.StopScore = Rules.HardCap; j.Label = "hard · comeback ×" + Rules.MaxFullHeals; });
            Add(jobs, skip, JobKind.ZeroScore, Locked(s, ZeroScore), j => { j.StopScore = 0; j.Label = "normal · zero-score run"; });

            int totals = Count(s, "combined_total_score") + Count(s, Shapes);
            Add(jobs, skip, JobKind.Totals, totals, j =>
            {
                j.StopScore = Rules.NormalCap;
                long need = Need(s, "combined_total_score");
                j.Label = need > 0 ? "normal · totals " + Num(s.Stat("combined_total_score")) + "/" + Num(need) : "normal · shape totals";
            });

            long attemptsNeed = Need(s, "combined_total_attempts");
            Add(jobs, skip, JobKind.Attempts, Count(s, "combined_total_attempts"), j =>
            {
                j.Mode = BotMode.AttemptFarm;
                j.Attempts = Rules.Clamp((int)(attemptsNeed - s.Stat("combined_total_attempts")), 1, Rules.MaxAttempts);
                j.Label = "attempts " + s.Stat("combined_total_attempts") + "/" + attemptsNeed;
            });
            return jobs;
        }

        // Locked achievements nothing here can earn (no stat to play towards, and not a menu visit).
        public static List<SteamStats.Achievement> Manual(SteamStats s)
        {
            var list = new List<SteamStats.Achievement>();
            foreach (var a in s.Achievements)
                if (!a.Unlocked && a.Stat == null && a.Name != HowToPlay && a.Name != ZeroScore && a.Name != Sprites) list.Add(a);
            return list;
        }

        static void Add(List<Job> jobs, ICollection<JobKind> skip, JobKind kind, int unlocks, Action<Job> setup)
        {
            if (unlocks <= 0 || (skip != null && skip.Contains(kind))) return;
            var j = new Job { Kind = kind, Unlocks = unlocks };
            setup(j);
            jobs.Add(j);
        }

        static string Num(long v) { return v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture); }

        public static bool SpriteUnlocked(SteamStats s) { var a = s.Find(Sprites); return a == null || a.Unlocked; }

        static int Locked(SteamStats s, string name) { var a = s.Find(name); return a != null && !a.Unlocked ? 1 : 0; }

        static int Count(SteamStats s, params string[] stats)
        {
            int n = 0;
            foreach (var a in s.Achievements)
                if (!a.Unlocked && a.Stat != null && Array.IndexOf(stats, a.Stat) >= 0) n++;
            return n;
        }

        // Highest threshold among the stat's still-locked achievements (0 when all are unlocked).
        static long Need(SteamStats s, string stat)
        {
            long need = 0;
            foreach (var a in s.Achievements)
                if (!a.Unlocked && a.Stat == stat) need = Math.Max(need, a.Threshold);
            return need;
        }

        // True while perfect-play looping still earns something: total score or shape counts.
        public static bool TotalsOpen(SteamStats s) { return Count(s, "combined_total_score") + Count(s, Shapes) > 0; }
    }
}
