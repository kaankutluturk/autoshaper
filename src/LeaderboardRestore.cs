using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoShaper
{
    // Danger zone "restore leaderboards": runs in its own short-lived process (--restore-leaderboards)
    // so the menu never talks to Steam. It connects to the running Steam client as WAVESHAPER through
    // steamclient.dll, lowers the high score and streak stats above the achievement caps back to them,
    // and rewrites the matching leaderboard entries to the capped values. Achievements and every other
    // stat are left alone.
    static class LeaderboardRestore
    {
        public const string Argument = "--restore-leaderboards";

        struct Board { public string Stat, Leaderboard; public int Cap; }
        static readonly Board[] Boards =
        {
            new Board { Stat = "high_score", Leaderboard = "lb_highscores_v2", Cap = Rules.NormalCap },
            new Board { Stat = "high_streak", Leaderboard = "lb_streaks_v2", Cap = Rules.NormalCap },
            new Board { Stat = "hm_high_score", Leaderboard = "lb_hm_highscores_v2", Cap = Rules.HardCap },
            new Board { Stat = "hm_high_streak", Leaderboard = "lb_hm_streaks_v2", Cap = Rules.HardCap },
        };

        public const int Ok = 0, GameRunning = 2, SteamUnavailable = 3, Failed = 5;

        // Runs the helper process; returns its exit code and last output line.
        public static int RunHelper(out string summary)
        {
            summary = "";
            var psi = new ProcessStartInfo(System.Windows.Forms.Application.ExecutablePath, Argument)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (var line in output.Split('\n')) { string t = line.Trim(); if (t.Length > 0) { Log.Write("restore: " + t); summary = t; } }
                return p.ExitCode;
            }
        }

        public static int Run()
        {
            if (Process.GetProcessesByName("WAVESHAPER").Length > 0) { Console.WriteLine("close the game first"); return GameRunning; }
            var cached = SteamStats.Load();
            string root = SteamStats.SteamRoot();
            if (root == null || cached == null) { Console.WriteLine("steam not found"); return SteamUnavailable; }

            using (var steam = SteamClient.Connect(root, SteamStats.AppId))
            {
                if (steam == null) { Console.WriteLine("steam is not running or refused the connection"); return SteamUnavailable; }

                steam.RequestCurrentStats();
                var values = new int[Boards.Length];
                if (!steam.Wait(() => steam.GetStat(Boards[0].Stat, out values[0]), 8000)) { Console.WriteLine("stats did not load"); return Failed; }

                // The interface must read what Steam's local cache holds before anything is written.
                if (values[0] != cached.Stat(Boards[0].Stat)) { Console.WriteLine("stats check failed, nothing changed"); return Failed; }

                int lowered = 0;
                for (int i = 0; i < Boards.Length; i++)
                {
                    if (!steam.GetStat(Boards[i].Stat, out values[i])) { Console.WriteLine("could not read " + Boards[i].Stat + ", nothing changed"); return Failed; }
                }
                for (int i = 0; i < Boards.Length; i++)
                {
                    if (values[i] <= Boards[i].Cap) continue;
                    Console.WriteLine(Boards[i].Stat + " " + values[i] + " -> " + Boards[i].Cap);
                    values[i] = Boards[i].Cap;
                    steam.SetStat(Boards[i].Stat, values[i]);
                    lowered++;
                }
                if (lowered > 0 && !steam.StoreStats()) { Console.WriteLine("stats could not be stored"); return Failed; }

                int rewritten = 0;
                for (int i = 0; i < Boards.Length; i++)
                {
                    ulong board;
                    if (!steam.FindLeaderboard(Boards[i].Leaderboard, out board)) { Console.WriteLine(Boards[i].Leaderboard + " not found"); continue; }
                    if (steam.UploadForced(board, values[i])) rewritten++;
                    else Console.WriteLine(Boards[i].Leaderboard + " could not be updated");
                }
                Console.WriteLine(lowered == 0 && rewritten == Boards.Length
                    ? "leaderboards already within the caps"
                    : "restored: " + lowered + " stats lowered, " + rewritten + "/" + Boards.Length + " leaderboards rewritten");
                return rewritten == Boards.Length ? Ok : Failed;
            }
        }

        // Minimal client for Steam's steamclient.dll (32-bit), identified to Steam as one app. Interface
        // methods are called through their vtables; the offsets match these interface versions.
        sealed class SteamClient : IDisposable
        {
            const string ClientVersion = "SteamClient017", UtilsVersion = "SteamUtils008", StatsVersion = "STEAMUSERSTATS_INTERFACE_VERSION011";
            const int LeaderboardFindResult = 1104, LeaderboardScoreUploaded = 1106, ForceUpdate = 2;

            IntPtr client, utils, stats;
            int pipe, user;

            public static SteamClient Connect(string steamRoot, int appId)
            {
                Environment.SetEnvironmentVariable("SteamAppId", appId.ToString());
                SetDllDirectory(steamRoot + ";" + Path.Combine(steamRoot, "bin"));
                IntPtr module = LoadLibraryEx(Path.Combine(steamRoot, "steamclient.dll"), IntPtr.Zero, 8 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
                if (module == IntPtr.Zero) return null;
                var create = Export<CreateInterfaceFn>(module, "CreateInterface");
                getCallback = Export<GetCallbackFn>(module, "Steam_BGetCallback");
                freeCallback = Export<FreeCallbackFn>(module, "Steam_FreeLastCallback");
                if (create == null || getCallback == null || freeCallback == null) return null;

                int rc;
                var s = new SteamClient { client = create(ClientVersion, out rc) };
                if (s.client == IntPtr.Zero) return null;
                s.pipe = s.Call<CreatePipeFn>(s.client, 0)(s.client);
                if (s.pipe == 0) return null;
                s.user = s.Call<ConnectUserFn>(s.client, 2)(s.client, s.pipe);
                if (s.user == 0) { s.Dispose(); return null; }
                s.utils = s.Call<GetUtilsFn>(s.client, 9)(s.client, s.pipe, UtilsVersion);
                s.stats = s.Call<GetUserStatsFn>(s.client, 13)(s.client, s.user, s.pipe, StatsVersion);
                // Steam must confirm it sees this process as the requested app.
                if (s.utils == IntPtr.Zero || s.stats == IntPtr.Zero || s.Call<GetAppIdFn>(s.utils, 9)(s.utils) != appId) { s.Dispose(); return null; }
                return s;
            }

            public bool RequestCurrentStats() { return Call<BoolFn>(stats, 0)(stats); }
            public bool GetStat(string name, out int value) { return Call<GetStatFn>(stats, 2)(stats, name, out value); }
            public bool SetStat(string name, int value) { return Call<SetStatFn>(stats, 4)(stats, name, value); }
            public bool StoreStats() { return Call<BoolFn>(stats, 10)(stats); }

            public bool FindLeaderboard(string name, out ulong board)
            {
                board = 0;
                var result = new byte[16];   // LeaderboardFindResult_t: uint64 handle, uint8 found
                if (!Await(Call<FindLeaderboardFn>(stats, 23)(stats, name), result, LeaderboardFindResult)) return false;
                board = BitConverter.ToUInt64(result, 0);
                return result[8] != 0 && board != 0;
            }

            public bool UploadForced(ulong board, int score)
            {
                var result = new byte[32];   // LeaderboardScoreUploaded_t: uint8 success, uint64 board, int32 score, ...
                return Await(Call<UploadScoreFn>(stats, 31)(stats, board, ForceUpdate, score, IntPtr.Zero, 0), result, LeaderboardScoreUploaded) && result[0] != 0;
            }

            bool Await(ulong call, byte[] result, int callbackId)
            {
                if (call == 0) return false;
                bool failed = false;
                var completed = Call<IsCallCompletedFn>(utils, 11);
                if (!Wait(() => completed(utils, call, out failed), 10000) || failed) return false;
                var handle = GCHandle.Alloc(result, GCHandleType.Pinned);
                try { return Call<GetCallResultFn>(utils, 13)(utils, call, handle.AddrOfPinnedObject(), result.Length, callbackId, out failed) && !failed; }
                finally { handle.Free(); }
            }

            public bool Wait(Func<bool> done, int timeoutMs)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMs)
                {
                    CallbackMsg msg;
                    while (getCallback(pipe, out msg)) freeCallback(pipe);   // keep the callback queue drained
                    if (done()) return true;
                    Thread.Sleep(50);
                }
                return false;
            }

            public void Dispose()
            {
                if (client == IntPtr.Zero) return;
                if (user != 0) Call<ReleaseUserFn>(client, 4)(client, pipe, user);
                if (pipe != 0) Call<ReleasePipeFn>(client, 1)(client, pipe);
                client = IntPtr.Zero;
            }

            T Call<T>(IntPtr instance, int index) where T : class
            {
                IntPtr vtable = Marshal.ReadIntPtr(instance);
                return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtable, index * IntPtr.Size), typeof(T));
            }

            static T Export<T>(IntPtr module, string name) where T : class
            {
                IntPtr p = GetProcAddress(module, name);
                return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
            }

            static GetCallbackFn getCallback;
            static FreeCallbackFn freeCallback;

            [StructLayout(LayoutKind.Sequential)] struct CallbackMsg { public int User, Callback; public IntPtr Param; public int Size; }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr CreateInterfaceFn([MarshalAs(UnmanagedType.LPStr)] string version, out int returnCode);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetCallbackFn(int pipe, out CallbackMsg msg);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool FreeCallbackFn(int pipe);

            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int CreatePipeFn(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool ReleasePipeFn(IntPtr self, int pipe);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate int ConnectUserFn(IntPtr self, int pipe);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void ReleaseUserFn(IntPtr self, int pipe, int user);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr GetUtilsFn(IntPtr self, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate IntPtr GetUserStatsFn(IntPtr self, int user, int pipe, [MarshalAs(UnmanagedType.LPStr)] string version);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate uint GetAppIdFn(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool IsCallCompletedFn(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetCallResultFn(IntPtr self, ulong call, IntPtr result, int size, int callbackExpected, [MarshalAs(UnmanagedType.I1)] out bool failed);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool BoolFn(IntPtr self);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool GetStatFn(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, out int value);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] [return: MarshalAs(UnmanagedType.I1)] delegate bool SetStatFn(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name, int value);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate ulong FindLeaderboardFn(IntPtr self, [MarshalAs(UnmanagedType.LPStr)] string name);
            [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate ulong UploadScoreFn(IntPtr self, ulong board, int method, int score, IntPtr details, int detailCount);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectory(string path);
            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
        }
    }
}
