using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace AutoShaper
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // Per-Monitor-V2 first (live DPI changes, physical game coordinates); older Windows falls back.
            bool perMonitor = false;
            try { perMonitor = Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            if (!perMonitor) try { Native.SetProcessDPIAware(); } catch { }
            float dpiScale = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f; } catch { }
            Size screen; try { screen = Screen.PrimaryScreen.Bounds.Size; } catch { screen = new Size(1920, 1080); }
            Skin.InitializeScale(ComputeScale(dpiScale, screen));

            if (args.Length > 0 && args[0] == LeaderboardRestore.Argument) return LeaderboardRestore.Run();
            if (args.Length > 0 && args[0] == "--self-test") return Tests.Run();
            if (args.Length > 2 && args[0] == "--analyze") return Tests.AnalyzeSnapshot(args[1], float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
            if (args.Length > 1 && args[0] == "--render")
            {
                if (args.Length > 2) Skin.InitializeScale(float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));
                using (var form = new MainForm(new Settings(), null)) form.Render(args[1]);
                return 0;
            }

            bool created;
            using (var single = new Mutex(true, @"Local\autoshaper-single-instance", out created))
            {
                if (!created)
                {
                    try { using (var signal = EventWaitHandle.OpenExisting(@"Local\autoshaper-show-menu")) signal.Set(); } catch { }
                    return 0;
                }
                using (var reopen = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\autoshaper-show-menu"))
                {
                    SpriteDrop.CleanUpStale();   // a sprite left behind if the app was closed mid-job
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(Settings.Load(), reopen));
                }
            }
            return 0;
        }

        // 1920x1080 is 1x. Windows scaling and screen resolution are independent (a 4K screen at 100% reports
        // 96 DPI), so the larger of the two scales wins rather than their product. Clamped to [1, 3].
        internal static float ComputeScale(float dpiScale, Size screenPixels)
        {
            float resolutionScale = Math.Min(screenPixels.Width / 1920f, screenPixels.Height / 1080f);
            return Math.Max(1f, Math.Min(3f, Math.Max(dpiScale, resolutionScale)));
        }
    }
}
