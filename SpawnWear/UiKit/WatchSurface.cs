using nanoFramework.UI;
using System.Drawing;
using SpawnWear.UI; // SmallFont (will move into the UI lib when it extracts)

namespace SpawnDev.UI
{
    /// <summary>
    /// IUiSurface backed by the watch's nanoFramework framebuffer (Bitmap) + the
    /// SmallFont bitmap font. The Blazor simulator implements the same interface
    /// over a 2D canvas, so a UIElement tree renders identically on both.
    /// </summary>
    public class WatchSurface : IUiSurface
    {
        private readonly Bitmap _fb;
        private readonly int _w;
        private readonly int _h;

        public WatchSurface(Bitmap fb, int width, int height)
        {
            _fb = fb;
            _w = width;
            _h = height;
        }

        public int Width => _w;
        public int Height => _h;

        // The UI library speaks in SmallFont "scale" units (5x7 glyphs x N). Map that to the two shared
        // proportional faces so widget screens match the rest of the UI: big face (~h36) for title-sized
        // text (scale >= 5), small face (~h24) for body/rows. If the SD fonts didn't load, every call
        // falls back to the 5x7 SmallFont at the requested scale - identical to the old behavior.
        private const int TitleScaleThreshold = 5;
        private static NativeFont PickFont(int scale) =>
            scale >= TitleScaleThreshold ? NativeFont.Shared : NativeFont.SharedSmall;

        public void Clear(Color color)
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _w, _h, color);
        }

        public void DrawRect(int x, int y, int w, int h, Color color) => _fb.FillRectangle(x, y, w, h, color);

        public void DrawText(string text, int x, int y, int scale, Color color)
        {
            NativeFont f = PickFont(scale);
            if (f != null && f.IsValid) f.Draw(_fb, text, x, y, color);
            else SmallFont.DrawString(_fb, text, x, y, scale, color);
        }

        public int MeasureText(string text, int scale)
        {
            NativeFont f = PickFont(scale);
            return (f != null && f.IsValid) ? f.Measure(text) : SmallFont.MeasureString(text, scale);
        }

        public int TextHeight(int scale)
        {
            NativeFont f = PickFont(scale);
            return (f != null && f.IsValid) ? f.Height : SmallFont.GlyphHeight * scale;
        }

        // Defensive: SetClippingRectangle is not verified on this CO5300 firmware. If it throws, no-op
        // (the scroll list may overflow, but it must NEVER crash/wedge the UI).
        public void SetClip(int x, int y, int w, int h) { try { _fb.SetClippingRectangle(x, y, w, h); } catch { } }
        public void ClearClip() { try { _fb.SetClippingRectangle(0, 0, _w, _h); } catch { } }

        public void Flush(int x, int y, int w, int h) => _fb.Flush(x, y, w, h);

        // No-arg Flush pushes the WHOLE bitmap reliably (the launcher uses this for full repaints).
        // The partial Flush(0,0,w,h) was dropping the bottom rows via the CO5300 even/odd alignment.
        public void FlushAll() => _fb.Flush();
    }
}
