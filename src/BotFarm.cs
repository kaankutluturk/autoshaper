using System;
using System.Collections.Generic;
using System.IO;

namespace AutoShaper
{
    enum Activity { Idle, Autoplay, Farm }

    // Achievement farm: plans from Steam's local stats, drives the game's menus with the keyboard and
    // hands each run to the autoplay engine with that job's settings. Autoplay and the farm share one
    // owner slot, so only one of them ever drives the game.
    sealed partial class Bot
    {
        enum FarmState { Off, Plan, ToMenu, Navigate, WaitRun, Playing, AfterRun, HowToPlay, SpriteCheck, WaitStats }

        // The game's menu keys are fixed: enter selects, left cycles the mode, down moves, escape goes back.
        const int VkEnter = 13, VkEscape = 27, VkLeft = 37, VkDown = 40;
        const int MaxStartTries = 3;

        volatile bool farmRequested, farmChanged;
        FarmState farm = FarmState.Off, afterMenu;
        Job job;
        long stateSince, lastKeyAt, pausedAt, spriteCheckedAt;
        int escapes, startTries, stuck, spriteAttempt;
        readonly Queue<int> menuKeys = new Queue<int>();   // negative entries are pauses in ms
        readonly HashSet<JobKind> skipped = new HashSet<JobKind>();
        DateTime statsWrittenAtJobStart, lastStatsWrite;
        long statsSettledSince;
        string lastJobSignature = "", farmNote = "";

        public Activity Activity { get { return farmRequested ? Activity.Farm : enabled ? Activity.Autoplay : Activity.Idle; } }

        public bool StartAutoplay()
        {
            if (farmRequested) return false;
            SetEnabled(true);
            return true;
        }

        public bool StartFarm()
        {
            if (enabled || farmRequested) return false;
            farmRequested = true; farmChanged = true;
            return true;
        }

        public void StopAll()
        {
            if (farmRequested) { farmRequested = false; farmChanged = true; }
            SetEnabled(false);
        }

        void ApplyFarmRequest(long now)
        {
            if (!farmChanged) return;
            farmChanged = false;
            if (farmRequested)
            {
                skipped.Clear(); stuck = 0; lastJobSignature = ""; farmNote = "";
                Enter(FarmState.Plan, now);
                Log.Write("farm started");
            }
            else if (farm != FarmState.Off) StopFarm("");
        }

        void StopFarm(string note)
        {
            SpriteDrop.CleanUp();
            farm = FarmState.Off;
            farmRequested = false;
            enabled = false;
            if (phase != Phase.Done) phase = Phase.Waiting;
            menuKeys.Clear();
            keys.ReleaseNow();
            farmNote = note;
            Log.Write("farm stopped" + (note.Length > 0 ? ": " + note : ""));
        }

        void Enter(FarmState s, long now) { farm = s; stateSince = now; }

        // A closed game stops the farm; a minimised one pauses its timers until it is restored.
        void FarmWindowUnavailable(long now, bool closed)
        {
            if (farm == FarmState.Off) return;
            if (closed) { StopFarm("game closed"); return; }
            if (pausedAt == 0) { pausedAt = now; keys.ReleaseNow(); }
        }

        void FarmTick(Frame f, long now)
        {
            if (farm == FarmState.Off) return;
            if (pausedAt != 0) { stateSince += now - pausedAt; lastKeyAt += now - pausedAt; pausedAt = 0; }
            switch (farm)
            {
                case FarmState.Plan: PlanNext(now); break;
                case FarmState.ToMenu: ToMenu(f, now); break;
                case FarmState.Navigate: Navigate(now); break;
                case FarmState.HowToPlay:
                    if (now - stateSince > 1500) ReturnToMenu(FarmState.WaitStats, now);
                    break;
                case FarmState.WaitRun:
                    if (inRun) Enter(FarmState.Playing, now);
                    else if (now - stateSince > 5000) RetryStart(now, "the run did not start");
                    break;
                case FarmState.Playing: Playing(f, now); break;
                case FarmState.SpriteCheck: SpriteCheck(now); break;
                case FarmState.AfterRun:
                    // Leave the game a moment to store stats and upload scores.
                    if (now - stateSince > 1500) ReturnToMenu(FarmState.WaitStats, now);
                    break;
                case FarmState.WaitStats:
                {
                    // Steam may write the stats file more than once after a run; plan only once it has
                    // been rewritten and then left alone for a moment.
                    var file = SteamStats.UserFile();
                    DateTime written = file != null ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                    if (written != lastStatsWrite) { lastStatsWrite = written; statsSettledSince = now; }
                    bool settled = written > statsWrittenAtJobStart && now - statsSettledSince > 2000;
                    if (settled || now - stateSince > 15000) Enter(FarmState.Plan, now);
                    break;
                }
            }
        }

        void ReturnToMenu(FarmState then, long now)
        {
            afterMenu = then; escapes = 0;
            Enter(FarmState.ToMenu, now);
        }

        void PlanNext(long now)
        {
            var stats = SteamStats.Load();
            if (stats == null) { StopFarm("steam stats not found"); return; }
            var plan = Planner.Plan(stats, skipped);
            if (plan.Count == 0) { StopFarm(skipped.Count == 0 ? "all achievements done" : "done, some jobs skipped"); return; }
            job = plan[0];
            // The same job again without new unlocks means the last attempt achieved nothing. Totals and
            // attempts progress without unlocking every run, so their own stat counts as progress too.
            string signature = job.Kind + ":" + stats.UnlockedCount
                + (job.Kind == JobKind.Totals ? ":" + stats.Stat("combined_total_score") : "")
                + (job.Kind == JobKind.Attempts ? ":" + stats.Stat("combined_total_attempts") : "");
            stuck = signature == lastJobSignature ? stuck + 1 : 0;
            lastJobSignature = signature;
            if (stuck >= 2) { StopFarm("no progress on " + job.Label); return; }
            statsWrittenAtJobStart = stats.Written;
            startTries = 0;
            spriteAttempt = 0;
            Log.Write("farm job: " + job.Label + " (" + job.Unlocks + " achievements, " + plan.Count + " jobs left)");
            ReturnToMenu(FarmState.Navigate, now);
        }

        // Escape leaves a run or the game-over screen and backs out of submenus; three presses from any
        // menu reach the top level with the cursor on PLAY GAME.
        void ToMenu(Frame f, long now)
        {
            if (f.InRun || f.Line == LineState.Dead)
            {
                escapes = 0;
                if (now - lastKeyAt > 700) Key(VkEscape, now);
                return;
            }
            if (escapes < 3) { if (now - lastKeyAt > 300) { Key(VkEscape, now); escapes++; } return; }
            if (now - lastKeyAt < 600) return;
            if (afterMenu == FarmState.WaitStats) { Enter(FarmState.WaitStats, now); return; }

            menuKeys.Clear();
            if (job.Kind == JobKind.Sprites)
            {
                Enter(QueueSpriteToggle() ? FarmState.Navigate : FarmState.Plan, now);
                return;
            }
            menuKeys.Enqueue(VkEnter);                                                   // PLAY GAME
            if (job.Kind == JobKind.HowToPlay) { menuKeys.Enqueue(VkDown); menuKeys.Enqueue(VkEnter); }
            else
            {
                if (job.Difficulty == Difficulty.Hard) menuKeys.Enqueue(VkLeft);            // NORMAL wraps to HARD MODE
                menuKeys.Enqueue(VkEnter);
            }
            Enter(FarmState.Navigate, now);
        }

        void Navigate(long now)
        {
            if (menuKeys.Count > 0)
            {
                int next = menuKeys.Peek();
                if (next < 0) { if (now - lastKeyAt >= -next) { menuKeys.Dequeue(); lastKeyAt = now; } }
                else if (now - lastKeyAt > 350) Key(menuKeys.Dequeue(), now);
                return;
            }
            if (job.Kind == JobKind.HowToPlay) { Enter(FarmState.HowToPlay, now); return; }
            if (job.Kind == JobKind.Sprites) { spriteCheckedAt = 0; Enter(FarmState.SpriteCheck, now); return; }
            // Armed before the run's first frame so the run is observed from its start.
            settings = job.ToSettings(source);
            runsDone = 0; attemptsDone = 0;
            enabled = true; enabledChanged = false;
            phase = Phase.Waiting;
            Enter(FarmState.WaitRun, now);
        }

        void Playing(Frame f, long now)
        {
            if (detected != Difficulty.Auto && detected != job.Difficulty)
            {
                Log.Write("farm: " + Rules.Name(detected) + " started instead of " + Rules.Name(job.Difficulty));
                RetryStart(now, "wrong game mode");
                return;
            }
            if (!enabled && phase == Phase.Done) { Enter(FarmState.AfterRun, now); return; }
            if (!enabled && gamepadSuspected) { StopFarm("keyboard input ignored: select menus with enter, not a controller"); return; }
            if (!inRun && f.Line == LineState.NoGame && now - lastInRunMs > 3000) StopFarm("the run was left");
        }

        void RetryStart(long now, string why)
        {
            enabled = false;
            phase = Phase.Waiting;
            if (++startTries >= MaxStartTries)
            {
                skipped.Add(job.Kind);
                Log.Write("farm: skipping " + job.Label + ": " + why);
                farmNote = "skipped " + job.Label;
                Enter(FarmState.Plan, now);
                return;
            }
            Log.Write("farm: " + why + ", retrying");
            ReturnToMenu(FarmState.Navigate, now);
        }

        // BRING YOUR OWN SPRITES: place a sprite (the save folder first, then the game's own Sprites
        // folder), switch Custom Sprites on so the game loads it, then put the option back as it was.
        bool QueueSpriteToggle()
        {
            spriteAttempt++;
            string folder = spriteAttempt == 1 ? SpriteDrop.SaveAreaFolder : SpriteDrop.InstallFolder(window.ProcessId);
            if (spriteAttempt > 2 || folder == null || !SpriteDrop.Place(folder))
            {
                SpriteDrop.CleanUp();
                skipped.Add(JobKind.Sprites);
                farmNote = "custom sprite could not be loaded";
                Log.Write("farm: skipping custom sprite");
                return false;
            }
            bool wasOn = SpriteDrop.CustomSpritesOn();
            foreach (int k in new[] { VkDown, VkDown, VkEnter, VkDown, VkDown, VkDown, VkDown, VkEnter }) menuKeys.Enqueue(k);   // OPTIONS > CUSTOM FILES
            if (wasOn) { menuKeys.Enqueue(VkEnter); menuKeys.Enqueue(-500); }   // off first, so switching on reloads
            menuKeys.Enqueue(VkEnter);                                         // CUSTOM SPRITES [ON] loads the sprite
            menuKeys.Enqueue(-1500);
            if (!wasOn) menuKeys.Enqueue(VkEnter);
            menuKeys.Enqueue(VkEscape); menuKeys.Enqueue(VkEscape);
            return true;
        }

        void SpriteCheck(long now)
        {
            if (now - spriteCheckedAt < 1000) return;
            spriteCheckedAt = now;
            string where = spriteAttempt == 1 ? "save folder" : "game folder";
            var stats = SteamStats.Load();
            if (stats != null && Planner.SpriteUnlocked(stats))
            {
                SpriteDrop.CleanUp();
                Log.Write("farm: custom sprite unlocked (" + where + ")");
                Enter(FarmState.Plan, now);
                return;
            }
            if (now - stateSince < 8000) return;
            SpriteDrop.CleanUp();
            Log.Write("farm: custom sprite not picked up from the " + where);
            ReturnToMenu(FarmState.Navigate, now);
        }

        void Key(int vkCode, long now)
        {
            keys.Tap(window.Handle, vkCode);
            lastKeyAt = now;
        }

        // Perfect-play loop: before each restart, wait for Steam to record the run just played, then go
        // on only while total score or shape achievements are still locked. Without readable stats the
        // loop stops after Rules.MaxLoopRuns, which covers the 5,000 total from zero.
        bool LoopActive { get { return farm == FarmState.Off && (BotMode)settings.Mode == BotMode.PerfectPlay && settings.Loop; } }
        long loopCheckedAt;
        DateTime deathWall;

        bool LoopShouldContinue(long now, out bool wait)
        {
            wait = false;
            if (runsDone >= Rules.MaxLoopRuns) return false;
            if (now - loopCheckedAt < 500) { wait = true; return false; }
            loopCheckedAt = now;
            var stats = SteamStats.Load();
            if (stats == null) return true;
            if (stats.Written <= deathWall && now - deathMs < 8000) { wait = true; return false; }
            return Planner.TotalsOpen(stats);
        }

        // In controller mode the game reads gamepad bindings and ignores keyboard presses, so every
        // intended hit fails. Three failures before any success means the keys are not arriving.
        int hitFailuresAtStart;
        bool anyHitLanded, gamepadSuspected;

        void NoteHitResult(bool failed)
        {
            if (anyHitLanded) return;
            if (!failed) { anyHitLanded = true; return; }
            if (++hitFailuresAtStart >= 3 && !gamepadSuspected)
            {
                gamepadSuspected = true;
                enabled = false;
                keys.ReleaseNow();
                Log.Write("keyboard input ignored by the game, stopping");
            }
        }
    }
}
