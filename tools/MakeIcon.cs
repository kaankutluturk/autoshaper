// Generates assets\autoshaper.ico from WAVESHAPER's own 64px pixel-art icon. The edit is done on the
// icon's own pixel grid: background, glow and the red neon outline are untouched; the inside of the
// "W" becomes flat three-tone metal with seams and rivets, and the centre block becomes a red eye.
// The result is scaled up with nearest-neighbour so it stays pixel art at every size.
// Input: the game's 64px icon image (tools\make-icon.ps1 extracts it). Build/run: tools\make-icon.ps1
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    // The W on the 64x64 grid. Bar and step share one interior; the eye block has its own outline.
    static readonly Rectangle[] Letter =
    {
        new Rectangle(12, 12, 8, 40), new Rectangle(20, 36, 8, 8),
        new Rectangle(36, 36, 8, 8), new Rectangle(44, 12, 8, 40),
    };
    static readonly Rectangle Eye = new Rectangle(28, 28, 8, 8);

    static readonly Color Light = Color.FromArgb(214, 216, 222), Mid = Color.FromArgb(152, 156, 164),
        Dark = Color.FromArgb(98, 101, 110), Rivet = Color.FromArgb(62, 64, 72),
        Housing = Color.FromArgb(36, 36, 42), EyeRed = Color.FromArgb(255, 36, 28), EyeDim = Color.FromArgb(150, 0, 0), EyeHot = Color.FromArgb(255, 196, 180);

    static int Main(string[] args)
    {
        string source = args[0], outDir = args[1];
        Directory.CreateDirectory(outDir);
        using (var original = new Bitmap(source))
        using (var edited = Edit(original))
        {
            var sizes = new[] { 256, 128, 64, 48, 32, 24, 16 };
            var pngs = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Resize(edited, s))
                {
                    if (s == 256) bmp.Save(Path.Combine(outDir, "autoshaper.png"), ImageFormat.Png);
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
                }
            WriteIco(Path.Combine(outDir, "autoshaper.ico"), sizes, pngs);
        }
        Console.WriteLine("wrote " + Path.Combine(outDir, "autoshaper.ico"));
        return 0;
    }

    static bool InLetter(int x, int y) { foreach (var r in Letter) if (r.Contains(x, y)) return true; return false; }
    static bool Inside(int x, int y) { return InLetter(x, y) && InLetter(x - 1, y) && InLetter(x + 1, y) && InLetter(x, y - 1) && InLetter(x, y + 1); }

    static Bitmap Edit(Bitmap original)
    {
        var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(original, 0, 0);

        // Letter interior: mid fill, light on the upper-left inner edge, dark on the lower-right.
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                if (!Inside(x, y)) continue;
                bool top = !Inside(x, y - 1), left = !Inside(x - 1, y), bottom = !Inside(x, y + 1), right = !Inside(x + 1, y);
                bmp.SetPixel(x, y, top || left ? Light : bottom || right ? Dark : Mid);
            }

        // Panel seams across both bars: a dark groove with a lit edge under it.
        foreach (int x0 in new[] { 14, 46 })
            foreach (int y in new[] { 23, 30 })
                for (int x = x0; x < x0 + 5; x++) { bmp.SetPixel(x, y, Dark); bmp.SetPixel(x, y + 1, Light); }

        // Joints where the steps meet the bars.
        for (int y = 38; y <= 42; y++) { bmp.SetPixel(20, y, Dark); bmp.SetPixel(43, y, Dark); }

        // Rivets near the bar ends and on the steps.
        foreach (var p in new[] { new Point(14, 14), new Point(17, 14), new Point(14, 49), new Point(17, 49),
                                  new Point(46, 14), new Point(49, 14), new Point(46, 49), new Point(49, 49),
                                  new Point(24, 40), new Point(39, 40) })
            bmp.SetPixel(p.X, p.Y, Rivet);

        // Eye: dark housing inside the centre block's neon outline, a red pupil with a hot pixel.
        for (int y = Eye.Top + 1; y < Eye.Bottom - 1; y++)
            for (int x = Eye.Left + 1; x < Eye.Right - 1; x++)
                bmp.SetPixel(x, y, Housing);
        for (int y = 30; y <= 33; y++)
            for (int x = 30; x <= 33; x++)
                bmp.SetPixel(x, y, (x == 30 || x == 33) && (y == 30 || y == 33) ? Housing : (x == 30 || x == 33 || y == 30 || y == 33) ? EyeDim : EyeRed);
        bmp.SetPixel(31, 31, EyeHot);
        return bmp;
    }

    // Nearest-neighbour when enlarging keeps the pixel grid; smaller sizes are downsampled smoothly.
    static Bitmap Resize(Bitmap src, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            bool enlarge = size >= src.Width;
            g.InterpolationMode = enlarge ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = enlarge ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, size, size);
        }
        return bmp;
    }

    static void WriteIco(string path, int[] sizes, List<byte[]> pngs)
    {
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
                w.Write(pngs[i].Length); w.Write(offset); offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
        }
    }
}
