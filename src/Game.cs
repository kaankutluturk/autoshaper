using System;
using System.Diagnostics;
using System.IO;

namespace AutoShaper
{
    // Finds the WAVESHAPER window and captures the gameplay band.
    sealed class GameWindow : IDisposable
    {
        public IntPtr Handle { get; private set; }
        public int ProcessId { get; private set; }
        public int ClientX { get; private set; }
        public int ClientY { get; private set; }
        public int ClientW { get; private set; }
        public int ClientH { get; private set; }
        public double Scale { get; private set; }
        public double OffX { get; private set; }
        public double OffY { get; private set; }

        IntPtr screenDC, memDC, dib, bits, oldObj;
        int dibW, dibH;
        DateTime lastLookup = DateTime.MinValue;

        public bool Attached { get { return Handle != IntPtr.Zero && Native.IsWindow(Handle); } }

        // Re-resolves the process at most twice a second; client geometry is refreshed every call.
        public bool Refresh()
        {
            if (!Attached || (DateTime.UtcNow - lastLookup).TotalMilliseconds > 500)
            {
                lastLookup = DateTime.UtcNow;
                if (!Attached)
                {
                    Handle = IntPtr.Zero;
                    foreach (var p in Process.GetProcessesByName("WAVESHAPER"))
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) { Handle = p.MainWindowHandle; ProcessId = p.Id; break; }
                    }
                }
            }
            if (!Attached || Native.IsIconic(Handle)) return false;
            Native.RECT r;
            if (!Native.GetClientRect(Handle, out r) || r.Right < 100 || r.Bottom < 60) return false;
            var pt = new Native.POINT();
            Native.ClientToScreen(Handle, ref pt);
            ClientX = pt.X; ClientY = pt.Y; ClientW = r.Right; ClientH = r.Bottom;
            Scale = Math.Min(ClientW / (double)Vision.RoomW, ClientH / (double)Vision.RoomH);
            OffX = (ClientW - Vision.RoomW * Scale) / 2;
            OffY = (ClientH - Vision.RoomH * Scale) / 2;
            return true;
        }

        // PrintWindow reads the game's own surface, so windows on top of it (including this app)
        // do not leak into the capture. Falls back to copying the screen if PrintWindow yields nothing.
        public bool UseScreenCopy { get; private set; }
        int blankPrints;

        public unsafe Frame Capture()
        {
            int y0 = (int)Math.Floor(OffY + Vision.BandTop * Scale);
            int y1 = Math.Min(ClientH, (int)Math.Ceiling(OffY + Vision.BandBottom * Scale));
            int w = ClientW, h = Math.Max(1, y1 - y0);
            if (!UseScreenCopy)
            {
                EnsureBuffer(w, ClientH);
                byte* band = (byte*)bits + y0 * w * 4;
                Fill((uint*)band, w * h);
                if (Native.PrintWindow(Handle, memDC, Native.PW_CLIENTONLY | Native.PW_RENDERFULLCONTENT) && !Untouched((uint*)band, w * h))
                {
                    blankPrints = 0; bandY0 = y0; bandH = h;
                    return Vision.Analyze(band, w * 4, w, h, y0, Scale, OffX, OffY);
                }
                if (++blankPrints >= 30) { UseScreenCopy = true; Log.Write("PrintWindow returned nothing; using screen copy (keep the game uncovered)"); }
                return null;
            }
            EnsureBuffer(w, h);
            if (!Native.BitBlt(memDC, 0, 0, w, h, screenDC, ClientX, ClientY + y0, Native.SRCCOPY)) return null;
            bandY0 = 0; bandH = h;
            return Vision.Analyze((byte*)bits, w * 4, w, h, y0, Scale, OffX, OffY);
        }

        int bandY0, bandH;

        // A black band is normal, so detect a capture that wrote nothing with a sentinel fill instead.
        const uint Sentinel = 0x01FE02FDu;
        static unsafe void Fill(uint* p, int n) { for (int i = 0; i < n; i++) p[i] = Sentinel; }
        static unsafe bool Untouched(uint* p, int n)
        {
            for (int i = 0; i < n; i += 97) if (p[i] != Sentinel) return false;
            return true;
        }

        // Saves the last captured band as a BMP for troubleshooting detection.
        public void SaveLastCapture(string path)
        {
            if (bits == IntPtr.Zero) return;
            int stride = dibW * 4, rows = Math.Min(bandH, dibH - bandY0), size = stride * rows;
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0x4D42); bw.Write(54 + size); bw.Write(0); bw.Write(54);
                bw.Write(40); bw.Write(dibW); bw.Write(-rows); bw.Write((short)1); bw.Write((short)32);
                bw.Write(0); bw.Write(size); bw.Write(2835); bw.Write(2835); bw.Write(0); bw.Write(0);
                var buf = new byte[size];
                System.Runtime.InteropServices.Marshal.Copy(bits + bandY0 * stride, buf, 0, size);
                bw.Write(buf);
            }
        }

        void EnsureBuffer(int w, int h)
        {
            if (screenDC == IntPtr.Zero) { screenDC = Native.GetDC(IntPtr.Zero); memDC = Native.CreateCompatibleDC(screenDC); }
            if (dib != IntPtr.Zero && w == dibW && h == dibH) return;
            FreeDib();
            var bi = new Native.BITMAPINFOHEADER();
            bi.biSize = 40; bi.biWidth = w; bi.biHeight = -h; bi.biPlanes = 1; bi.biBitCount = 32;
            dib = Native.CreateDIBSection(screenDC, ref bi, 0, out bits, IntPtr.Zero, 0);
            oldObj = Native.SelectObject(memDC, dib);
            dibW = w; dibH = h;
        }

        void FreeDib()
        {
            if (dib == IntPtr.Zero) return;
            Native.SelectObject(memDC, oldObj);
            Native.DeleteObject(dib);
            dib = IntPtr.Zero; bits = IntPtr.Zero;
        }

        public void Dispose()
        {
            FreeDib();
            if (memDC != IntPtr.Zero) { Native.DeleteDC(memDC); memDC = IntPtr.Zero; }
            if (screenDC != IntPtr.Zero) { Native.ReleaseDC(IntPtr.Zero, screenDC); screenDC = IntPtr.Zero; }
        }
    }


    // Presses the game's bound keys. Only one key is ever held, and only briefly: while a key is
    // held the game treats it as the active press, so a held mismatch against a colliding shape
    // would count as a wrong key.
    sealed class KeyPresser
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        int heldVk;
        IntPtr heldWindow;
        long releaseAt;

        public int HoldMs = 40;

        public void Tap(IntPtr window, int vk)
        {
            ReleaseNow();
            Send(window, vk, true);
            heldVk = vk; heldWindow = window;
            releaseAt = clock.ElapsedMilliseconds + HoldMs;
        }

        public void Update()
        {
            if (heldVk != 0 && clock.ElapsedMilliseconds >= releaseAt) ReleaseNow();
        }

        public bool Holding { get { return heldVk != 0; } }

        public void ReleaseNow()
        {
            if (heldVk == 0) return;
            Send(heldWindow, heldVk, false);
            heldVk = 0;
        }

        // Posted key messages reach the game even while another window has focus.
        static void Send(IntPtr window, int vk, bool down)
        {
            uint scan = Native.MapVirtualKey((uint)vk, 0);
            uint l = 1u | (scan << 16);
            if (!down) l |= 0xC0000000u;
            Native.PostMessage(window, down ? Native.WM_KEYDOWN : Native.WM_KEYUP, (IntPtr)vk, (IntPtr)unchecked((int)l));
        }
    }
}
