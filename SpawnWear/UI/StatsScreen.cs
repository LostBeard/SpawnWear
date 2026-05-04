using nanoFramework.Runtime.Native;
using nanoFramework.UI;
using SpawnWear.Drivers.Power;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Stats / diagnostics screen. Stacks three readouts vertically:
    ///   1. Battery percent (3-digit number, e.g. "087" for 87%)
    ///   2. Battery voltage in millivolts (4-digit, e.g. "4123" for 4.123 V)
    ///   3. Uptime (HH:MM:SS in compact 7-segment glyphs)
    ///
    /// All renders use the existing <see cref="SegmentFont"/> at a smaller
    /// glyph size, so no new font assets are required. A row of small
    /// markers on the left edge of each row visually distinguishes them
    /// (one bar = battery percent, two bars = mV, three bars = uptime).
    ///
    /// Power model: same as <see cref="Watchface"/> - full repaint on first
    /// frame after Invalidate(), partial flushes thereafter, all flush
    /// rectangles even/odd-aligned for the CO5300 quirk.
    /// </summary>
    public class StatsScreen : IScreen
    {
        private readonly Bitmap _fb;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        private readonly Axp2101Driver _axp;

        // Compact glyph geometry. ~18 px digit cells, 32 px tall, 5 px stroke.
        private const int DigitWidth = 18;
        private const int DigitHeight = 32;
        private const int Spacing = 3;
        private const int Thickness = 5;

        // Row-Y coordinates (computed in Layout). Three stacked rows centered
        // vertically with gaps.
        private int _row1Y, _row2Y, _row3Y;
        private int _rowsLeftX;
        private int _rowsRightX;

        // Cached values - skip redraw when they haven't changed.
        private int _lastPct = int.MinValue;
        private int _lastMv = int.MinValue;
        private int _lastUptimeSec = -1;
        private bool _needsFullRepaint = true;
        private int _pageDotIndex = -1;
        private int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        private StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        public StatsScreen(Bitmap framebuffer, int panelWidth, int panelHeight, Axp2101Driver axp = null)
        {
            _fb = framebuffer;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _axp = axp;
        }

        public void Invalidate()
        {
            _needsFullRepaint = true;
            _lastPct = int.MinValue;
            _lastMv = int.MinValue;
            _lastUptimeSec = -1;
        }

        public void OnResume() => Invalidate();

        public void OnPause() { /* no resources */ }

        public bool OnTap(int x, int y) => false; // let navigator cycle back

        public void Tick()
        {
            // Read inputs first.
            int pct = -1;
            int mv = -1;
            if (_axp != null)
            {
                try { pct = _axp.ReadBatteryPercent(); }
                catch { pct = -1; }
                try { mv = _axp.ReadBatteryMillivolts(); }
                catch { mv = -1; }
            }
            int uptimeSec = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) % 86400);

            Layout();

            if (_needsFullRepaint)
            {
                _fb.Clear();
                _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);
                DrawRowMarkers();
                DrawPercent(pct);
                DrawMillivolts(mv);
                DrawUptime(uptimeSec);
                if (_pageDotCount > 1)
                {
                    PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
                }
                _fb.Flush();
                _statusBar?.Render(force: true);
                _needsFullRepaint = false;
                _lastPct = pct;
                _lastMv = mv;
                _lastUptimeSec = uptimeSec;
                return;
            }

            // Per-tick: refresh status bar (cheap; only flushes on change).
            _statusBar?.Render(force: false);

            // Partial repaints, only redraw rows whose content changed.
            if (pct != _lastPct)
            {
                ClearRow(_row1Y);
                DrawPercent(pct);
                FlushRowAligned(_row1Y);
                _lastPct = pct;
            }
            if (mv != _lastMv)
            {
                ClearRow(_row2Y);
                DrawMillivolts(mv);
                FlushRowAligned(_row2Y);
                _lastMv = mv;
            }
            if (uptimeSec != _lastUptimeSec)
            {
                ClearRow(_row3Y);
                DrawUptime(uptimeSec);
                FlushRowAligned(_row3Y);
                _lastUptimeSec = uptimeSec;
            }
        }

        private void Layout()
        {
            // Layout the three rows with even spacing in the area below the
            // status bar (when present) and above the page-dots row.
            int contentTop = _statusBar != null ? StatusBar.ReservedHeight : 0;
            int gap = 24;
            int blockHeight = (3 * DigitHeight) + (2 * gap);
            int top = contentTop + (_panelHeight - contentTop - blockHeight) / 2;
            _row1Y = top;
            _row2Y = top + DigitHeight + gap;
            _row3Y = top + 2 * (DigitHeight + gap);
            // Right-align the digit blocks against a common right edge so all
            // rows visually line up. The widest row is uptime (HH:MM:SS = 6
            // digits + 2 colons + 7 spacings).
            int uptimeWidth = SegmentFont.HhMmSsWidth(DigitWidth, DigitWidth - 4, Spacing);
            _rowsRightX = (_panelWidth + uptimeWidth) / 2;
            _rowsLeftX = _rowsRightX - uptimeWidth;
        }

        private void DrawRowMarkers()
        {
            // Three small vertical bars to the LEFT of each row, growing in
            // count: row1 = 1 bar, row2 = 2 bars, row3 = 3 bars. Visually
            // distinguishes rows without needing letter glyphs.
            int markerSize = 6;
            int markerSpacing = 4;
            int markerLeft = _rowsLeftX - 30;

            for (int row = 0; row < 3; row++)
            {
                int y = (row == 0) ? _row1Y : (row == 1) ? _row2Y : _row3Y;
                int yCenter = y + DigitHeight / 2 - markerSize / 2;
                for (int i = 0; i <= row; i++)
                {
                    int xMarker = markerLeft - i * (markerSize + markerSpacing);
                    _fb.FillRectangle(xMarker, yCenter, markerSize, markerSize, Color.White);
                }
            }
        }

        private void DrawPercent(int pct)
        {
            // Right-align 3 digits in the row block; shows "---" if pct < 0.
            int x = _rowsRightX - (3 * DigitWidth + 2 * Spacing);
            int y = _row1Y;
            if (pct < 0)
            {
                // Three dashes (just the middle segment of each digit cell).
                for (int i = 0; i < 3; i++)
                {
                    int dashX = x + i * (DigitWidth + Spacing);
                    _fb.FillRectangle(dashX, y + (DigitHeight - Thickness) / 2, DigitWidth, Thickness, Color.White);
                }
                return;
            }
            int hundreds = (pct / 100) % 10;
            int tens = (pct / 10) % 10;
            int ones = pct % 10;
            SegmentFont.DrawDigit(_fb, hundreds, x, y, DigitWidth, DigitHeight, Thickness, Color.White);
            SegmentFont.DrawDigit(_fb, tens, x + (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
            SegmentFont.DrawDigit(_fb, ones, x + 2 * (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
        }

        private void DrawMillivolts(int mv)
        {
            // Right-align 4 digits.
            int x = _rowsRightX - (4 * DigitWidth + 3 * Spacing);
            int y = _row2Y;
            if (mv < 0) mv = 0;
            int d3 = (mv / 1000) % 10;
            int d2 = (mv / 100) % 10;
            int d1 = (mv / 10) % 10;
            int d0 = mv % 10;
            SegmentFont.DrawDigit(_fb, d3, x + 0 * (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
            SegmentFont.DrawDigit(_fb, d2, x + 1 * (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
            SegmentFont.DrawDigit(_fb, d1, x + 2 * (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
            SegmentFont.DrawDigit(_fb, d0, x + 3 * (DigitWidth + Spacing), y, DigitWidth, DigitHeight, Thickness, Color.White);
        }

        private void DrawUptime(int uptimeSec)
        {
            int h = (uptimeSec / 3600) % 24;
            int m = (uptimeSec / 60) % 60;
            int s = uptimeSec % 60;
            // Use a slightly narrower colon for tight horizontal layout.
            int colonW = DigitWidth - 4;
            int totalWidth = SegmentFont.HhMmSsWidth(DigitWidth, colonW, Spacing);
            int x = _rowsRightX - totalWidth;
            SegmentFont.DrawHhMmSs(_fb, h, m, s, x, _row3Y,
                DigitWidth, DigitHeight, colonW, Spacing, Thickness, Color.White);
        }

        private void ClearRow(int rowY)
        {
            // Clear a strip the full row width. Use even alignment for the flush.
            int alignedX = _rowsLeftX & ~1;
            int alignedY = rowY & ~1;
            int alignedRight = (_rowsRightX) | 1;
            int alignedBottom = (rowY + DigitHeight - 1) | 1;
            _fb.FillRectangle(alignedX, alignedY, alignedRight - alignedX + 1, alignedBottom - alignedY + 1, Color.Black);
        }

        private void FlushRowAligned(int rowY)
        {
            int alignedX = _rowsLeftX & ~1;
            int alignedY = rowY & ~1;
            int alignedRight = (_rowsRightX) | 1;
            int alignedBottom = (rowY + DigitHeight - 1) | 1;
            _fb.Flush(alignedX, alignedY, alignedRight - alignedX + 1, alignedBottom - alignedY + 1);
        }
    }
}
