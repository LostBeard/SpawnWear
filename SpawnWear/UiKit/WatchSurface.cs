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

        public void Clear(Color color)
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _w, _h, color);
        }

        public void DrawRect(int x, int y, int w, int h, Color color) => _fb.FillRectangle(x, y, w, h, color);

        public void DrawText(string text, int x, int y, int scale, Color color) =>
            SmallFont.DrawString(_fb, text, x, y, scale, color);

        public int MeasureText(string text, int scale) => SmallFont.MeasureString(text, scale);

        public int TextHeight(int scale) => SmallFont.GlyphHeight * scale;

        public void Flush(int x, int y, int w, int h) => _fb.Flush(x, y, w, h);

        // No-arg Flush pushes the WHOLE bitmap reliably (the launcher uses this for full repaints).
        // The partial Flush(0,0,w,h) was dropping the bottom rows via the CO5300 even/odd alignment.
        public void FlushAll() => _fb.Flush();
    }
}
