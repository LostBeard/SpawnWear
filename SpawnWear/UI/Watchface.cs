using nanoFramework.UI;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Minimal watch face for the SpawnWear V1 demo. Draws an HH:MM:SS readout
    /// of the device uptime in the center of the panel against a black AMOLED
    /// background.
    ///
    /// Power model: full repaint only on the FIRST tick or after an explicit
    /// invalidate (e.g. wake-from-sleep). Subsequent ticks ONLY redraw the
    /// digits region and partial-flush that rectangle - typically ~25 KB pushed
    /// per second versus 411 KB for a full-screen Flush.
    ///
    /// Time source for V1 is uptime via <c>Environment.TickCount</c>. Phase 3
    /// will swap this for a proper PCF85063 RTC reading once that driver lands.
    /// </summary>
    public class Watchface
    {
        private readonly Bitmap _fb;
        private readonly int _panelWidth;
        private readonly int _panelHeight;

        // Glyph geometry. Tuned for the 410x502 panel: digits ~64 px tall,
        // 36 px wide with 8-px stroke. Total HH:MM:SS line ~336 px wide,
        // centered horizontally on the 410-wide panel.
        private const int DigitWidth = 36;
        private const int DigitHeight = 64;
        private const int ColonWidth = 16;
        private const int Spacing = 4;
        private const int Thickness = 8;

        // Cached digit bounding rectangle. Computed on first paint so partial
        // flushes can target the EXACT pixels that change.
        private int _digitsX;
        private int _digitsY;
        private int _digitsWidth;
        private int _digitsHeight;

        // Last rendered values, used to skip redraw if the readout hasn't moved.
        private int _lastH = -1;
        private int _lastM = -1;
        private int _lastS = -1;

        // True until the first full repaint completes; ensures the background
        // gets cleared and the layout is computed.
        private bool _needsFullRepaint = true;

        public Watchface(Bitmap framebuffer, int panelWidth, int panelHeight)
        {
            _fb = framebuffer;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
        }

        /// <summary>
        /// Forces the next <see cref="Tick"/> to repaint the entire panel. Call
        /// after a wake-from-sleep where panel RAM contents are not trusted.
        /// </summary>
        public void Invalidate()
        {
            _needsFullRepaint = true;
            _lastH = -1;
            _lastM = -1;
            _lastS = -1;
        }

        /// <summary>
        /// Renders the current uptime to the framebuffer and flushes the
        /// minimum region needed. Returns true if any pixels were pushed.
        /// </summary>
        public bool Tick()
        {
            // Convert uptime to HH:MM:SS. Wraps at 24h for V1.
            // DateTime.UtcNow.Ticks is wall-clock ticks (100 ns) since epoch on
            // nanoFramework; without an RTC sync it just monotonically increases
            // from boot, which is exactly what we want for an uptime readout.
            long elapsedSec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            int h = (int)((elapsedSec / 3600) % 24);
            int m = (int)((elapsedSec / 60) % 60);
            int s = (int)(elapsedSec % 60);

            if (!_needsFullRepaint && h == _lastH && m == _lastM && s == _lastS)
            {
                return false; // nothing changed, no flush needed
            }

            int totalWidth = SegmentFont.HhMmSsWidth(DigitWidth, ColonWidth, Spacing);
            _digitsX = (_panelWidth - totalWidth) / 2;
            _digitsY = (_panelHeight - DigitHeight) / 2;
            _digitsWidth = totalWidth;
            _digitsHeight = DigitHeight;

            if (_needsFullRepaint)
            {
                // Full panel clear-to-black + initial digits paint.
                _fb.Clear();
                _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);
                SegmentFont.DrawHhMmSs(
                    _fb, h, m, s,
                    _digitsX, _digitsY,
                    DigitWidth, DigitHeight,
                    ColonWidth, Spacing,
                    Thickness, Color.White);
                _fb.Flush();
                _needsFullRepaint = false;
            }
            else
            {
                // Partial repaint: clear the digits strip to black, redraw,
                // and flush ONLY that rectangle. ~25 KB at 16bpp instead of 411 KB.
                //
                // CO5300 alignment quirk (per hackaday.com/2026/04/11 comment thread by
                // the waveshare-watch-rs author + our Notes/co5300-quirks.md):
                //   * CASET / PASET window MUST round x_start / y_start DOWN to even
                //   * x_end / y_end MUST round UP to odd
                //   * minimum 2-pixel write width and height
                // None of this is in the datasheet; the chip silently snaps the address
                // window and any pixel-write whose actual landed bounds disagree with
                // what we drew leaves stale pixels at the edge. The bug surfaces as
                // "small bits of the previous digits left behind" when we flush an
                // odd-aligned window.
                int alignedX = _digitsX & ~1;
                int alignedY = _digitsY & ~1;
                int alignedRight = (_digitsX + _digitsWidth - 1) | 1;
                int alignedBottom = (_digitsY + _digitsHeight - 1) | 1;
                int alignedW = alignedRight - alignedX + 1;
                int alignedH = alignedBottom - alignedY + 1;

                _fb.FillRectangle(alignedX, alignedY, alignedW, alignedH, Color.Black);
                SegmentFont.DrawHhMmSs(
                    _fb, h, m, s,
                    _digitsX, _digitsY,
                    DigitWidth, DigitHeight,
                    ColonWidth, Spacing,
                    Thickness, Color.White);
                _fb.Flush(alignedX, alignedY, alignedW, alignedH);
            }

            _lastH = h;
            _lastM = m;
            _lastS = s;
            return true;
        }
    }
}
