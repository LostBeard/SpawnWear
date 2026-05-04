using nanoFramework.UI;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Rtc;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Minimal watch face for the SpawnWear V1 demo. Draws an HH:MM:SS readout
    /// of the device uptime in the center of the panel against a black AMOLED
    /// background, with a battery indicator bar beneath when an
    /// <see cref="Axp2101Driver"/> is supplied.
    ///
    /// Power model: full repaint only on the FIRST tick or after an explicit
    /// invalidate (e.g. wake-from-sleep). Subsequent ticks ONLY redraw the
    /// digits region and partial-flush that rectangle - typically ~25 KB pushed
    /// per second versus 411 KB for a full-screen Flush.
    ///
    /// Time source for V1 is uptime via <c>DateTime.UtcNow.Ticks</c>. Phase 3
    /// will swap this for a proper PCF85063 RTC reading once that driver lands.
    /// </summary>
    public class Watchface : IScreen
    {
        private readonly Bitmap _fb;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        private readonly Axp2101Driver _axp;
        private readonly Pcf85063Driver _rtc;
        private int _lastBatteryPercent = -2; // -2 = never read, -1 = uncalibrated, 0..100 = valid

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

        // Page-dots: the navigator owns the index/count and pushes them down
        // through the constructor. -1 = no dots rendered (single-screen mode).
        private int _pageDotIndex = -1;
        private int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }

        // Shared status bar. When set, the screen reserves
        // StatusBar.ReservedHeight px at the top and asks the bar to render
        // itself on every Tick. Null = no bar (single-screen / kiosk mode).
        private StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        // Last rendered values, used to skip redraw if the readout hasn't moved.
        private int _lastH = -1;
        private int _lastM = -1;
        private int _lastS = -1;

        // True until the first full repaint completes; ensures the background
        // gets cleared and the layout is computed.
        private bool _needsFullRepaint = true;

        // Battery indicator geometry. Centered horizontally below the clock.
        // 200 px wide x 16 px tall body + 6 px cap; total ~210 px. Plenty
        // of headroom on the 410 px panel.
        private const int BatteryBodyWidth = 200;
        private const int BatteryBodyHeight = 16;
        private const int BatteryCapWidth = 6;
        private const int BatteryCapHeight = 8;
        private const int BatteryStrokeThickness = 2;
        private const int BatteryGapBelowDigits = 30;

        // Cached battery rectangle so partial flush after a percentage change
        // can target only the bar + cap region.
        private int _batX, _batY, _batW, _batH;

        public Watchface(Bitmap framebuffer, int panelWidth, int panelHeight, Axp2101Driver axp = null, Pcf85063Driver rtc = null)
        {
            _fb = framebuffer;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _axp = axp;
            _rtc = rtc;
        }

        // IScreen ----------------------------------------------------------------

        void IScreen.Tick() { Tick(); }

        void IScreen.OnResume() { Invalidate(); }

        void IScreen.OnPause() { /* no resources to release */ }

        bool IScreen.OnTap(int x, int y) => false; // let navigator cycle to next screen

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
            _lastBatteryPercent = -2;
        }

        /// <summary>
        /// Renders the current uptime to the framebuffer and flushes the
        /// minimum region needed. Returns true if any pixels were pushed.
        /// </summary>
        public bool TickReturnsPainted()
        {
            return DoTick();
        }

        // Public Tick is the IScreen contract; ignores the painted-bool return.
        public void Tick()
        {
            DoTick();
        }

        private bool DoTick()
        {
            // Prefer the PCF85063 RTC when it's reporting valid time (oscillator
            // running, time has been set since power-up). The RTC is battery-backed
            // through the AXP2101 coin-cell pin so wall-clock time survives a
            // reboot. Falls back to uptime via DateTime.UtcNow.Ticks when the RTC
            // is missing or its OS flag is asserted.
            int h, m, s;
            if (_rtc != null && _rtc.TryRead(out var rtcTime))
            {
                h = rtcTime.Hour;
                m = rtcTime.Minute;
                s = rtcTime.Second;
            }
            else
            {
                long elapsedSec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                h = (int)((elapsedSec / 3600) % 24);
                m = (int)((elapsedSec / 60) % 60);
                s = (int)(elapsedSec % 60);
            }

            if (!_needsFullRepaint && h == _lastH && m == _lastM && s == _lastS)
            {
                return false; // nothing changed, no flush needed
            }

            // Reserve the top of the panel for the status bar when one is set.
            int contentTop = _statusBar != null ? StatusBar.ReservedHeight : 0;

            int totalWidth = SegmentFont.HhMmSsWidth(DigitWidth, ColonWidth, Spacing);
            _digitsX = (_panelWidth - totalWidth) / 2;
            _digitsY = contentTop + (_panelHeight - contentTop - DigitHeight) / 2;
            _digitsWidth = totalWidth;
            _digitsHeight = DigitHeight;

            // Battery bar bounding rect (includes cap on the right side).
            _batW = BatteryBodyWidth + BatteryCapWidth;
            _batH = BatteryBodyHeight;
            _batX = (_panelWidth - _batW) / 2;
            _batY = _digitsY + _digitsHeight + BatteryGapBelowDigits;

            int batPercent = ReadBatteryPercentSafe();
            bool batChanged = batPercent != _lastBatteryPercent;

            if (_needsFullRepaint)
            {
                // Full panel clear-to-black + initial digits paint + battery bar.
                _fb.Clear();
                _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);
                SegmentFont.DrawHhMmSs(
                    _fb, h, m, s,
                    _digitsX, _digitsY,
                    DigitWidth, DigitHeight,
                    ColonWidth, Spacing,
                    Thickness, Color.White);
                DrawBatteryBar(batPercent);
                if (_pageDotCount > 1)
                {
                    PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
                }
                _fb.Flush();
                _needsFullRepaint = false;
                _lastBatteryPercent = batPercent;
                _statusBar?.Render(force: true);
            }
            else
            {
                // Per-tick: refresh the status bar (cheap; only flushes on actual change).
                _statusBar?.Render(force: false);
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

                // Repaint the battery bar separately when the percent changes.
                // Digits and battery are far enough apart that a single combined
                // partial flush would waste bytes on the gap; two narrow flushes
                // is cheaper.
                if (batChanged)
                {
                    DrawBatteryBar(batPercent);
                    int bAlignedX = _batX & ~1;
                    int bAlignedY = _batY & ~1;
                    int bAlignedRight = (_batX + _batW - 1) | 1;
                    int bAlignedBottom = (_batY + _batH - 1) | 1;
                    _fb.Flush(bAlignedX, bAlignedY, bAlignedRight - bAlignedX + 1, bAlignedBottom - bAlignedY + 1);
                    _lastBatteryPercent = batPercent;
                }
            }

            _lastH = h;
            _lastM = m;
            _lastS = s;
            return true;
        }

        private int ReadBatteryPercentSafe()
        {
            if (_axp == null) return -1;
            try
            {
                return _axp.ReadBatteryPercent();
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Draws an outlined horizontal battery bar with a fill proportional to
        /// <paramref name="percent"/>. Outline is white. Fill color shifts:
        /// percent &gt;= 50 → green, 20..49 → yellow, &lt;20 → red. percent &lt;= 0
        /// renders an empty outline (typically meaning the AXP fuel gauge is
        /// uncalibrated or the battery is missing).
        /// </summary>
        private void DrawBatteryBar(int percent)
        {
            // Always-clear: wipe the whole bar bounding rect to black first so
            // a previous fill of a higher percent doesn't stay behind.
            _fb.FillRectangle(_batX, _batY, _batW, _batH, Color.Black);

            // Outlined body: top + bottom strokes, then left + right.
            _fb.FillRectangle(_batX, _batY, BatteryBodyWidth, BatteryStrokeThickness, Color.White);
            _fb.FillRectangle(_batX, _batY + BatteryBodyHeight - BatteryStrokeThickness, BatteryBodyWidth, BatteryStrokeThickness, Color.White);
            _fb.FillRectangle(_batX, _batY, BatteryStrokeThickness, BatteryBodyHeight, Color.White);
            _fb.FillRectangle(_batX + BatteryBodyWidth - BatteryStrokeThickness, _batY, BatteryStrokeThickness, BatteryBodyHeight, Color.White);

            // Cap on the right side.
            int capX = _batX + BatteryBodyWidth;
            int capY = _batY + (BatteryBodyHeight - BatteryCapHeight) / 2;
            _fb.FillRectangle(capX, capY, BatteryCapWidth, BatteryCapHeight, Color.White);

            if (percent <= 0) return;
            if (percent > 100) percent = 100;

            int fillPad = BatteryStrokeThickness + 1;
            int fillMaxWidth = BatteryBodyWidth - 2 * fillPad;
            int fillWidth = (fillMaxWidth * percent) / 100;
            // Even-align fill width for the CO5300 alignment quirk.
            fillWidth &= ~1;
            int fillX = _batX + fillPad;
            int fillY = _batY + fillPad;
            int fillH = BatteryBodyHeight - 2 * fillPad;
            // Even-align fillH likewise.
            fillH &= ~1;

            Color fillColor;
            if (percent >= 50) fillColor = Color.LimeGreen;
            else if (percent >= 20) fillColor = Color.Yellow;
            else fillColor = Color.Red;

            if (fillWidth > 0 && fillH > 0)
            {
                _fb.FillRectangle(fillX, fillY, fillWidth, fillH, fillColor);
            }
        }
    }
}
