// Updates the centre eye housing with the same pixel-art metal finish as the W, then writes the
// 256px preview and multi-size Windows icon. The 64px source grid lives in assets/autoshaper-source.png.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class MakeIcon
{
    static readonly Rectangle Eye = new Rectangle(28, 28, 8, 8);
    static readonly Color Light = Color.FromArgb(214, 216, 222);
    static readonly Color Mid = Color.FromArgb(152, 156, 164);
    static readonly Color Dark = Color.FromArgb(98, 101, 110);
    static readonly Color Rivet = Color.FromArgb(62, 64, 72);
    static readonly Color EyeRed = Color.FromArgb(255, 36, 28);
    static readonly Color EyeDim = Color.FromArgb(150, 0, 0);
    static readonly Color EyeHot = Color.FromArgb(255, 196, 180);
    static readonly int[] Sizes = { 256, 128, 64, 48, 32, 24, 16 };

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: MakeIcon <64px source.png> <output directory>"); return 2; }
        string outputDirectory = args[1];
        Directory.CreateDirectory(outputDirectory);
        using (var source = new Bitmap(args[0]))
        using (var edited = Edit(source))
        {
            var pngs = new List<byte[]>();
            foreach (int size in Sizes)
            {
                using (var icon = Resize(edited, size))
                {
                    if (size == 256) icon.Save(Path.Combine(outputDirectory, "autoshaper.png"), ImageFormat.Png);
                    using (var stream = new MemoryStream())
                    {
                        icon.Save(stream, ImageFormat.Png);
                        pngs.Add(stream.ToArray());
                    }
                }
            }
            WriteIco(Path.Combine(outputDirectory, "autoshaper.ico"), Sizes, pngs);
        }
        Console.WriteLine("wrote AutoShaper icon assets to " + Path.GetFullPath(outputDirectory));
        return 0;
    }

    static Bitmap Edit(Bitmap source)
    {
        if (source.Width != 64 || source.Height != 64) throw new ArgumentException("source icon must be 64x64 pixels");
        var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(source, 0, 0);

        // Replace the black eye housing with a bevelled metal plate matching the W.
        for (int y = Eye.Top + 1; y < Eye.Bottom - 1; y++)
            for (int x = Eye.Left + 1; x < Eye.Right - 1; x++)
            {
                bool top = y == Eye.Top + 1, left = x == Eye.Left + 1;
                bool bottom = y == Eye.Bottom - 2, right = x == Eye.Right - 2;
                bmp.SetPixel(x, y, top || left ? Light : bottom || right ? Dark : Mid);
            }

        // Two tiny fasteners make the housing read as robot plating around the red eye.
        bmp.SetPixel(Eye.Left + 1, Eye.Top + 1, Rivet);
        bmp.SetPixel(Eye.Right - 2, Eye.Bottom - 2, Rivet);

        // Keep the original-size red eye, with its black corner pixels replaced by dark steel.
        for (int y = 30; y <= 33; y++)
            for (int x = 30; x <= 33; x++)
            {
                bool corner = (x == 30 || x == 33) && (y == 30 || y == 33);
                bool rim = x == 30 || x == 33 || y == 30 || y == 33;
                bmp.SetPixel(x, y, corner ? Dark : rim ? EyeDim : EyeRed);
            }
        bmp.SetPixel(31, 31, EyeHot);
        return bmp;
    }

    static Bitmap Resize(Bitmap source, int size)
    {
        var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(resized))
        {
            bool enlarge = size >= source.Width;
            g.InterpolationMode = enlarge ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = enlarge ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, size, size);
        }
        return resized;
    }

    static void WriteIco(string path, int[] sizes, List<byte[]> images)
    {
        using (var file = File.Create(path))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(images[i].Length);
                writer.Write(offset);
                offset += images[i].Length;
            }
            foreach (var image in images) writer.Write(image);
        }
    }
}
