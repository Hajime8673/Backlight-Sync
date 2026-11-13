namespace BacklightSync;

public enum Side { None, Left, Top, Right }

/// <summary>Where each LED physically sits: which side and how far along it (always counted bottom-to-top
/// for the sides and left-to-right for the top, as seen from the front of the screen).</summary>
public readonly record struct LedSlot(Side Side, int Index, int Count);

public static class Layout
{
    public static LedSlot[] Build(Settings s)
    {
        var slots = new List<LedSlot>(s.LedCount);
        void Add(Side side, int n, bool reverse)
        {
            for (int i = 0; i < n; i++) slots.Add(new LedSlot(side, reverse ? n - 1 - i : i, n));
        }

        if (s.Clockwise)
        {   // bottom-left, up the left, across the top, down the right
            Add(Side.Left, s.LeftLeds, false);
            Add(Side.Top, s.TopLeds, false);
            Add(Side.Right, s.RightLeds, true);
        }
        else
        {   // bottom-right, up the right, across the top (right to left), down the left
            Add(Side.Right, s.RightLeds, false);
            Add(Side.Top, s.TopLeds, true);
            Add(Side.Left, s.LeftLeds, true);
        }
        while (slots.Count < s.LedCount) slots.Add(new LedSlot(Side.None, 0, 1));
        return slots.Take(s.LedCount).ToArray();
    }
}

/// <summary>Averages the picture next to each edge into one colour per LED (in linear light).</summary>
public sealed class EdgeSampler
{
    static readonly float[] ToLinear = BuildLut();
    static float[] BuildLut()
    {
        var t = new float[256];
        for (int i = 0; i < 256; i++) t[i] = MathF.Pow(i / 255f, 2.2f);
        return t;
    }

    /// <summary>Output: 3 floats (linear R,G,B 0..1) per LED.</summary>
    public void Sample(byte[] px, int w, int h, Bars bars, int depthPercent, LedSlot[] slots, float[] outRgb)
    {
        // The picture rectangle without the black bars (pull in 1px: that row is a blend of picture and black).
        int x0 = bars.Left > 0 ? bars.Left + 1 : 0;
        int x1 = w - (bars.Right > 0 ? bars.Right + 1 : 0);
        int y0 = bars.Top > 0 ? bars.Top + 1 : 0;
        int y1 = h - (bars.Bottom > 0 ? bars.Bottom + 1 : 0);
        if (x1 - x0 < 8 || y1 - y0 < 8) { x0 = 0; y0 = 0; x1 = w; y1 = h; }
        int cw = x1 - x0, ch = y1 - y0;
        int dx = Math.Max(2, cw * depthPercent / 100);
        int dy = Math.Max(2, ch * depthPercent / 100);

        for (int i = 0; i < slots.Length; i++)
        {
            var s = slots[i];
            int ax0, ax1, ay0, ay1;
            switch (s.Side)
            {
                case Side.Left:
                case Side.Right:
                {
                    // index 0 is the bottom LED
                    float a = (float)s.Index / s.Count, b = (float)(s.Index + 1) / s.Count;
                    ay1 = y1 - (int)MathF.Round(a * ch);
                    ay0 = y1 - (int)MathF.Round(b * ch);
                    if (s.Side == Side.Left) { ax0 = x0; ax1 = x0 + dx; } else { ax1 = x1; ax0 = x1 - dx; }
                    break;
                }
                case Side.Top:
                {
                    float a = (float)s.Index / s.Count, b = (float)(s.Index + 1) / s.Count;
                    ax0 = x0 + (int)MathF.Round(a * cw);
                    ax1 = x0 + (int)MathF.Round(b * cw);
                    ay0 = y0; ay1 = y0 + dy;
                    break;
                }
                default:
                    // spare LEDs (more LEDs than edges): whole-picture average, handled below
                    ax0 = x0; ax1 = x1; ay0 = y0; ay1 = y1;
                    break;
            }
            if (ax1 <= ax0) ax1 = ax0 + 1;
            if (ay1 <= ay0) ay1 = ay0 + 1;
            ax0 = Math.Clamp(ax0, 0, w - 1); ax1 = Math.Clamp(ax1, 1, w);
            ay0 = Math.Clamp(ay0, 0, h - 1); ay1 = Math.Clamp(ay1, 1, h);

            float r = 0, g = 0, bl = 0; int n = 0;
            int step = s.Side == Side.None ? 3 : 1;
            for (int y = ay0; y < ay1; y += step)
            {
                int p = (y * w + ax0) * 4;
                for (int x = ax0; x < ax1; x += step, p += 4 * step)
                {
                    bl += ToLinear[px[p]]; g += ToLinear[px[p + 1]]; r += ToLinear[px[p + 2]]; n++;
                }
            }
            if (n == 0) n = 1;
            outRgb[i * 3] = r / n; outRgb[i * 3 + 1] = g / n; outRgb[i * 3 + 2] = bl / n;
        }
    }
}
