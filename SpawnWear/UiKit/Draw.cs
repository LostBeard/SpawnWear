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
    }
}
