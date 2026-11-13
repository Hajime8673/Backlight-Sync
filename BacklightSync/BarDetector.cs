using System.Diagnostics;

namespace BacklightSync;

/// <summary>Size of the black bars on each side, in pixels of the small capture picture.</summary>
public readonly record struct Bars(int Top, int Bottom, int Left, int Right)
{
    public static readonly Bars None = new(0, 0, 0, 0);
    public bool Any => Top > 0 || Bottom > 0 || Left > 0 || Right > 0;
}

/// <summary>
/// Finds letterbox / pillarbox bars (and combinations) so the LEDs follow the real picture
/// instead of the black around it. Designed to survive subtitles, dark scenes and fades:
///  * rows/columns count as black when almost no pixel is bright, so a logo or sparkle doesn't hide a bar
///  * subtitles inside a bar are tolerated because letterbox bars are (nearly) symmetric
///  * a lopsided result (dark lower half of a night scene) is not trusted
///  * bars are only widened after they have been stable for a while, but removed quickly
///  * a completely black frame never changes anything
/// </summary>
public sealed class BarDetector
{
    readonly int[] _cur = new int[4];       // applied: top, bottom, left, right
    readonly int[] _cand = new int[4];
    readonly long[] _candSince = new long[4];
    int[] _rowCnt = Array.Empty<int>(), _colCnt = Array.Empty<int>();

    const long GrowAfterMs = 700;   // content got smaller (bars wider): be sure
    const long ShrinkAfterMs = 120; // content got bigger (bars narrower): react fast

    public Bars Current => new(_cur[0], _cur[1], _cur[2], _cur[3]);

    public void Reset()
    {
        Array.Clear(_cur); Array.Clear(_cand); Array.Clear(_candSince);
    }

    public Bars Update(byte[] px, int w, int h, int threshold)
    {
        if (_rowCnt.Length != h) _rowCnt = new int[h];
        if (_colCnt.Length != w) _colCnt = new int[w];
        Array.Clear(_rowCnt); Array.Clear(_colCnt);

        // ---- pass 1: bright pixels per row
        for (int y = 0; y < h; y++)
        {
            int i = y * w * 4, c = 0;
            for (int x = 0; x < w; x++, i += 4)
                if (Luma(px[i + 2], px[i + 1], px[i]) > threshold) c++;
            _rowCnt[y] = c;
        }
        int minRow = Math.Max(2, w / 64);
        int t = 0; while (t < h && _rowCnt[t] <= minRow) t++;
        if (t >= h) return Current; // all black: hold what we have
        int b = 0; while (b < h && _rowCnt[h - 1 - b] <= minRow) b++;

        int minBarY = 2;
        // subtitles inside a bar: bars are symmetric, so if the other bar is bigger and a black gap
        // sits right before where its mirror image would end, treat the text as part of the bar
        if (t >= minBarY && b < t) b = Extend(b, t, i => _rowCnt[h - 1 - i], minRow);
        else if (b >= minBarY && t < b) t = Extend(t, b, i => _rowCnt[i], minRow);
        bool okY = Sanity(ref t, ref b, h);

        // ---- pass 2: columns, only inside the picture rows
        int y0 = t, y1 = h - b;
        for (int y = y0; y < y1; y++)
        {
            int i = y * w * 4;
            for (int x = 0; x < w; x++, i += 4)
                if (Luma(px[i + 2], px[i + 1], px[i]) > threshold) _colCnt[x]++;
        }
        int rows = Math.Max(1, y1 - y0);
        int minCol = Math.Max(2, rows / 64);
        int l = 0; while (l < w && _colCnt[l] <= minCol) l++;
        if (l >= w) return Current;
        int r = 0; while (r < w && _colCnt[w - 1 - r] <= minCol) r++;
        int minBarX = 2;
        if (l >= minBarX && r < l) r = Extend(r, l, i => _colCnt[w - 1 - i], minCol);
        else if (r >= minBarX && l < r) l = Extend(l, r, i => _colCnt[i], minCol);
        bool okX = Sanity(ref l, ref r, w);

        // an implausible axis (e.g. half the screen dark) keeps whatever we had before
        var m = new[] { okY ? t : _cur[0], okY ? b : _cur[1], okX ? l : _cur[2], okX ? r : _cur[3] };
        for (int k = 0; k < 4; k++) if (m[k] < 2) m[k] = 0;   // ignore hair-thin bars
        Apply(m);
        return Current;
    }

    /// <summary>
    /// 'have' rows at this side are black, the opposite bar is 'limit' rows. If a bar of the same size would end
    /// exactly where real picture starts (picture row right after a black gap), the stuff in between is subtitles.
    /// </summary>
    static int Extend(int have, int limit, Func<int, int> count, int minCount)
    {
        if (have >= limit) return have;
        return count(limit) > minCount && count(limit - 1) <= minCount ? limit : have;
    }

    /// <summary>
    /// A real letterbox is symmetric. Lopsided results are usually a dark scene, so only the smaller bar is trusted
    /// for both sides. Bars covering most of the picture are not believed at all.
    /// </summary>
    static bool Sanity(ref int a, ref int b, int dim)
    {
        int max = Math.Max(a, b), min = Math.Min(a, b);
        if (max > dim * 0.4) return false;
        if (min < max * 0.6) { a = b = min; }
        return true;
    }

    void Apply(int[] m)
    {
        long now = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        for (int k = 0; k < 4; k++)
        {
            if (m[k] == _cur[k]) { _cand[k] = m[k]; _candSince[k] = now; continue; }
            if (Math.Abs(m[k] - _cand[k]) > 1 || _candSince[k] == 0) { _cand[k] = m[k]; _candSince[k] = now; continue; }
            long need = m[k] > _cur[k] ? GrowAfterMs : ShrinkAfterMs;
            if (now - _candSince[k] >= need) { _cur[k] = m[k]; _cand[k] = m[k]; }
        }
    }

    static int Luma(byte r, byte g, byte b) => (54 * r + 183 * g + 19 * b) >> 8;
}
