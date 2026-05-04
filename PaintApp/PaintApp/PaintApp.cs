using SpawnWear.AppContracts;
using System.Drawing;

namespace PaintApp
{
    /// <summary>
    /// SpawnWear demo app: tap-to-paint colored dots. Each tap drops a
    /// 12-px filled square at the tap location, cycling through a palette
    /// on each subsequent tap so the canvas builds up a colorful pattern.
    ///
    /// Demonstrates ISpawnApp.OnTap with non-trivial side effects: the
    /// app maintains paint state across taps, partial-flushes only the
    /// dirty rectangle, and avoids the status-bar / page-indicator zones
    /// the firmware reserves.
    /// </summary>
    public class PaintApp : ISpawnApp
    {
        IServiceHost _services;
        bool _firstFrame = true;
        int _paletteIndex;
        int _lastX, _lastY;
        Color _lastColor;
        bool _hasPendingFlush;

        // 8-color palette cycled through on each tap.
        static readonly Color[] Palette = new[]
        {
            Color.FromArgb(0xFF, 0x55, 0x55),
            Color.FromArgb(0xFF, 0xAA, 0x33),
            Color.FromArgb(0xFF, 0xEE, 0x22),
            Color.FromArgb(0x88, 0xEE, 0x22),
            Color.FromArgb(0x22, 0xCC, 0xAA),
            Color.FromArgb(0x55, 0x99, 0xFF),
            Color.FromArgb(0xCC, 0x66, 0xFF),
            Color.FromArgb(0xFF, 0x99, 0xCC),
        };

        public string Name => "PAINT";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _firstFrame = true;
            _paletteIndex = 0;
            return true;
        }

        public void OnResume(IDisplayBuffer fb)
        {
            _firstFrame = true;
            Render(fb);
        }

        public void OnPause() { }
        public void OnDestroy() { _services = null; }

        public void Tick(IDisplayBuffer fb)
        {
            if (_firstFrame)
            {
                Render(fb);
            }
            else if (_hasPendingFlush)
            {
                // Partial flush of the dot we just painted.
                FlushDotArea(fb, _lastX, _lastY);
                _hasPendingFlush = false;
            }
        }

        public bool OnTap(int x, int y)
        {
            // Bail if the tap was in a system-reserved zone (status bar or
            // page indicator).
            int statusBarH = 64;
            int pageIndicatorH = 60;
            if (y < statusBarH) return true;
            // We don't have direct access to PanelHeight here without fb;
            // store the tap and let Tick handle painting + flushing.
            _lastX = x;
            _lastY = y;
            _lastColor = Palette[_paletteIndex];
            _paletteIndex = (_paletteIndex + 1) % Palette.Length;
            _hasPendingFlush = true;
            return true;
        }

        // -- Rendering ---------------------------------------------------

        const int DotSize = 16;

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int h = fb.PanelHeight;
            int top = fb.StatusBarHeight;
            int bottom = h - fb.PageIndicatorHeight;

            // Black canvas.
            fb.Clear(Color.Black);

            // Title at the top (dim).
            string title = "TAP TO PAINT";
            int titleScale = 2;
            int titleW = fb.MeasureString(title, titleScale);
            fb.DrawString(title, (w - titleW) / 2, top + 14, titleScale, Color.FromArgb(120, 120, 140));

            // Footer hint.
            string hint = "LONG PRESS = HOME";
            int hintScale = 2;
            int hintW = fb.MeasureString(hint, hintScale);
            fb.DrawString(hint, (w - hintW) / 2, bottom - 30, hintScale, Color.FromArgb(80, 80, 100));

            fb.Flush();
            _firstFrame = false;
        }

        void FlushDotArea(IDisplayBuffer fb, int cx, int cy)
        {
            int x = cx - DotSize / 2;
            int y = cy - DotSize / 2;
            // Bail if the dot would land in a system-reserved zone.
            if (y < fb.StatusBarHeight) y = fb.StatusBarHeight;
            if (y + DotSize > fb.PanelHeight - fb.PageIndicatorHeight)
                y = fb.PanelHeight - fb.PageIndicatorHeight - DotSize;
            if (x < 0) x = 0;
            if (x + DotSize > fb.PanelWidth) x = fb.PanelWidth - DotSize;

            fb.FillRectangle(x, y, DotSize, DotSize, _lastColor);
            fb.Flush(x, y, DotSize, DotSize);
        }
    }
}
