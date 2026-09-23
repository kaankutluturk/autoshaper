// Reads an executable's first icon group (the file is only read) and writes it as original.ico plus one PNG per size.
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

class IconExtract
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] static extern IntPtr LoadResource(IntPtr module, IntPtr res);
    [DllImport("kernel32.dll")] static extern IntPtr LockResource(IntPtr data);
    [DllImport("kernel32.dll")] static extern uint SizeofResource(IntPtr module, IntPtr res);
    delegate bool EnumNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
    [DllImport("kernel32.dll")] static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumNameProc cb, IntPtr param);

    static byte[] Res(IntPtr m, IntPtr name, int type)
    {
        IntPtr r = FindResource(m, name, (IntPtr)type);
        if (r == IntPtr.Zero) return null;
        var buf = new byte[SizeofResource(m, r)];
        Marshal.Copy(LockResource(LoadResource(m, r)), buf, 0, buf.Length);
        return buf;
    }

    static int Main(string[] args)
    {
        IntPtr m = LoadLibraryEx(args[0], IntPtr.Zero, 0x2 | 0x20); // datafile + image resource
        if (m == IntPtr.Zero) { Console.WriteLine("cannot open"); return 1; }
        IntPtr group = IntPtr.Zero;
        EnumResourceNames(m, (IntPtr)14, (a, b, name, d) => { group = name; return false; }, IntPtr.Zero);
        byte[] dir = Res(m, group, 14);
        int count = BitConverter.ToUInt16(dir, 4);
        var images = new byte[count][];
        for (int i = 0; i < count; i++) images[i] = Res(m, (IntPtr)BitConverter.ToUInt16(dir, 6 + i * 14 + 12), 3);
        Directory.CreateDirectory(args[1]);
        using (var fs = File.Create(Path.Combine(args[1], "original.ico")))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)count);
            int offset = 6 + 16 * count;
            for (int i = 0; i < count; i++)
            {
                int e = 6 + i * 14;
                w.Write(dir, e, 12);            // width, height, colours, reserved, planes, bit count, bytes
                w.Write(offset); offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
        for (int i = 0; i < count; i++)
        {
            int size = dir[6 + i * 14]; if (size == 0) size = 256;
            Console.WriteLine("image " + i + ": " + size + "px, " + BitConverter.ToUInt16(dir, 6 + i * 14 + 6) + "bpp, " + images[i].Length + " bytes" + (images[i][1] == 'P' ? " (png)" : ""));
            if (images[i][1] == 'P') File.WriteAllBytes(Path.Combine(args[1], "original-" + size + ".png"), images[i]);
            else using (var ico = new Icon(Path.Combine(args[1], "original.ico"), size, size)) ico.ToBitmap().Save(Path.Combine(args[1], "original-" + size + ".png"));
        }
        return 0;
    }
}
