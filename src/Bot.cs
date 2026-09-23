using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace AutoShaper
{
    sealed class BotStatus
    {
        public bool Attached, InRun;
        public int Health = -1, Score, Cap;
        public string Difficulty = "", Note = "", Job = "", FarmNote = "";
    }

    // Plays WAVESHAPER from what is on screen.
    //
    // Game rules this relies on:
    //  * A shape is destroyed the first frame it touches the player if the held key or the last pressed
    //    key matches its type, so one tap ahead of time is enough.
    //  * Touching the player while holding a different key costs 1 health, and so does a shape that
    //    passes untouched. Each failed shape costs exactly 1 health.
    //  * Contact starts when a shape's centre reaches x=103; the shape is clear of the player below x=57.
    //  * High score, streak and full heals are only saved when the player dies.
    sealed partial class Bot : IDisposable
    {
        const double HandledX = 96;         // centre at or below this: contact has already happened
        const double PassedX = 48;          // centre below this: the shape is clear of the player
        const double MinNewTargetX = 150;   // a new target closer than this is never a real shape
        const long ConfirmMs = 650;         // a passed shape with no damage by then was scored
        const long DamageQuietMs = 250;     // shapes wiped by damage are ignored for this long

        readonly GameWindow window = new GameWindow();
        readonly KeyPresser keys = new KeyPresser();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly object statusGate = new object();
        BotStatus status = new BotStatus();
        Thread thread;
        volatile bool quit, enabled, enabledChanged, snapshotRequested;
        volatile Settings source;     // the menu's live settings
        volatile Settings settings;   // the copy the current run uses
        int[] vk = GameKeys.Load();

        bool inRun, runSeenFromStart, sawNoRun, startOnDeathScreen, restartKeyWasDown;
        long runStartMs, deathMs, restartSentMs, lastInRunMs, quietUntil;
        int hits, fullHeals, runsDone, attemptsDone;
        int health = -1, pendingHealth = -1, pendingHealthFrames;
        bool lowHealth;
        Shape lastSent;
        Difficulty detected = Difficulty.Auto;
        readonly List<double> speedSamples = new List<double>();
        double speed = 250;
        enum Phase { Waiting, Playing, Dead, Restarting, Done }
        Phase phase = Phase.Waiting;

        bool hasTarget, tHit, tReinforced, tFailed;
        Shape tType;
        double tCenter, tFirstCenter;
        long tSeenMs, tFirstMs;
        Shape handledType; double handledCenter; long handledAt;

        struct Passed { public long At; public bool Failed, Hit; }
        readonly List<Passed> passed = new List<Passed>();

        public Bot(Settings s) { source = s; settings = s.Clone(); }

        public void Start()
        {
            Native.timeBeginPeriod(1);
            thread = new Thread(Loop) { IsBackground = true, Name = "bot", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        public void UpdateSettings(Settings s) { source = s; if (!enabled) settings = s.Clone(); }

        public void RequestSnapshot() { snapshotRequested = true; }

        public BotStatus Status { get { lock (statusGate) return status; } }

        // Applied on the bot thread, which owns all run state. Ignored while the farm is running.
        void SetEnabled(bool value)
        {
            if (enabled == value || farmRequested) return;
            if (value) settings = source.Clone();
            enabled = value;
            enabledChanged = true;
        }

        void Loop()
        {
            while (!quit)
            {
                try { Tick(); }
                catch (Exception ex) { Log.Write("tick error: " + ex); Thread.Sleep(200); }
                Thread.Sleep(1);
            }
            keys.ReleaseNow();
        }

        void Tick()
        {
            long now = clock.ElapsedMilliseconds;
            ApplyFarmRequest(now);
            if (enabledChanged)
            {
                enabledChanged = false;
                phase = enabled && inRun ? Phase.Playing : Phase.Waiting;
                if (enabled) { runsDone = 0; attemptsDone = 0; } else keys.ReleaseNow();
                Log.Write(enabled ? "autoplay started, mode " + (BotMode)settings.Mode : "autoplay stopped");
            }
            keys.Update();

            if (!window.Refresh())
            {
                FarmWindowUnavailable(now, !window.Attached);
                if (inRun) EndRun(now, "game window lost");
                sawNoRun = false;
                Publish(null, window.Attached ? "game window minimised" : "");
                Thread.Sleep(100);
                return;
            }

            Frame f = window.Capture();
            if (f == null) { Thread.Sleep(5); return; }
            if (snapshotRequested) { snapshotRequested = false; SaveSnapshot(f); }

            Process(f, now);
            FarmTick(f, now);
            Publish(f, null);
        }

        void Process(Frame f, long now)
        {
            if (f.InRun)
            {
                if (!inRun) BeginRun(f, now, sawNoRun || phase == Phase.Restarting);
                else if (phase == Phase.Restarting && now - restartSentMs > 350) BeginRun(f, now, true);
                else if (UserPressedRestart()) { Log.Write("manual restart"); BeginRun(f, now, true); }

                UpdateHealth(f.Health, now);
                Track(f, now);
                SettlePassed(now, false);
                if (enabled && phase == Phase.Playing) Act(now);
            }
            else if (f.Line == LineState.Dead)
            {
                if (inRun) OnDeath(now);
                // Started on a game-over screen: restart straight away.
                if (enabled && phase == Phase.Waiting) { phase = Phase.Dead; deathMs = now - 1500; startOnDeathScreen = true; }
                if (enabled && (phase == Phase.Dead || phase == Phase.Restarting)) MaybeRestart(now);
            }
            else if (inRun && now - lastInRunMs > 600)
            {
                EndRun(now, "left the run");
            }
            // A run that starts after a menu or game-over screen was seen is observed from its first frame.
            if (f.InRun) lastInRunMs = now; else sawNoRun = true;
        }

        bool UserPressedRestart()
        {
            bool down = (Native.GetAsyncKeyState(vk[5]) & 0x8000) != 0 && !keys.Holding;
            bool edge = down && !restartKeyWasDown;
            restartKeyWasDown = down;
            return edge && Native.GetForegroundWindow() == window.Handle;
        }

        void BeginRun(Frame f, long now, bool fromStart)
        {
            inRun = true;
            startOnDeathScreen = false;
            runSeenFromStart = fromStart;
            phase = enabled ? (ModeDone() ? Phase.Done : Phase.Playing) : Phase.Waiting;
            runStartMs = now;
            hits = 0; fullHeals = 0; lowHealth = false;
            hitFailuresAtStart = 0; anyHitLanded = false; gamepadSuspected = false;
            health = f.Health; pendingHealth = -1;
            lastSent = Shape.None;
            hasTarget = false; passed.Clear(); handledType = Shape.None;
            detected = Difficulty.Auto; speedSamples.Clear();
            speed = Rules.StartSpeed(EffectiveDifficulty());
            quietUntil = 0;
            vk = GameKeys.Load();
            Log.Write("run start" + (fromStart ? "" : " (joined mid-run)") + ", health " + health);
        }

        void EndRun(long now, string why)
        {
            SettlePassed(now, true);
            Log.Write("run ended: " + why + ", score ~" + Score());
            inRun = false;
            hasTarget = false;
            if (phase != Phase.Done) phase = Phase.Waiting;
            keys.ReleaseNow();
        }

        void OnDeath(long now)
        {
            // The line turns red on death, so the last health lost is never seen by UpdateHealth.
            for (int i = 0; i < Math.Max(1, health); i++) AttributeDamage(now);
            SettlePassed(now, true);
            inRun = false;
            hasTarget = false;
            keys.ReleaseNow();
            deathMs = now; deathWall = DateTime.UtcNow;
            Log.Write("died: score ~" + Score() + ", full heals " + fullHeals + ", " + Rules.Name(EffectiveDifficulty()));
            if (!enabled) { phase = Phase.Waiting; return; }
            runsDone++;
            phase = ModeDone() ? Phase.Done : Phase.Dead;
            if (phase == Phase.Done) { enabled = false; Log.Write("run finished"); }
        }

        // One run per start, except attempts (restarts until banked) and the perfect-play loop.
        bool ModeDone()
        {
            if ((BotMode)settings.Mode == BotMode.AttemptFarm) return attemptsDone >= settings.Attempts;
            if (LoopActive) return runsDone >= Rules.MaxLoopRuns;
            return runsDone >= 1;
        }

        void MaybeRestart(long now)
        {
            if (!startOnDeathScreen && !LoopActive) return;
            bool due = phase == Phase.Dead ? now - deathMs > 1500 : now - restartSentMs > 2500;   // retries a lost press
            if (!due) return;
            if (LoopActive && phase == Phase.Dead && !startOnDeathScreen)
            {
                bool wait;
                bool again = LoopShouldContinue(now, out wait);
                if (wait) return;
                if (!again) { phase = Phase.Done; enabled = false; Log.Write("loop finished after " + runsDone + " runs"); return; }
            }
            keys.Tap(window.Handle, vk[5]);
            restartSentMs = now;
            phase = Phase.Restarting;
            Log.Write("restart");
        }

        void UpdateHealth(int h, long now)
        {
            if (h == health) { pendingHealth = -1; return; }
            if (h != pendingHealth) { pendingHealth = h; pendingHealthFrames = 1; return; }
            if (++pendingHealthFrames < 2) return;   // one-frame colour noise
            int old = health;
            health = h;
            pendingHealth = -1;
            if (old < 0) return;
            if (h < old)
            {
                for (int i = 0; i < old - h; i++) AttributeDamage(now);
                Log.Write("damage: health " + old + " -> " + h);
                if (h == 1) lowHealth = true;
                hasTarget = false;   // the game wipes every shape on screen after damage
                quietUntil = now + DamageQuietMs;
            }
            else if (h == 3 && lowHealth)
            {
                lowHealth = false;
                fullHeals++;
                Log.Write("full heal " + fullHeals);
            }
        }

        // A wrong key costs health at contact (the current target); a miss costs it shortly after the
        // shape has passed. Shapes let through on purpose are blamed first.
        void AttributeDamage(long now)
        {
            if (hasTarget && !tFailed && PredictedCenter(now) < 125) { tFailed = true; return; }
            for (int round = 0; round < 2; round++)
                for (int i = 0; i < passed.Count; i++)
                    if (!passed[i].Failed && (round == 1 || !passed[i].Hit)) { var p = passed[i]; p.Failed = true; passed[i] = p; return; }
        }

        void SettlePassed(long now, bool all)
        {
            for (int i = passed.Count - 1; i >= 0; i--)
            {
                if (!all && now - passed[i].At < ConfirmMs) continue;
                if (!passed[i].Failed) hits++;
                if (passed[i].Hit) NoteHitResult(passed[i].Failed);
                passed.RemoveAt(i);
            }
        }

        int Score() { return Rules.StartScore(EffectiveDifficulty()) + hits; }

        // Counts every unresolved passed shape as scored, so the cap can never be overshot.
        int ProjectedScore() { return Score() + PassedUnresolved(); }

        int PassedUnresolved() { int n = 0; foreach (var p in passed) if (!p.Failed) n++; return n; }

        double PredictedCenter(long now) { return tCenter - speed * (now - tSeenMs) / 1000.0; }

        void Track(Frame f, long now)
        {
            if (hasTarget)
            {
                double maxMove = 1500 * Math.Max(1, now - tSeenMs) / 1000.0 + 12;
                bool found = false, crossed = false;
                foreach (var s in f.Shapes)
                {
                    if (s.Type != tType || s.Center > tCenter + 6 || s.Center < tCenter - maxMove) continue;
                    if (s.Center <= ReleaseX()) { crossed = true; break; }
                    found = true;
                    tCenter = s.Center; tSeenMs = now;
                    if (tFirstCenter - s.Center >= 60) SampleSpeed((tFirstCenter - s.Center) / ((now - tFirstMs) / 1000.0));
                    break;
                }
                if (!found)
                {
                    // Destroyed shapes vanish at contact, so a lost target is handled once predicted past it.
                    if (crossed || PredictedCenter(now) <= ReleaseX()) HandleTarget(now);
                    else if (tCenter > 140 && now - tSeenMs > 300) hasTarget = false;   // lost far from the player
                }
            }
            if (!hasTarget && now >= quietUntil)
            {
                foreach (var s in f.Shapes)
                {
                    // Real shapes enter from the right and are still far away when they become the next
                    // target. Anything new close to the player is debris or a shape already handled.
                    if (s.Center < MinNewTargetX || IsJustHandled(s, now)) continue;
                    NewTarget(s, now);
                    break;
                }
            }
        }

        // A hit shape is destroyed at first contact, so the next key can follow at once. A shape let
        // through keeps touching the player until it is past, and a key pressed meanwhile could still
        // destroy it or count as a wrong key.
        double ReleaseX() { return tHit ? HandledX : PassedX; }

        void SampleSpeed(double v)
        {
            if (v < 100 || v > 2000) return;
            speed = speed * 0.7 + v * 0.3;
            if (speedSamples.Count >= 5 || clock.ElapsedMilliseconds - runStartMs > 8000 || !runSeenFromStart) return;
            speedSamples.Add(v);
            if (speedSamples.Count < 3) return;
            var sorted = new List<double>(speedSamples);
            sorted.Sort();
            double median = sorted[sorted.Count / 2];
            var d = Rules.FromSpeed(median);
            if (d != detected) { detected = d; Log.Write("detected " + Rules.Name(d) + " (" + median.ToString("0") + " px/s)"); }
        }

        void NewTarget(DetectedShape s, long now)
        {
            hasTarget = true;
            tType = s.Type; tCenter = s.Center; tFirstCenter = s.Center;
            tSeenMs = now; tFirstMs = now;
            tReinforced = false; tFailed = false;
            tHit = DecideHit();
            Log.Write("target " + tType + " @" + tCenter.ToString("0") + (tHit ? " hit" : " let through") + ", score ~" + ProjectedScore() + ", health " + health);
        }

        void HandleTarget(long now)
        {
            passed.Add(new Passed { At = now, Failed = tFailed, Hit = tHit });
            hasTarget = false;
            handledType = tType; handledCenter = PredictedCenter(now); handledAt = now;
        }

        // A shape that briefly drops out of detection can be handled a frame early; when it reappears
        // it must not become a new target, or it would be counted twice.
        bool IsJustHandled(DetectedShape s, long now)
        {
            if (handledType == Shape.None || now - handledAt > 350 || s.Type != handledType) return false;
            double expected = handledCenter - speed * (now - handledAt) / 1000.0;
            return Math.Abs(s.Center - expected) < 30;
        }

        // Until the starting speed has identified the mode, assume Hard: its 260-point budget is the
        // smallest, so no mode can pass its cap while detection is pending.
        Difficulty EffectiveDifficulty() { return detected == Difficulty.Auto ? Difficulty.Hard : detected; }

        int StopScore()
        {
            int cap = Rules.Cap(EffectiveDifficulty());
            if ((BotMode)settings.Mode != BotMode.PerfectPlay) return cap;
            return settings.Uncapped ? settings.StopScore : Math.Min(cap, settings.StopScore);
        }

        // Decided once per shape, when it becomes the next one to reach the player.
        bool DecideHit()
        {
            if (!runSeenFromStart) return false;   // score unknown: never risk passing the cap
            bool underCap = ProjectedScore() + 1 <= StopScore();
            switch ((BotMode)settings.Mode)
            {
                case BotMode.ComebackFarm:
                    if (!underCap || fullHeals >= settings.FullHeals) return false;
                    // Drop to 1 health, then heal back to full; never let a shape through at 1 health.
                    return lowHealth || health - IntendedMissesInFlight() <= 1;
                default:
                    return underCap;
            }
        }

        // Shapes let through on purpose whose damage has not shown yet.
        int IntendedMissesInFlight() { int n = 0; foreach (var p in passed) if (!p.Hit && !p.Failed) n++; return n; }

        void Act(long now)
        {
            if ((BotMode)settings.Mode == BotMode.AttemptFarm && hits + PassedUnresolved() >= 1
                && (passed.Count == 0 || now - passed[passed.Count - 1].At > 300) && now - runStartMs > 400)
            {
                attemptsDone++;
                Log.Write("attempt " + attemptsDone + " banked");
                keys.Tap(window.Handle, vk[5]);
                restartSentMs = now;
                phase = Phase.Restarting;
                if (attemptsDone >= settings.Attempts) { enabled = false; phase = Phase.Done; Log.Write("attempts done"); }
                return;
            }
            if (!runSeenFromStart || !hasTarget || keys.Holding) return;

            if (tHit)
            {
                if (lastSent != tType) Press(tType);
                else if (!tReinforced && PredictedCenter(now) < 200) { tReinforced = true; Press(tType); }   // in case a tap was dropped
            }
            else if (lastSent == tType)
            {
                Press(WrongKeyFor(tType));
            }
        }

        Shape WrongKeyFor(Shape t)
        {
            for (int i = 1; i <= 4; i++) if ((Shape)i != t && (Shape)i != lastSent) return (Shape)i;
            return t == Shape.TriangleUp ? Shape.TriangleDown : Shape.TriangleUp;
        }

        void Press(Shape s)
        {
            keys.Tap(window.Handle, vk[(int)s]);
            lastSent = s;
        }

        void SaveSnapshot(Frame f)
        {
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                Directory.CreateDirectory(Log.Folder);
                window.SaveLastCapture(Path.Combine(Log.Folder, "snapshot-" + stamp + ".bmp"));
                var sb = new StringBuilder();
                sb.AppendLine("client " + window.ClientW + "x" + window.ClientH + " scale " + window.Scale.ToString("0.00"));
                sb.AppendLine("line " + f.Line + " coverage " + f.LineCoverage + " rgb " + f.R.ToString("0") + "," + f.G.ToString("0") + "," + f.B.ToString("0"));
                foreach (var s in f.Shapes) sb.AppendLine("shape " + s);
                File.WriteAllText(Path.Combine(Log.Folder, "snapshot-" + stamp + ".txt"), sb.ToString());
                Log.Write("snapshot saved: " + stamp);
            }
            catch (Exception ex) { Log.Write("snapshot failed: " + ex.Message); }
        }

        void Publish(Frame f, string note)
        {
            var s = new BotStatus();
            s.Attached = window.Attached && f != null;
            s.InRun = inRun;
            s.Health = inRun ? health : (f != null && f.Line == LineState.Dead ? 0 : -1);
            s.Score = Score();
            s.Cap = StopScore();
            s.Difficulty = detected == Difficulty.Auto ? "detecting" : Rules.Name(detected).ToLowerInvariant();

            s.Note = note ?? (gamepadSuspected ? "keyboard input ignored: select menus with enter, not a controller"
                : inRun && !runSeenFromStart ? "joined mid-run: restart the run to play it" : "");
            s.Job = farm != FarmState.Off && job != null ? job.Label : "";
            s.FarmNote = farmNote;
            lock (statusGate) status = s;
        }

        public void Dispose()
        {
            quit = true;
            if (thread != null) { thread.Join(500); Native.timeEndPeriod(1); }
            window.Dispose();
        }
    }
}
