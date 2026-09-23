using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace AutoShaper
{
    // Offline checks: renders synthetic game frames the way the game draws them and runs the detector.
    static class Tests
    {
        static int failures;

        public static int Run()
        {
            foreach (int scale in new[] { 1, 2, 3 })
            {
                ShapesAreClassified(scale);
                HealthColours(scale);
            }
            MenuTextIsNotARun();
            PartialAndShrinkingShapesIgnored();
            DebrisOverTheLineIsNotAShape();
            RulesAreConservative();
            AutoplayAndFarmNeverOverlap();
            PlannerOrdersAndSizesJobs();
            Console.WriteLine(failures == 0 ? "all tests passed" : failures + " test(s) failed");
            return failures == 0 ? 0 : 1;
        }

        // --analyze <snapshot.bmp> <scale>: runs the detector on a saved snapshot (the band starts at BandTop).
        public static unsafe int AnalyzeSnapshot(string path, float scale)
        {
            using (var bmp = new Bitmap(path))
            using (var copy = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb))
            {
                var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int y0 = (int)Math.Floor(Vision.BandTop * scale);
                    var f = Vision.Analyze((byte*)data.Scan0, data.Stride, copy.Width, copy.Height, y0, scale, 0, 0);
                    Console.WriteLine(Path.GetFileName(path) + ": " + f.Line + ", shapes: " + (f.Shapes.Count == 0 ? "none" : string.Join(", ", f.Shapes)));
                }
                finally { copy.UnlockBits(data); }
            }
            return 0;
        }

        static void Check(bool ok, string what)
        {
            if (!ok) { failures++; Console.WriteLine("FAIL: " + what); }
        }

        // Draws the wave line with bumps. Each shape: (type, left x, height).
        static Frame Render(int scale, Color line, params object[] shapes)
        {
            int w = 480 * scale, h = 270 * scale;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(line, scale))
                {
                    g.Clear(Color.Black);
                    int cursor = 90;
                    for (int i = 0; i < shapes.Length; i += 3)
                    {
                        var t = (Shape)shapes[i]; int x = (int)shapes[i + 1]; int ht = (int)shapes[i + 2];
                        Line(g, pen, scale, cursor, 136, x, 136);
                        int dir = Vision.IsUp(t) ? -1 : 1;
                        if (t == Shape.TriangleUp || t == Shape.TriangleDown)
                        {
                            Line(g, pen, scale, x, 136, x + 16, 136 + dir * ht);
                            Line(g, pen, scale, x + 16, 136 + dir * ht, x + 32, 136);
                        }
                        else
                        {
                            Line(g, pen, scale, x, 136, x, 136 + dir * ht);
                            Line(g, pen, scale, x, 136 + dir * ht, x + 32, 136 + dir * ht);
                            Line(g, pen, scale, x + 32, 136 + dir * ht, x + 32, 136);
                        }
                        cursor = x + 32;
                    }
                    Line(g, pen, scale, cursor, 136, 480, 136);
                }
                return Analyze(bmp, scale);
            }
        }

        static void Line(Graphics g, Pen pen, int s, int x0, int y0, int x1, int y1)
        {
            g.DrawLine(pen, x0 * s + s / 2f, y0 * s + s / 2f, x1 * s + s / 2f, y1 * s + s / 2f);
        }

        static unsafe Frame Analyze(Bitmap bmp, int scale)
        {
            int y0 = Vision.BandTop * scale, y1 = Vision.BandBottom * scale;
            var data = bmp.LockBits(new Rectangle(0, y0, bmp.Width, y1 - y0), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { return Vision.Analyze((byte*)data.Scan0, data.Stride, bmp.Width, y1 - y0, y0, scale, 0, 0); }
            finally { bmp.UnlockBits(data); }
        }

        static void ShapesAreClassified(int scale)
        {
            var f = Render(scale, Color.Lime,
                Shape.TriangleUp, 120, 32, Shape.SquareDown, 200, 32, Shape.SquareUp, 290, 32, Shape.TriangleDown, 380, 32);
            string ctx = " at scale " + scale;
            Check(f.Shapes.Count == 4, "four shapes detected" + ctx + " (got " + f.Shapes.Count + ": " + string.Join(", ", f.Shapes) + ")");
            if (f.Shapes.Count != 4) return;
            var expect = new[] { Shape.TriangleUp, Shape.SquareDown, Shape.SquareUp, Shape.TriangleDown };
            var lefts = new[] { 120, 200, 290, 380 };
            for (int i = 0; i < 4; i++)
            {
                Check(f.Shapes[i].Type == expect[i], "shape " + i + " is " + expect[i] + ctx + " (got " + f.Shapes[i].Type + ")");
                Check(Math.Abs(f.Shapes[i].Center - (lefts[i] + 16)) <= 3, "shape " + i + " centre" + ctx + " (got " + f.Shapes[i].Center + ")");
            }
            Check(f.Line == LineState.Health3, "green line is full health" + ctx);
        }

        static void HealthColours(int scale)
        {
            Check(Render(scale, Color.FromArgb(255, 255, 0), Shape.TriangleUp, 200, 32).Line == LineState.Health2, "yellow is 2 health at scale " + scale);
            Check(Render(scale, Color.FromArgb(255, 160, 64), Shape.TriangleUp, 200, 32).Line == LineState.Health1, "orange is 1 health at scale " + scale);
            Check(Render(scale, Color.FromArgb(255, 0, 0), Shape.TriangleUp, 200, 32).Line == LineState.Dead, "red is dead at scale " + scale);
            // Line flicker lowers alpha: a dimmed line keeps its hue.
            Check(Render(scale, Color.FromArgb(150, 150, 0), Shape.TriangleUp, 200, 32).Line == LineState.Health2, "dim yellow is 2 health at scale " + scale);
        }

        static void MenuTextIsNotARun()
        {
            using (var bmp = new Bitmap(960, 540, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var font = new Font("Consolas", 28f, FontStyle.Bold))
                {
                    g.Clear(Color.Black);
                    g.DrawString("PLAY GAME", font, Brushes.White, 260, 240);
                    g.DrawString("LEADERBOARDS", font, Brushes.White, 260, 290);
                }
                var f = Analyze(bmp, 2);
                Check(f.Line == LineState.NoGame, "menu text is not a run (got " + f.Line + ")");
                Check(!f.InRun, "menu is not in a run");
            }
            using (var bmp = new Bitmap(960, 540, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.Black);
                Check(Analyze(bmp, 2).Line == LineState.NoGame, "black screen is not a run");
            }
        }

        // Explosion particles can cluster into a shape-sized blob above the line; the line stays intact under them.
        static void DebrisOverTheLineIsNotAShape()
        {
            foreach (int scale in new[] { 1, 2 })
                using (var bmp = new Bitmap(480 * scale, 270 * scale, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    using (var pen = new Pen(Color.Lime, scale))
                    using (var chunk = new SolidBrush(Color.Lime))
                    {
                        g.Clear(Color.Black);
                        Line(g, pen, scale, 90, 136, 480, 136);                       // unbroken wave line
                        var rnd = new Random(3);
                        for (int i = 0; i < 14; i++)                                  // chunks in a 30x30 cluster above it
                            g.FillRectangle(chunk, (110 + rnd.Next(26)) * scale, (106 + rnd.Next(26)) * scale, 4 * scale, 4 * scale);
                        g.FillRectangle(chunk, 112 * scale, 105 * scale, 26 * scale, 3 * scale); // make it tall and wide enough to pass the size checks
                        g.FillRectangle(chunk, 124 * scale, 105 * scale, 3 * scale, 30 * scale);
                    }
                    var f = Analyze(bmp, scale);
                    Check(f.Shapes.Count == 0, "debris over an intact line is ignored at scale " + scale + " (got " + string.Join(", ", f.Shapes) + ")");
                    Check(f.Line == LineState.Health3, "debris doesn't hide the line colour at scale " + scale);
                }
        }

        static void PartialAndShrinkingShapesIgnored()
        {
            var f = Render(2, Color.Lime, Shape.SquareUp, 200, 14, Shape.TriangleUp, 462, 32);
            Check(f.Shapes.Count == 0, "shrinking and edge shapes are not targets (got " + string.Join(", ", f.Shapes) + ")");
        }

        static void AutoplayAndFarmNeverOverlap()
        {
            using (var bot = new Bot(new Settings()))
            {
                Check(bot.Activity == Activity.Idle, "starts idle");
                Check(bot.StartAutoplay() && bot.Activity == Activity.Autoplay, "autoplay starts");
                Check(!bot.StartFarm() && bot.Activity == Activity.Autoplay, "farm refused while autoplay runs");
                bot.StopAll();
                Check(bot.Activity == Activity.Idle, "stop all stops autoplay");
                Check(bot.StartFarm() && bot.Activity == Activity.Farm, "farm starts");
                Check(!bot.StartAutoplay() && bot.Activity == Activity.Farm, "autoplay refused while farming");
                bot.StopAll();
                Check(bot.Activity == Activity.Idle, "stop all stops the farm");
            }
        }

        static SteamStats.Achievement Ach(string name, string stat, long threshold, bool unlocked)
        {
            return new SteamStats.Achievement { Name = name, Title = name, Stat = stat, Threshold = threshold, Unlocked = unlocked };
        }

        static void PlannerOrdersAndSizesJobs()
        {
            var s = new SteamStats();
            s.Achievements.Add(Ach("ACHIEVEMENT_RTFM", null, 0, true));
            s.Achievements.Add(Ach("ACHIEVEMENT_SCORE_0", null, 0, false));
            s.Achievements.Add(Ach("ACHIEVEMENT_SPRITE", null, 0, false));
            s.Achievements.Add(Ach("ACHIEVEMENT_FRIENDS_HIGHSCORE", null, 0, false));
            s.Achievements.Add(Ach("HS27", "high_score", 420, false));
            s.Achievements.Add(Ach("HM26", "hm_high_score", 260, false));
            s.Achievements.Add(Ach("CB6", "full_heals", 6, true));
            s.Achievements.Add(Ach("HMCB6", "hm_full_heals", 6, false));
            s.Achievements.Add(Ach("TOTAL", "combined_total_score", 5000, false));
            s.Achievements.Add(Ach("ATT", "combined_total_attempts", 30, false));
            s.Stats["combined_total_score"] = 1826;
            s.Stats["combined_total_attempts"] = 13;

            var plan = Planner.Plan(s, null);
            var order = new[] { JobKind.Sprites, JobKind.PerfectNormal, JobKind.PerfectHard, JobKind.ComebackHard, JobKind.ZeroScore, JobKind.Totals, JobKind.Attempts };
            Check(plan.Count == order.Length, "plan has " + order.Length + " jobs (got " + plan.Count + ")");
            for (int i = 0; i < Math.Min(plan.Count, order.Length); i++) Check(plan[i].Kind == order[i], "job " + i + " is " + order[i] + " (got " + plan[i].Kind + ")");
            var hard = plan.Find(j => j.Kind == JobKind.PerfectHard);
            Check(hard != null && hard.Difficulty == Difficulty.Hard && hard.StopScore == 260, "hard job plays to 260");
            var zero = plan.Find(j => j.Kind == JobKind.ZeroScore);
            Check(zero != null && zero.StopScore == 0 && zero.Difficulty == Difficulty.Normal, "zero-score job is a normal run stopping at 0");
            var attempts = plan.Find(j => j.Kind == JobKind.Attempts);
            Check(attempts != null && attempts.Attempts == 17, "attempts job banks only the 17 missing (got " + (attempts == null ? 0 : attempts.Attempts) + ")");
            Check(Planner.Manual(s).Count == 1 && Planner.Manual(s)[0].Name == "ACHIEVEMENT_FRIENDS_HIGHSCORE", "manual list holds only what the farm can't do");
            Check(Planner.TotalsOpen(s), "loop continues while total score is locked");
            s.Find("TOTAL").Unlocked = true;
            Check(!Planner.TotalsOpen(s), "loop stops once totals are unlocked");
            Check(Planner.Plan(s, new[] { JobKind.PerfectNormal }).TrueForAll(j => j.Kind != JobKind.PerfectNormal), "skipped jobs are left out");
        }

        static void RulesAreConservative()
        {
            Check(Rules.Cap(Difficulty.Normal) == 420 && Rules.Cap(Difficulty.NormalShort) == 420 && Rules.Cap(Difficulty.Hard) == 260, "caps are 420/420/260");
            Check(Rules.FromSpeed(250) == Difficulty.Normal && Rules.FromSpeed(350) == Difficulty.Hard && Rules.FromSpeed(410) == Difficulty.NormalShort, "speed classification");
            // Before detection the bot assumes Hard; its points budget must not exceed any real mode's.
            int pending = Rules.Cap(Difficulty.Hard) - Rules.StartScore(Difficulty.Hard);
            foreach (var d in new[] { Difficulty.Normal, Difficulty.NormalShort, Difficulty.Hard })
                Check(pending <= Rules.Cap(d) - Rules.StartScore(d), "pending-detection budget fits " + d);
        }
    }
}
