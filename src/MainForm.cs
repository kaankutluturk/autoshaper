using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AutoShaper
{
    // Borderless, topmost 480x274 menu with tabs and a one-line status footer.
    sealed class MainForm : Form
    {
        const int PageCount = 3;
        const int TabsX = 196;
        const int WM_DPICHANGED = 0x02E0;

        readonly Settings settings;
        readonly Bot bot;
        readonly EventWaitHandle reopen;
        readonly bool launchWindow;
        readonly Panel[] pages = new Panel[PageCount];
        readonly MenuTabs tabs = new MenuTabs("autoplay", "achievements", "misc");
        readonly FooterBar footer = new FooterBar();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 150 };
        KeyMonitor keys;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;
        ToolStripMenuItem trayToggle;
        IntPtr previous;
        bool closing;
        string notice = "", lastFarmNote = "";
        DateTime noticeUntil, lastHotkeyAt;

        MicroButton startButton;
        // Settings controls are locked while anything runs; each run uses the settings it started with.
        readonly List<Control> locked = new List<Control>();
        Activity? lockedFor;
        // Danger zone: an action is armed first and only runs from the confirm button.
        enum Armed { None, Uncapped, Restore }
        Armed armed;
        bool restoring;
        Label dangerWarning;
        MicroButton dangerConfirm;

        Label achProgress, achNext, achQueue, achManual;
        MicroButton farmButton;
        SteamStats preview;
        DateTime previewAt;

        public MainForm(Settings s, EventWaitHandle showSignal)
        {
            settings = s;
            reopen = showSignal;
            launchWindow = showSignal != null;
            bot = new Bot(s);
            Text = "AutoShaper"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = launchWindow; TopMost = true; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(480, 274); BackColor = Skin.Background; ForeColor = Skin.Text; Font = Skin.Font; DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (launchWindow)
            {
                try { keys = new KeyMonitor(); keys.Pressed += OnBinding; keys.Configure(settings.MenuKey, settings.PauseKey); }
                catch (Exception e) { if (keys != null) { keys.Dispose(); keys = null; } Notice(e.Message); }
            }

            tabs.SetBounds(TabsX, 5, 465 - TabsX, 40); tabs.Change = SelectPage; Controls.Add(tabs);
            Controls.Add(footer);
            footer.MouseDown += (o, e) => DragMenu(e);
            var menu = new ContextMenuStrip();
            menu.Items.Add("open logs folder", null, (o, e) => { try { Directory.CreateDirectory(Log.Folder); Process.Start(Log.Folder); } catch { Notice("could not open logs folder"); } });
            menu.Items.Add("save detection snapshot", null, (o, e) => { bot.RequestSnapshot(); Notice("snapshot saved to logs"); });
            DarkMenuRenderer.Apply(menu);
            footer.ContextMenuStrip = menu;
            VisibleChanged += (o, e) => UpdateTrayText();
            Resize += (o, e) => UpdateTrayText();
            if (launchWindow) CreateTray();
            BuildAll(0);

            timer.Tick += (o, e) => Tick();
            Load += (o, e) => { bot.Start(); timer.Start(); Log.Write("menu opened"); };
            FormClosing += (o, e) => Shutdown();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle = launchWindow ? (p.ExStyle & ~0x80) | 0x40000 : (p.ExStyle & ~0x40000) | 0x80;
                return p;
            }
        }

        void BuildAll(int selected)
        {
            for (int i = 0; i < PageCount; i++)
            {
                if (pages[i] != null) { Controls.Remove(pages[i]); pages[i].Dispose(); }
                pages[i] = new Panel { Location = new Point(16, 57), Size = new Size(448, 179), BackColor = Skin.Background, Visible = i == selected };
                Controls.Add(pages[i]);
            }
            footer.SetBounds(18, 245, 444, 17);
            BuildAutoplay(); BuildAchievements(); BuildMisc();
            tabs.Selected = selected;
            if (Skin.Scale != 1f) Scale(new SizeF(Skin.Scale, Skin.Scale));
            UpdateText();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                float dpiScale = ((int)((long)m.WParam & 0xFFFF)) / 96f;
                var r = (Native.RECT)System.Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                var target = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                Size screen; try { screen = Screen.FromRectangle(target).Bounds.Size; } catch { screen = new Size(1920, 1080); }
                float scale = Program.ComputeScale(dpiScale, screen);
                if (Math.Abs(scale - Skin.Scale) > 0.001f) RebuildUI(scale);
                Location = new Point(r.Left, r.Top);
            }
            base.WndProc(ref m);
        }

        // Rescaling an already scaled control tree drifts from rounding, so the tree is rebuilt from the
        // 480x274 baseline instead.
        void RebuildUI(float scale)
        {
            int selected = tabs.Selected;
            Skin.InitializeScale(scale);
            ClientSize = new Size(480, 274); Font = Skin.Font;
            tabs.SetBounds(TabsX, 5, 465 - TabsX, 40);
            BuildAll(selected);
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            int i3 = Skin.D(3), i7 = Skin.D(7), i10 = Skin.D(10), i50 = Skin.D(50), i20 = Skin.D(20), i88 = Skin.D(88), i5 = Skin.D(5), i11 = Skin.D(11);
            using (var p = new Pen(Color.Black)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using (var p = new Pen(Skin.Border)) { g.DrawRectangle(p, i3, i3, Width - i7, Height - i7); g.DrawRectangle(p, i10, i50, Width - i20, Height - i88); }
            using (var p = new Pen(Color.FromArgb(22, 22, 24))) g.DrawRectangle(p, i5, i5, Width - i11, Height - i11);
            TextRenderer.DrawText(g, "AutoShaper", Skin.Logo,
                new Rectangle(0, Skin.D(5), Skin.D(TabsX), Skin.D(35)), Color.White,
                TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        void DragMenu(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); } }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.X < Skin.D(TabsX) && e.Y < Skin.D(50)) DragMenu(e); base.OnMouseDown(e); }

        void SelectPage(int index)
        {
            if (index < 0 || index >= PageCount) return;
            tabs.Selected = index; tabs.Invalidate();
            for (int i = 0; i < PageCount; i++) pages[i].Visible = i == index;
            UpdateText();
            Invalidate(true);
        }

        FieldGroup Group(Control parent, string title, int x, int y, int w, int h) { var g = new FieldGroup(title, x, y, w, h); parent.Controls.Add(g); return g; }

        Label Label(Control parent, string text, int x, int y, int w, int h = 18)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = Skin.Muted, Font = Skin.Font, BackColor = Color.Transparent, AutoEllipsis = true };
            parent.Controls.Add(l);
            return l;
        }

        MicroButton Button(Control parent, string text, int x, int y, int w, Action action)
        {
            var b = new MicroButton(text, () => { try { action(); } catch (Exception e) { Log.Write("'" + text + "' failed: " + e.Message); Notice(e.Message); } }) { Location = new Point(x, y), Size = new Size(w, 24) };
            parent.Controls.Add(b);
            return b;
        }

        void Check(Control parent, string text, int x, int y, int w, Func<bool> getter, Action<bool> setter)
        {
            var c = new MicroCheck(text, getter, v => { setter(v); Changed(); parent.Invalidate(true); }) { Location = new Point(x, y), Width = w };
            parent.Controls.Add(c);
            locked.Add(c);
        }

        // Slider with exact entry (click the value to type one). Both paths clamp to [min, max], and max
        // is the achievement ceiling, so typed values can't exceed it either.
        void Slider(FieldGroup parent, string text, int y, int min, int max, Func<int> getter, Action<int> setter)
        {
            Action<int> set = v => { setter(Rules.Clamp(v, min, max)); Changed(); };
            var slider = new MicroSlider(text, min, max, 1, () => getter(), v => set((int)v)) { Location = new Point(12, y), Width = 420 };
            parent.Controls.Add(slider);
            locked.Add(slider);
            var exact = new TextBox { Visible = false, TextAlign = HorizontalAlignment.Right, BackColor = Skin.Panel, ForeColor = Skin.Text, Font = Skin.Font, BorderStyle = BorderStyle.FixedSingle, Location = new Point(250, y - 2), Size = new Size(170, 23), MaxLength = 6 };
            parent.Controls.Add(exact);
            exact.BringToFront();
            bool ending = false;
            Action<bool> finish = commit =>
            {
                if (!exact.Visible || ending) return;
                ending = true;
                int value;
                if (commit && int.TryParse(exact.Text.Trim(), out value))
                {
                    int clamped = Rules.Clamp(value, min, max);
                    set(clamped);
                    if (clamped != value) Notice(text + " is limited to " + min + "–" + max);
                }
                exact.Hide(); slider.Invalidate(); ending = false;
            };
            slider.EditValue = () => { exact.Text = getter().ToString(); exact.Show(); exact.Focus(); exact.SelectAll(); };
            exact.KeyDown += (o, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Escape) { finish(e.KeyCode == Keys.Enter); e.SuppressKeyPress = true; slider.Focus(); } };
            exact.KeyPress += (o, e) => { if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true; };
            exact.LostFocus += (o, e) => finish(true);
        }

        void BuildAutoplay()
        {
            locked.Clear();
            var p = Group(pages[0], "perfect play", 0, 0, 448, 148);
            Check(p, "loop", 14, 24, 200, () => settings.Loop, v => settings.Loop = v);
            if (settings.Uncapped) { var tag = Label(p, "uncapped", 230, 25, 204); tag.TextAlign = ContentAlignment.MiddleRight; tag.ForeColor = Skin.Accent; }
            Slider(p, "stop at score", 57, 0, settings.Uncapped ? Rules.UncappedMax : Rules.NormalCap, () => settings.StopScore, v => settings.StopScore = v);
            startButton = Button(p, "start autoplay", 14, 108, 420, ToggleAutoplay);
            lockedFor = null;
        }
        void BuildAchievements()
        {
            var g = Group(pages[1], "achievements", 0, 0, 448, 174);
            achProgress = Label(g, "-", 14, 24, 420); achProgress.ForeColor = Skin.Text;
            achNext = Label(g, "", 14, 50, 420); achNext.ForeColor = Skin.Text;
            achQueue = Label(g, "", 14, 72, 420, 36);
            achManual = Label(g, "", 14, 114, 420); achManual.ForeColor = Skin.Footnote;
            farmButton = Button(g, "start farming", 14, 140, 420, ToggleFarm);
            previewAt = DateTime.MinValue;
        }

        void BuildMisc()
        {
            var binds = Group(pages[2], "bindings", 0, 0, 218, 94);
            Label(binds, "menu", 14, 29, 87);
            binds.Controls.Add(new BindingEditor(() => settings.MenuKey, () => settings.MenuModifiers, (k, m) => SetBinding(true, k, m), keys) { Location = new Point(105, 24), Width = 99 });
            Label(binds, "start/stop", 14, 63, 87);
            binds.Controls.Add(new BindingEditor(() => settings.PauseKey, () => settings.PauseModifiers, (k, m) => SetBinding(false, k, m), keys) { Location = new Point(105, 58), Width = 99 });

            var app = Group(pages[2], "app", 230, 0, 218, 94);
            Button(app, "save settings", 14, 24, 92, () => { settings.Save(); Notice("settings saved"); });
            locked.Add(Button(app, "reset settings", 112, 24, 92, ResetSettings));
            Button(app, "unload AutoShaper", 14, 58, 190, Close);

            // Both actions arm first and need the confirm button; arming one disarms the other.
            var danger = Group(pages[2], "danger zone", 0, 100, 448, 79);
            var allow = new MicroCheck("allow uncapped scores", () => settings.Uncapped || armed == Armed.Uncapped, RequestUncapped) { Location = new Point(14, 22), Width = 163 };
            danger.Controls.Add(allow);
            locked.Add(allow);
            locked.Add(Button(danger, "restore leaderboards", 14, 48, 163, () => { armed = armed == Armed.Restore ? Armed.None : Armed.Restore; UpdateDanger(); }));
            dangerWarning = Label(danger, "", 190, 20, 244, 30);
            dangerConfirm = Button(danger, "", 190, 48, 100, ConfirmDanger);
            locked.Add(dangerConfirm);
            UpdateDanger();
        }

        void RequestUncapped(bool value)
        {
            if (value) { if (!settings.Uncapped) armed = Armed.Uncapped; UpdateDanger(); return; }
            if (armed == Armed.Uncapped) armed = Armed.None;
            if (settings.Uncapped)
            {
                settings.Uncapped = false;
                settings.StopScore = Math.Min(settings.StopScore, Rules.NormalCap);
                Changed();
                Log.Write("score caps restored");
                RebuildUI(Skin.Scale);
                return;
            }
            UpdateDanger();
        }

        void ConfirmDanger()
        {
            var action = armed;
            armed = Armed.None;
            if (action == Armed.Uncapped)
            {
                settings.Uncapped = true;
                Changed();
                Log.Write("score caps lifted for autoplay");
                RebuildUI(Skin.Scale);
            }
            else if (action == Armed.Restore) RestoreLeaderboards();
            else UpdateDanger();
        }

        void RestoreLeaderboards()
        {
            if (bot.Activity != Activity.Idle) { Notice("stop first"); UpdateDanger(); return; }
            if (Process.GetProcessesByName("WAVESHAPER").Length > 0) { Notice("close the game first"); UpdateDanger(); return; }
            restoring = true;
            UpdateDanger();
            Notice("restoring leaderboards…");
            var worker = new Thread(() =>
            {
                string summary; int code;
                try { code = LeaderboardRestore.RunHelper(out summary); }
                catch (Exception ex) { code = LeaderboardRestore.Failed; summary = ex.Message; }
                BeginInvoke(new Action(() =>
                {
                    restoring = false;
                    UpdateDanger();
                    Notice(summary.Length > 0 ? summary : code == LeaderboardRestore.Ok ? "leaderboards restored" : "restore failed (see log)");
                }));
            }) { IsBackground = true };
            worker.Start();
        }

        void UpdateDanger()
        {
            if (dangerWarning == null) return;
            string text; bool warn = true;
            switch (armed)
            {
                case Armed.Uncapped: text = "scores go to public leaderboards and can\nspoil friend rankings for other players"; break;
                case Armed.Restore: text = "lowers your top scores and leaderboard\nentries to 420 / 260 (close the game)"; break;
                default:
                    warn = settings.Uncapped;
                    text = restoring ? "restoring leaderboards…" : settings.Uncapped ? "autoplay score caps are lifted" : "lifts the 420 / 260 autoplay score caps";
                    break;
            }
            dangerWarning.Text = text;
            dangerWarning.ForeColor = warn ? Skin.Accent : Skin.Muted;
            dangerConfirm.Text = armed == Armed.Restore ? "restore" : "enable";
            dangerConfirm.Visible = armed != Armed.None && !restoring;
            dangerWarning.Parent.Invalidate(true);
        }
        void Changed()
        {
            settings.Save();
            bot.UpdateSettings(settings);
            UpdateText();
        }

        // Autoplay and the farm never overlap: each start is refused while the other runs.
        void ToggleAutoplay()
        {
            if (bot.Activity == Activity.Autoplay) bot.StopAll();
            else if (bot.Activity == Activity.Farm) Notice("stop the achievement farm first");
            else bot.StartAutoplay();
            UpdateText();
        }

        void ToggleFarm()
        {
            if (bot.Activity == Activity.Farm) bot.StopAll();
            else if (bot.Activity == Activity.Autoplay) Notice("stop autoplay first");
            else if (SteamStats.Load() == null) Notice("steam stats not found");
            else bot.StartFarm();
            UpdateText();
        }

        // The start/stop binding always stops whatever runs; when idle it starts the current tab's action.
        void HotkeyToggle()
        {
            if ((DateTime.UtcNow - lastHotkeyAt).TotalMilliseconds < 400) return;   // key repeat or double press
            lastHotkeyAt = DateTime.UtcNow;
            if (bot.Activity != Activity.Idle) { bot.StopAll(); UpdateText(); }
            else if (tabs.Selected == 1) ToggleFarm();
            else ToggleAutoplay();
        }

        void ResetSettings()
        {
            if (bot.Activity != Activity.Idle) { Notice("stop first"); return; }
            var d = new Settings();
            settings.StopScore = d.StopScore; settings.Loop = d.Loop; settings.Uncapped = d.Uncapped;
            settings.MenuKey = d.MenuKey; settings.MenuModifiers = d.MenuModifiers; settings.PauseKey = d.PauseKey; settings.PauseModifiers = d.PauseModifiers;
            armed = Armed.None;
            Changed();
            ApplyBindings();
            RebuildUI(Skin.Scale);
            Notice("settings reset");
        }

        void SetBinding(bool menu, int key, int mods)
        {
            if (key != 0 && (menu ? settings.PauseKey : settings.MenuKey) == key && (menu ? settings.PauseModifiers : settings.MenuModifiers) == mods) { Notice("choose a different binding for each action"); return; }
            if (menu) { settings.MenuKey = key; settings.MenuModifiers = mods; } else { settings.PauseKey = key; settings.PauseModifiers = mods; }
            Changed();
            ApplyBindings();
        }

        void ApplyBindings() { if (keys != null) keys.Configure(settings.MenuKey, settings.PauseKey); }

        static bool Matches(int assigned, int modifiers, int key, int observed) { return assigned != 0 && assigned == key && modifiers == observed; }

        void OnBinding(int key, int mods, Point point)
        {
            if (closing || BindingEditor.Capturing) return;
            bool isMouse = key < 7 || key >= 256;
            if (isMouse && Visible && Bounds.Contains(point)) return;
            bool menu = Matches(settings.MenuKey, settings.MenuModifiers, key, mods), pause = Matches(settings.PauseKey, settings.PauseModifiers, key, mods);
            if (!menu && !pause) return;
            BeginInvoke(new Action(() =>
            {
                if (closing || BindingEditor.Capturing) return;
                if (menu) { if (Visible && WindowState != FormWindowState.Minimized) HideMenu(); else ShowMenu(); }
                else HotkeyToggle();
            }));
        }

        void ToggleMenu()
        {
            if (closing) return;
            if (Visible && WindowState != FormWindowState.Minimized) HideMenu(); else ShowMenu();
        }

        void HideMenu()
        {
            settings.Save();
            Hide();
            if (Native.IsWindow(previous)) Native.SetForegroundWindow(previous);
        }

        void ShowMenu()
        {
            UpdateText();
            previous = Native.GetForegroundWindow();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate(); Native.SetForegroundWindow(Handle);
        }

        void CreateTray()
        {
            try
            {
                if (Icon == null) Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (Icon == null) throw new InvalidOperationException("application icon unavailable");
                trayMenu = new ContextMenuStrip();
                DarkMenuRenderer.Apply(trayMenu);
                trayToggle = new ToolStripMenuItem();
                trayToggle.Click += (o, e) => ToggleMenu();
                trayMenu.Items.Add(trayToggle);
                trayMenu.Items.Add(new ToolStripSeparator());
                trayMenu.Items.Add("unload AutoShaper", null, (o, e) => Close());
                trayMenu.Opening += (o, e) => UpdateTrayText();
                tray = new NotifyIcon { Icon = Icon, Text = "AutoShaper", ContextMenuStrip = trayMenu };
                tray.MouseClick += (o, e) => { if (e.Button == MouseButtons.Left) ToggleMenu(); };
                tray.Visible = true;
                UpdateTrayText();
            }
            catch
            {
                Log.Write("tray.unavailable");
                DisposeTray();
                Notice("tray icon unavailable");
            }
        }

        void UpdateTrayText()
        {
            if (trayToggle != null) trayToggle.Text = Visible && WindowState != FormWindowState.Minimized ? "hide menu" : "show menu";
        }

        void DisposeTray()
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.ContextMenuStrip = null;
                tray.Dispose();
                tray = null;
            }
            if (trayMenu != null) { trayMenu.Dispose(); trayMenu = null; }
            trayToggle = null;
        }

        void Tick()
        {
            if (reopen != null && reopen.WaitOne(0)) ShowMenu();
            if (Visible) UpdateText();
        }

        void Notice(string message) { notice = message; noticeUntil = DateTime.UtcNow.AddSeconds(6); UpdateText(); }

        void UpdateText()
        {
            if (bot == null || startButton == null) return;
            var s = bot.Status;
            var act = bot.Activity;
            string label = act == Activity.Autoplay ? "stop autoplay" : "start autoplay";
            if (startButton.Text != label) { startButton.Text = label; startButton.Invalidate(); }
            if (lockedFor != act)
            {
                lockedFor = act;
                foreach (var ctl in locked) { ctl.Enabled = act == Activity.Idle; ctl.Invalidate(); }
                startButton.Enabled = act != Activity.Farm; startButton.Invalidate();
            }
            RefreshAchievements(s, act);
            if (s.FarmNote != lastFarmNote) { lastFarmNote = s.FarmNote; if (s.FarmNote.Length > 0) Notice(s.FarmNote); }

            // Footer: game state · run detail · activity · progress · notice.
            var parts = new List<FooterBar.Part>();
            if (!s.Attached) parts.Add(new FooterBar.Part("waiting for game", Skin.Muted));
            else if (!s.InRun) parts.Add(new FooterBar.Part(s.Health == 0 ? "game over" : "attached", Skin.Muted));
            else
            {
                parts.Add(new FooterBar.Part(s.Difficulty, Skin.Muted));
                parts.Add(new FooterBar.Part(s.Score + "/" + s.Cap, Skin.Text));
                if (s.Health > 0) parts.Add(new FooterBar.Part(new string('■', s.Health) + new string('□', 3 - s.Health), Skin.HealthColour(s.Health)));
            }
            parts.Add(new FooterBar.Part(act == Activity.Farm ? "farming" : act == Activity.Autoplay ? "running" : "stopped", act == Activity.Idle ? Skin.Muted : Skin.Text));
            if (act == Activity.Farm && s.Job.Length > 0) parts.Add(new FooterBar.Part(s.Job, Skin.Muted));
            if (DateTime.UtcNow < noticeUntil) parts.Add(new FooterBar.Part(notice, Skin.Accent));
            else if (s.Note.Length > 0) parts.Add(new FooterBar.Part(s.Note, Skin.Accent));
            footer.SetParts(parts);
        }

        void RefreshAchievements(BotStatus s, Activity act)
        {
            if (farmButton == null) return;
            if ((pages[1].Visible || act == Activity.Farm) && (DateTime.UtcNow - previewAt).TotalMilliseconds > 3000)
            {
                preview = SteamStats.Load();
                previewAt = DateTime.UtcNow;
            }
            string button = act == Activity.Farm ? "stop farming" : "start farming";
            if (farmButton.Text != button) { farmButton.Text = button; farmButton.Invalidate(); }
            bool usable = act == Activity.Farm || (act == Activity.Idle && preview != null);
            if (farmButton.Enabled != usable) { farmButton.Enabled = usable; farmButton.Invalidate(); }
            if (!pages[1].Visible) return;
            if (preview == null)
            {
                Set(achProgress, "steam stats not found");
                Set(achNext, "launch the game once with steam running");
                Set(achQueue, ""); Set(achManual, "");
                return;
            }
            var plan = Planner.Plan(preview, null);
            Set(achProgress, preview.UnlockedCount + " / " + preview.Achievements.Count + " unlocked");
            Set(achNext, act == Activity.Farm && s.Job.Length > 0 ? "now: " + s.Job : plan.Count == 0 ? "nothing left to farm" : "next: " + plan[0].Label);
            var rest = new List<string>();
            for (int i = 1; i < plan.Count; i++) rest.Add(plan[i].Label);
            Set(achQueue, rest.Count > 0 ? "then: " + string.Join(" · ", rest.ToArray()) : "");
            var manual = new List<string>();
            foreach (var a in Planner.Manual(preview)) manual.Add(a.Title.ToLowerInvariant());
            Set(achManual, manual.Count > 0 ? string.Join(", ", manual.ToArray()) + (manual.Count == 1 ? " needs" : " need") + " manual play" : "");
        }

        static void Set(Label l, string text) { if (l.Text != text) l.Text = text; }

        // --render: saves each tab as an image.
        public void Render(string directory)
        {
            Directory.CreateDirectory(directory);
            Opacity = 0; Show();
            string[] names = { "autoplay", "achievements", "misc" };
            for (int i = 0; i < PageCount; i++)
            {
                SelectPage(i); Application.DoEvents();
                Snap(Path.Combine(directory, names[i] + ".png"));
            }
            SelectPage(2);
            RequestUncapped(true); Application.DoEvents();
            Snap(Path.Combine(directory, "misc-uncapped-armed.png"));
            RequestUncapped(false);
            armed = Armed.Restore; UpdateDanger(); Application.DoEvents();
            Snap(Path.Combine(directory, "misc-restore-armed.png"));
            armed = Armed.None; UpdateDanger();
            SelectPage(0);
            Hide();
        }

        void Snap(string path)
        {
            using (var b = new Bitmap(Width, Height)) { DrawToBitmap(b, ClientRectangle); b.Save(path); }
        }

        void Shutdown()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            bot.StopAll();
            bot.Dispose();
            if (keys != null) keys.Dispose();
            DisposeTray();
            settings.Save();
            Log.Write("menu closed");
        }
    }

    // One-line status with coloured segments, so health can use the game's own line colours.
    sealed class FooterBar : Control
    {
        public struct Part
        {
            public readonly string Text; public readonly Color Colour;
            public Part(string text, Color colour) { Text = text; Colour = colour; }
        }

        List<Part> parts = new List<Part>();
        string signature = "";

        public FooterBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Skin.Background; }

        public void SetParts(List<Part> next)
        {
            var sig = new System.Text.StringBuilder();
            foreach (var p in next) sig.Append(p.Text).Append('\u0001').Append(p.Colour.ToArgb()).Append('\u0002');
            if (sig.ToString() == signature) return;
            signature = sig.ToString();
            parts = next;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Skin.Background);
            const TextFormatFlags f = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            int x = 0;
            for (int i = 0; i < parts.Count && x < Width; i++)
            {
                if (i > 0) x = Draw(g, " · ", Skin.Muted, x, f);
                x = Draw(g, parts[i].Text, parts[i].Colour, x, f);
            }
        }

        int Draw(Graphics g, string text, Color colour, int x, TextFormatFlags f)
        {
            TextRenderer.DrawText(g, text, Skin.Font, new Rectangle(x, 0, Width - x, Height), colour, f | TextFormatFlags.EndEllipsis);
            return x + TextRenderer.MeasureText(g, text, Skin.Font, Size.Empty, f).Width;
        }
    }
}
