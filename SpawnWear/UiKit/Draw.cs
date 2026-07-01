using System.Drawing;

namespace SpawnDev.UI
{
    /// <summary>
    /// Shared drawing helpers for widgets, composed from the one primitive the surface gives us
    /// (filled rectangles). Rounded rectangles are the single biggest "embedded -> polished" jump,
    /// so they live here and every widget uses them.
    /// </summary>
    public static class Shapes
    {
        /// <summary>Filled rounded rectangle. Corners follow a true circle of the given radius,
        /// rasterized as per-row horizontal spans (cheap: ~2*radius extra fills). radius is clamped to
        /// half the smaller side; radius==0 is a plain rect; radius==min/2 is a capsule.</summary>
        public static void RoundedRect(IUiSurface s, int x, int y, int w, int h, int radius, Color color)
        {
            if (w <= 0 || h <= 0) return;
            int r = radius;
            if (r < 0) r = 0;
            if (r > w / 2) r = w / 2;
            if (r > h / 2) r = h / 2;
            if (r == 0) { s.DrawRect(x, y, w, h, color); return; }

            // straight middle band (full width)
            s.DrawRect(x, y + r, w, h - 2 * r, color);

            // top + bottom caps: for each of the r rows, inset x by the circle's horizontal offset
            for (int i = 0; i < r; i++)
            {
                int dy = r - i;                       // distance above/below the corner-arc center
                int dx = r - IntSqrt(r * r - dy * dy); // horizontal inset for this row
                int rw = w - 2 * dx;
                if (rw <= 0) continue;
                s.DrawRect(x + dx, y + i, rw, 1, color);             // top cap row
                s.DrawRect(x + dx, y + h - 1 - i, rw, 1, color);     // bottom cap row
            }
        }

        /// <summary>Integer square root (Newton). nanoFramework-safe, no System.Math dependency.</summary>
        public static int IntSqrt(int v)
        {
            if (v <= 0) return 0;
            int x = v, y = (x + 1) / 2;
            while (y < x) { x = y; y = (x + v / x) / 2; }
            return x;
        }

        /// <summary>Filled circle, rasterized as horizontal spans (same cheap approach as the rounded-rect
        /// corners). Composed only from DrawRect so it works on any IUiSurface.</summary>
        public static void FillCircle(IUiSurface s, int cx, int cy, int r, Color color)
        {
            if (r <= 0) return;
            for (int dy = -r; dy <= r; dy++)
            {
                int dx = IntSqrt(r * r - dy * dy);
                s.DrawRect(cx - dx, cy + dy, 2 * dx + 1, 1, color);
            }
        }

        /// <summary>Stroked circle (a ring): draw a filled disc then punch the hole back to
        /// <paramref name="holeColor"/> (the surface behind the ring).</summary>
        public static void Ring(IUiSurface s, int cx, int cy, int r, int thickness, Color color, Color holeColor)
        {
            if (thickness < 1) thickness = 1;
            FillCircle(s, cx, cy, r, color);
            if (r - thickness > 0) FillCircle(s, cx, cy, r - thickness, holeColor);
        }

        /// <summary>Thick line via DDA-stepped square stamps. Good enough for glyph-scale icon strokes;
        /// composed only from DrawRect.</summary>
        public static void Line(IUiSurface s, int x0, int y0, int x1, int y1, int thickness, Color color)
        {
            if (thickness < 1) thickness = 1;
            int half = thickness / 2;
            int dx = x1 - x0, dy = y1 - y0;
            int adx = dx < 0 ? -dx : dx;
            int ady = dy < 0 ? -dy : dy;
            int steps = adx > ady ? adx : ady;
            if (steps == 0) { s.DrawRect(x0 - half, y0 - half, thickness, thickness, color); return; }
            for (int i = 0; i <= steps; i++)
            {
                int x = x0 + (dx * i) / steps;
                int y = y0 + (dy * i) / steps;
                s.DrawRect(x - half, y - half, thickness, thickness, color);
            }
        }
    }

    /// <summary>The quick-settings tile icon set. Each is drawn inside an (x,y,size,size) box in
    /// <paramref name="color"/>; <paramref name="bg"/> is the tile fill behind the icon (used to punch
    /// ring holes). Built from Shapes primitives so it renders on any IUiSurface.</summary>
    public enum UiIcon { None, Wifi, Bluetooth, Companion, Http }

    public static class Icons
    {
        public static void Draw(IUiSurface s, UiIcon icon, int x, int y, int size, Color color, Color bg)
        {
            int cx = x + size / 2;
            int cy = y + size / 2;
            int th = size / 10; if (th < 3) th = 3;
            switch (icon)
            {
                case UiIcon.Wifi:
                {
                    // Four ascending signal bars, bottom-aligned.
                    int gap = size / 12; if (gap < 2) gap = 2;
                    int bw = (size - 5 * gap) / 4;
                    int baseY = y + size - gap;
                    for (int i = 0; i < 4; i++)
                    {
                        int h = ((i + 1) * (size - 2 * gap)) / 4;
                        int bx = x + gap + i * (bw + gap);
                        Shapes.RoundedRect(s, bx, baseY - h, bw, h, bw / 3, color);
                    }
                    break;
                }
                case UiIcon.Bluetooth:
                {
                    // Spine + the two right-pointing runes = a recognizable Bluetooth glyph.
                    int top = y + size / 6, bot = y + size - size / 6, mid = (top + bot) / 2;
                    int right = x + (size * 2) / 3;
                    Shapes.Line(s, cx, top, cx, bot, th, color);
                    Shapes.Line(s, cx, top, right, y + (size * 3) / 8, th, color);
                    Shapes.Line(s, right, y + (size * 3) / 8, cx, mid, th, color);
                    Shapes.Line(s, cx, bot, right, y + (size * 5) / 8, th, color);
                    Shapes.Line(s, right, y + (size * 5) / 8, cx, mid, th, color);
                    break;
                }
                case UiIcon.Companion:
                {
                    // Two nodes joined by a bar (mirrors the status-bar companion-link icon).
                    int r = size / 6;
                    int lx = x + size / 5, rx = x + (size * 4) / 5;
                    int barH = size / 8;
                    s.DrawRect(lx, cy - barH / 2, rx - lx, barH, color);
                    Shapes.FillCircle(s, lx, cy, r, color);
                    Shapes.FillCircle(s, rx, cy, r, color);
                    break;
                }
                case UiIcon.Http:
                {
                    // Globe: ring + equator/meridian + two latitudes.
                    int r = size / 2 - th;
                    Shapes.Ring(s, cx, cy, r, th, color, bg);
                    Shapes.Line(s, cx - r, cy, cx + r, cy, th, color);
                    Shapes.Line(s, cx, cy - r, cx, cy + r, th, color);
                    Shapes.Line(s, cx - (r * 3) / 4, cy - r / 2, cx + (r * 3) / 4, cy - r / 2, th, color);
                    Shapes.Line(s, cx - (r * 3) / 4, cy + r / 2, cx + (r * 3) / 4, cy + r / 2, th, color);
                    break;
                }
            }
        }
    }
}
