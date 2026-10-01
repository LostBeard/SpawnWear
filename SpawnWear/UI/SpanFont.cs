using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Renders a 1bpp .tinyfnt that is too big for the native NativeText engine (two static slots of
    /// 7 KB, both used by the UI faces) - e.g. the ~120 px watch-face clock digits. The font is parsed
    /// in managed code ONCE into per-glyph rectangles: each row's horizontal ink runs, with a run merged
    /// into the rectangle above it when the previous row had the same run (straight strokes collapse to
    /// one rectangle). Drawing is then one native FillRectangle per rectangle, ~100-200 per digit.
    ///
    /// Single character range only (what tools/fonts/genfont.cs writes). Characters outside the range
    /// are skipped. <see cref="Draw"/>'s y is the top of the INK box (the font's empty line-spacing rows
    /// above and below the glyphs are trimmed), so <see cref="Height"/> is the visible digit height.
    /// </summary>
    public class SpanFont
    {
        const string ClockFontPath = "D:\\spawnsans-clock.tinyfnt";
        static SpanFont _clock;
        static bool _clockTried;
        static readonly object _clockLock = new object();

        /// <summary>The watch-face clock digits ('0'-'9', ':'), or null when the SD font is missing or
        /// unreadable. Parsed on first use; a second thread asking mid-parse waits for it instead of
        /// seeing "tried, no font" and falling back to the 5x7 font.</summary>
        public static SpanFont Clock
        {
            get
            {
                lock (_clockLock)
                {
                    if (!_clockTried)
                    {
                        long t0 = System.DateTime.UtcNow.Ticks;
                        try { _clock = new SpanFont(System.IO.File.ReadAllBytes(ClockFontPath)); }
                        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine("[SpanFont] clock font: " + ex.Message); }
                        _clockTried = true;
                        System.Diagnostics.Debug.WriteLine("[SpanFont] clock font " + (_clock != null ? "loaded" : "MISSING") + " in " + (System.DateTime.UtcNow.Ticks - t0) / System.TimeSpan.TicksPerMillisecond + " ms");
                    }
                    return _clock;
                }
            }
        }

        readonly int _first;
        readonly int[] _advance;
        readonly short[][] _rects; // per glyph: x, y, w, h quads (x from the glyph's left, y = atlas row)
        int _inkTop, _inkBottom;

        /// <summary>Visible ink height (all glyphs' union), in pixels.</summary>
        public int Height { get { return _inkBottom - _inkTop; } }

        public SpanFont(byte[] f)
        {
            int ranges = Rd16(f, 16);
            int chars = Rd16(f, 18);
            int atlasW = Rd32(f, 24);
            int atlasH = Rd32(f, 28);
            int rangesP = 36;
            _first = Rd16(f, rangesP + 4);
            int rangeOffset = Rd32(f, rangesP + 8);
            int charsP = rangesP + (ranges + 1) * 12;
            int atlasP = charsP + (chars + 1) * 4;
            int wiw = (atlasW + 31) / 32;

            _advance = new int[chars];
            _rects = new short[chars][];
            _inkTop = atlasH;
            _inkBottom = 0;
            short[] buf = new short[4 * 1024];
            // Rectangles still growing downward from the previous row: index into buf (quad start).
            int[] open = new int[64];
            int[] nextOpen = new int[64];

            for (int g = 0; g < chars; g++)
            {
                int ax0 = rangeOffset + Rd16(f, charsP + g * 4);
                int w = Rd16(f, charsP + (g + 1) * 4) - Rd16(f, charsP + g * 4);
                if (w < 0) w = 0;
                _advance[g] = w;
                int n = 0, nOpen = 0;
                for (int y = 0; y < atlasH; y++)
                {
                    int rowP = atlasP + y * wiw * 4;
                    int nNext = 0;
                    int wordIdx = -1, word = 0, start = -1;
                    for (int x = 0; x <= w; x++)
                    {
                        bool on = false;
                        if (x < w)
                        {
                            int ax = ax0 + x;
                            int wi = ax >> 5;
                            if (wi != wordIdx)
                            {
                                // One atlas read per 32 px; an empty word with no run open skips ahead.
                                wordIdx = wi;
                                word = Rd32(f, rowP + wi * 4);
                                if (word == 0 && start < 0) { x += 31 - (ax & 31); continue; }
                            }
                            on = ((word >> (ax & 31)) & 1) != 0;
                        }
                        if (on) { if (start < 0) start = x; continue; }
                        if (start < 0) continue;
                        int len = x - start;
                        if (y < _inkTop) _inkTop = y;
                        if (y + 1 > _inkBottom) _inkBottom = y + 1;
                        // Extend the rectangle above if the previous row had exactly this run.
                        int hit = -1;
                        for (int k = 0; k < nOpen; k++)
                        {
                            int q = open[k];
                            if (buf[q] == start && buf[q + 2] == len && buf[q + 1] + buf[q + 3] == y) { hit = q; break; }
                        }
                        if (hit < 0)
                        {
                            if (n + 4 > buf.Length)
                            {
                                short[] bigger = new short[buf.Length * 2];
                                System.Array.Copy(buf, bigger, n);
                                buf = bigger;
                            }
                            hit = n;
                            buf[n] = (short)start; buf[n + 1] = (short)y; buf[n + 2] = (short)len; buf[n + 3] = 0;
                            n += 4;
                        }
                        buf[hit + 3]++;
                        if (nNext < nextOpen.Length) nextOpen[nNext++] = hit;
                        start = -1;
                    }
                    int[] t = open; open = nextOpen; nextOpen = t; nOpen = nNext;
                }
                short[] rects = new short[n];
                System.Array.Copy(buf, rects, n);
                _rects[g] = rects;
            }
        }

        /// <summary>Pixel width of <paramref name="text"/> (characters outside the font add nothing).</summary>
        public int Measure(string text)
        {
            int total = 0;
            for (int i = 0; i < text.Length; i++)
            {
                int g = text[i] - _first;
                if (g >= 0 && g < _advance.Length) total += _advance[g];
            }
            return total;
        }

        /// <summary>Draws <paramref name="text"/> with the top of the ink box at <paramref name="y"/>.</summary>
        public void Draw(Bitmap fb, string text, int x, int y, Color color)
        {
            int top = y - _inkTop;
            for (int i = 0; i < text.Length; i++)
            {
                int g = text[i] - _first;
                if (g < 0 || g >= _advance.Length) continue;
                short[] r = _rects[g];
                for (int k = 0; k < r.Length; k += 4)
                    fb.FillRectangle(x + r[k], top + r[k + 1], r[k + 2], r[k + 3], color);
                x += _advance[g];
            }
        }

        static int Rd16(byte[] d, int p) { return d[p] | (d[p + 1] << 8); }
        static int Rd32(byte[] d, int p) { return d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24); }
    }
}
