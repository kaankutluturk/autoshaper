using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoShaper
{
    // Global bindings poll only the configured keys and mouse buttons. The low-level
    // mouse hook is present only for a wheel binding or while the binding editor captures.
    static class KeyNames
    {
        public static string Name(int key, int mods = 0)
        {
            string name;
            switch (key)
            {
                case 0: return "none";
                case 1: name = "mouse 1"; break; case 2: name = "mouse 2"; break; case 4: name = "mouse 3"; break; case 5: name = "mouse 4"; break; case 6: name = "mouse 5"; break;
                case 16: name = "shift"; break; case 17: name = "ctrl"; break; case 18: name = "alt"; break;
                case 256: name = "wheel up"; break; case 257: name = "wheel down"; break; case 258: name = "wheel left"; break; case 259: name = "wheel right"; break;
                default: name = ((Keys)key).ToString().ToLowerInvariant(); break;
            }
            return ((mods & 2) != 0 ? "ctrl + " : "") + ((mods & 4) != 0 ? "shift + " : "") + ((mods & 1) != 0 ? "alt + " : "") + ((mods & 8) != 0 ? "win + " : "") + name;
        }
    }

    sealed class KeyMonitor : IDisposable
    {
        delegate IntPtr Hook(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        const int WhMouseLl = 14, SmSwapButton = 23;
        readonly Hook mouse;
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Dictionary<int, int> watched = new Dictionary<int, int>();
        readonly bool[] down = new bool[256];
        IntPtr mh;
        int menuKey, pauseKey;
        bool capturing, disposed;
        public Action<int, int, Point> Pressed, Released;

        public KeyMonitor()
        {
            mouse = MouseEvent;
            timer.Tick += Poll;
            timer.Start();
        }

        public void Configure(int menu, int pause)
        {
            menuKey = menu; pauseKey = pause;
            Rebuild();
        }

        public void SetCapturing(bool value)
        {
            if (capturing == value) return;
            capturing = value;
            Rebuild();
        }

        void Rebuild()
        {
            watched.Clear();
            WatchBinding(menuKey); WatchBinding(pauseKey);
            if (capturing)
            {
                WatchModifiers();
                WatchBinding(1); WatchBinding(2); WatchBinding(4); WatchBinding(5); WatchBinding(6);
            }

            Array.Clear(down, 0, down.Length);
            foreach (var pair in watched) down[pair.Key] = IsDown(pair.Key);
            UpdateWheelHook(capturing || IsWheel(menuKey) || IsWheel(pauseKey));
        }

        void WatchBinding(int key)
        {
            if (key == 0 || IsWheel(key)) return;
            if (key == 16) { Watch(160, 16); Watch(161, 16); }
            else if (key == 17) { Watch(162, 17); Watch(163, 17); }
            else if (key == 18) { Watch(164, 18); Watch(165, 18); }
            else if (key == 1 || key == 2)
            {
                bool swapped = Native.GetSystemMetrics(SmSwapButton) != 0;
                int source = key == 1 ? (swapped ? 2 : 1) : (swapped ? 1 : 2);
                Watch(source, key);
            }
            else if (key == 4 || key == 5 || key == 6) Watch(key, key);
            else if (key > 6 && key < 256) Watch(key, key);
        }

        void WatchModifiers()
        {
            Watch(160, 16); Watch(161, 16); Watch(162, 17); Watch(163, 17);
            Watch(164, 18); Watch(165, 18); Watch(91, 91); Watch(92, 92);
        }

        void Watch(int source, int key) { watched[source] = key; }

        void UpdateWheelHook(bool needed)
        {
            if (needed && mh == IntPtr.Zero) mh = SetWindowsHookEx(WhMouseLl, mouse, GetModuleHandle(null), 0);
            else if (!needed && mh != IntPtr.Zero) { UnhookWindowsHookEx(mh); mh = IntPtr.Zero; }
        }

        static bool IsWheel(int key) { return key >= 256 && key <= 259; }

        static bool IsDown(int key) { return (Native.GetAsyncKeyState(key) & 0x8000) != 0; }

        public static int CurrentModifiers(int key)
        {
            int m = 0;
            if ((IsDown(162) || IsDown(163)) && key != 17) m |= 2;
            if ((IsDown(160) || IsDown(161)) && key != 16) m |= 4;
            if ((IsDown(164) || IsDown(165)) && key != 18) m |= 1;
            if ((IsDown(91) || IsDown(92)) && key != 91 && key != 92) m |= 8;
            return m;
        }

        void Poll(object sender, EventArgs e)
        {
            var changes = new List<KeyValuePair<int, bool>>();
            foreach (var pair in watched)
            {
                bool held = IsDown(pair.Key);
                if (held != down[pair.Key]) { down[pair.Key] = held; changes.Add(new KeyValuePair<int, bool>(pair.Key, held)); }
            }
            foreach (var change in changes)
            {
                int key = watched[change.Key];
                var handler = change.Value ? Pressed : Released;
                if (handler != null) handler(key, CurrentModifiers(key), key <= 6 ? Cursor.Position : Point.Empty);
            }
        }

        IntPtr MouseEvent(int c, IntPtr w, IntPtr l)
        {
            if (c >= 0)
            {
                var m = (MouseData)Marshal.PtrToStructure(l, typeof(MouseData));
                int msg = w.ToInt32();
                if ((m.Flags & 1) == 0 && (msg == 0x20a || msg == 0x20e))
                {
                    int delta = (short)(m.Data >> 16);
                    int key = msg == 0x20a ? (delta > 0 ? 256 : 257) : (delta > 0 ? 259 : 258);
                    var handler = Pressed;
                    if (handler != null) handler(key, CurrentModifiers(key), new Point(m.X, m.Y));
                }
            }
            return CallNextHookEx(mh, c, w, l);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop(); timer.Tick -= Poll; timer.Dispose();
            if (mh != IntPtr.Zero) { UnhookWindowsHookEx(mh); mh = IntPtr.Zero; }
        }
    }

    sealed class BindingEditor : Control
    {
        readonly Func<int> key, mods; readonly Action<int, int> set; readonly KeyMonitor monitor;
        static BindingEditor active; public static bool Capturing { get { return active != null; } }
        int modifier; bool finishing;

        public BindingEditor(Func<int> getKey, Func<int> getMods, Action<int, int> setter, KeyMonitor input)
        {
            key = getKey; mods = getMods; set = setter; monitor = input; Height = 23; TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.Selectable | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            using (var p = new Pen(active == this ? Skin.Accent : Skin.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            Skin.TextAt(e.Graphics, active == this ? "" : KeyNames.Name(key(), mods()), ClientRectangle, Skin.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        void Edit()
        {
            if (active != null) active.End();
            Focus(); active = this; modifier = 0; finishing = false;
            if (monitor != null) { monitor.SetCapturing(true); monitor.Pressed += OnPress; monitor.Released += OnRelease; }
            Invalidate();
        }
        void End()
        {
            if (monitor != null) { monitor.Pressed -= OnPress; monitor.Released -= OnRelease; monitor.SetCapturing(false); }
            if (active == this) active = null;
            modifier = 0; Invalidate();
        }
        static int Normalize(int k) { return k == 160 || k == 161 ? 16 : k == 162 || k == 163 ? 17 : k == 164 || k == 165 ? 18 : k; }
        static bool IsModifier(int k) { return k == 16 || k == 17 || k == 18 || k == 91 || k == 92; }
        void OnPress(int k, int m, Point point) { if (active != this || finishing) return; if (IsModifier(k)) { modifier = k; return; } Accept(k == 27 ? 0 : k, k == 27 ? 0 : m); }
        void OnRelease(int k, int m, Point point) { if (active == this && !finishing && k == modifier) Accept(k, 0); }
        void Accept(int k, int m) { finishing = true; BeginInvoke(new Action(() => { if (active != this) return; set(k, m); End(); })); }
        protected override void OnMouseDown(MouseEventArgs e) { if (active != this && e.Button == MouseButtons.Left) Edit(); base.OnMouseDown(e); }
        protected override bool ProcessCmdKey(ref Message message, Keys data)
        {
            if (active == this)
            {
                int key = Normalize((int)(data & Keys.KeyCode));
                OnPress(key, KeyMonitor.CurrentModifiers(key), Cursor.Position);
                return true;
            }
            if (data == Keys.Enter || data == Keys.Space) { Edit(); return true; }
            return base.ProcessCmdKey(ref message, data);
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            int key = Normalize((int)e.KeyCode);
            if (active == this && !finishing && IsModifier(key) && key == modifier) Accept(key, 0);
            base.OnKeyUp(e);
        }
        protected override void OnLostFocus(EventArgs e) { if (active == this && !finishing) End(); base.OnLostFocus(e); }
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible && active == this) End(); base.OnVisibleChanged(e); }
        protected override void Dispose(bool disposing) { if (disposing) End(); base.Dispose(disposing); }
    }
}
