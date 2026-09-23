using System;
using System.Collections.Generic;

namespace AutoShaper
{
    // Block types exactly as the game numbers them (oSolid.blocktype / CONTROL_n).
    enum Shape { None = 0, TriangleUp = 1, TriangleDown = 2, SquareUp = 3, SquareDown = 4 }

    enum LineState { NoGame, Dead, Health1, Health2, Health3 }

    struct DetectedShape
    {
        public Shape Type;
        public double Center;   // room x of the shape's centre
        public int Width, Height;
        public override string ToString() { return Type + "@" + Center.ToString("0") + " (" + Width + "x" + Height + ")"; }
    }

    sealed class Frame
    {
        public LineState Line;
        public int LineCoverage;             // flat baseline columns that are lit
        public double R, G, B;               // average baseline colour
        public readonly List<DetectedShape> Shapes = new List<DetectedShape>();

        public int Health
        {
            get
            {
                switch (Line) { case LineState.Health3: return 3; case LineState.Health2: return 2; case LineState.Health1: return 1; case LineState.Dead: return 0; }
                return -1;
            }
        }

        public bool InRun { get { return Line == LineState.Health1 || Line == LineState.Health2 || Line == LineState.Health3; } }
    }

    // Reads the game's 480x270 room from a captured band of the window.
    // Geometry comes from the game data: oPlayer at (80,136), shapes are 32x32 bumps
    // drawn on the baseline y=136, above it for "up" types and below it for "down" types.
    static class Vision
    {
        public const int RoomW = 480, RoomH = 270;
        public const int BandTop = 100, BandBottom = 173;      // room rows captured
        public const int Baseline = 136;
        public const int ScanFromX = 90;
        const int Rows = BandBottom - BandTop;
        const int LitThreshold = 40;

        public static bool IsUp(Shape s) { return s == Shape.TriangleUp || s == Shape.SquareUp; }

        // pixels: 32bpp BGRA top-down buffer holding physical rows [bandY0, bandY0+h) of the client area.
        // scale/offX/offY map room coordinates to client pixels.
        public static unsafe Frame Analyze(byte* pixels, int stride, int w, int h, int bandY0, double scale, double offX, double offY)
        {
            var lit = new bool[Rows, RoomW];
            var baseR = new double[RoomW]; var baseG = new double[RoomW]; var baseB = new double[RoomW]; var baseN = new int[RoomW];

            for (int py = 0; py < h; py++)
            {
                int roomY = (int)Math.Floor(((py + bandY0) - offY) / scale);
                int r = roomY - BandTop;
                if (r < 0 || r >= Rows) continue;
                bool baseRow = roomY >= Baseline - 2 && roomY <= Baseline + 2;
                byte* row = pixels + py * stride;
                for (int px = 0; px < w; px++)
                {
                    byte* p = row + px * 4;
                    int b = p[0], g = p[1], rr = p[2];
                    int m = rr > g ? (rr > b ? rr : b) : (g > b ? g : b);
                    if (m <= LitThreshold) continue;
                    int roomX = (int)Math.Floor((px - offX) / scale);
                    if (roomX < 0 || roomX >= RoomW) continue;
                    lit[r, roomX] = true;
                    if (baseRow) { baseR[roomX] += rr; baseG[roomX] += g; baseB[roomX] += b; baseN[roomX]++; }
                }
            }
            return Classify(lit, baseR, baseG, baseB, baseN);
        }

        static Frame Classify(bool[,] lit, double[] baseR, double[] baseG, double[] baseB, int[] baseN)
        {
            var f = new Frame();
            var upH = new int[RoomW]; var downH = new int[RoomW];
            for (int x = ScanFromX; x < RoomW; x++)
            {
                for (int y = BandTop + 1; y <= Baseline - 4; y++)
                    if (lit[y - BandTop, x]) { upH[x] = Baseline - y; break; }
                for (int y = BandBottom - 1; y >= Baseline + 4; y--)
                    if (lit[y - BandTop, x]) { downH[x] = y - Baseline; break; }
            }

            // Where the wave line runs along the baseline. A real shape replaces the line over its width
            // (the line goes up and over it), while debris such as explosion particles floats over an
            // unbroken line, so this tells the two apart.
            var lineUnder = new bool[RoomW];
            for (int x = 0; x < RoomW; x++)
                for (int y = Baseline - 2; y <= Baseline + 2 && !lineUnder[x]; y++)
                    if (lit[y - BandTop, x]) lineUnder[x] = true;

            var shapeCols = new bool[RoomW];
            FindShapes(upH, true, f, shapeCols, lineUnder);
            FindShapes(downH, false, f, shapeCols, lineUnder);
            f.Shapes.Sort((a, b) => a.Center.CompareTo(b.Center));

            // Baseline colour and continuity, measured away from bumps (the flat wave line).
            double R = 0, G = 0, B = 0; int flat = 0, covered = 0;
            for (int x = 100; x < RoomW - 2; x++)
            {
                bool nearShape = false;
                for (int k = -3; k <= 3 && !nearShape; k++) { int xx = x + k; if (xx >= 0 && xx < RoomW && shapeCols[xx]) nearShape = true; }
                if (nearShape) continue;
                flat++;
                if (baseN[x] > 0) { covered++; R += baseR[x] / baseN[x]; G += baseG[x] / baseN[x]; B += baseB[x] / baseN[x]; }
            }
            f.LineCoverage = covered;
            if (covered < 40 || covered < flat * 0.5) { f.Line = LineState.NoGame; return f; }
            R /= covered; G /= covered; B /= covered;
            f.R = R; f.G = G; f.B = B;
            f.Line = ClassifyColour(R, G, B);
            return f;
        }

        // oGame colours: health 3 = c_lime (0,255,0), 2 = (255,255,0), 1 = (255,160,64), dead = (255,0,0).
        public static LineState ClassifyColour(double r, double g, double b)
        {
            double top = Math.Max(r, Math.Max(g, b));
            if (top < 30) return LineState.NoGame;
            if (b > 0.6 * top) return LineState.NoGame;          // white/blue: menu text, not the wave line
            if (g >= 0.5 * top && r < 0.5 * g) return LineState.Health3;
            if (r < 0.5 * top) return LineState.NoGame;
            double ratio = g / r;
            if (ratio >= 0.82) return LineState.Health2;
            if (ratio >= 0.38) return LineState.Health1;
            return LineState.Dead;
        }

        static void FindShapes(int[] heights, bool up, Frame f, bool[] shapeCols, bool[] lineUnder)
        {
            int x = ScanFromX;
            while (x < RoomW)
            {
                if (heights[x] < 5) { x++; continue; }
                int start = x, end = x, gap = 0;
                for (int k = x + 1; k < RoomW; k++)
                {
                    if (heights[k] >= 5) { end = k; gap = 0; }
                    else if (++gap > 1) break;
                }
                x = end + 1;
                for (int k = start; k <= end; k++) shapeCols[k] = true;

                int width = end - start + 1, maxH = 0;
                for (int k = start; k <= end; k++) maxH = Math.Max(maxH, heights[k]);
                if (maxH < 26 || width < 20 || width > 48 || end >= RoomW - 1) continue; // partial, shrinking (destroyed) or debris
                int inner = 0, covered = 0;
                for (int k = start + 4; k <= end - 4; k++) { inner++; if (lineUnder[k]) covered++; }
                if (inner > 0 && covered > inner / 2) continue;                        // line intact underneath: debris, not a shape
                int tall = 0;
                for (int k = start; k <= end; k++) if (heights[k] >= maxH * 0.75) tall++;
                bool square = tall >= width * 0.6;

                var s = new DetectedShape();
                s.Type = up ? (square ? Shape.SquareUp : Shape.TriangleUp) : (square ? Shape.SquareDown : Shape.TriangleDown);
                s.Center = (start + end) / 2.0;
                s.Width = width;
                s.Height = maxH;
                f.Shapes.Add(s);
            }
        }
    }
}
