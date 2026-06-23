using nanoFramework.UI;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Rtc;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Android-style title bar that lives in the top 32 px of every screen.
    /// Shows HH:MM on the left, a row of small status icons on the right
    /// (USB-plug when VBUS is in, BLE dot when advertising, battery cap +
    /// percent number).
    ///
    /// Power model: caller invokes <see cref="Render"/> each frame; the
    /// bar internally caches its last-rendered state and only does a partial
    /// flush when something actually changed (minute roll, battery percent,
    /// vbus plug state). On full-screen repaints (after wake / screen
    /// switch) caller passes <c>force = true</c> to skip the cache.
    ///
    /// Reserves the top 32 px so screens should render their content from
    /// y >= <see cref="ReservedHeight"/>.
    /// </summary>
    public class StatusBar
    {
        public const int ReservedHeight = 64;

        // Glyph scale for the time text (4 = 20x28 px chars). Sized so the
        // bar is comfortably readable at arm's length on the 410x502 panel.
        const int TimeScale = 4;
        const int IconBoxSize = 36;
        const int IconBoxGap = 12;
        // The Waveshare ESP32-S3-Touch-AMOLED-2.06 case bezel rounds the
        // display corners with a sizable R - per the vendor mechanical
        // drawing the visible AMOLED area is 33.09 x 40.51 mm inside a
        // 42.00 x 50.80 mm case shell, so roughly 4 mm = ~50 px of inset
        // along each edge. Drawing in the corner-clipped zones leaves
        // status icons cut in half. Keep the bar's content inside a
        // 50-px-from-each-edge safe area.
        const int CornerSafeInset = 50;

        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly Axp2101Driver _axp;
        readonly Pcf85063Driver _rtc;
        // Optional - main loop sets this so the BLE icon mirrors the radio state.
        // We don't query the BluetoothLEServer directly to keep StatusBar driver-free.
        bool _bleAdvertising = false;
        // WiFi state: -1 = unknown, 0 = disconnected, 1..4 = signal-bar count
        // (1 = weakest, 4 = strongest). Set by the main loop based on whatever
        // signal-strength source is convenient.
        int _wifiBars = -1;
        // Companion link state: true when the WebRTC/hub link to the Companion app is up. Set by the
        // main loop from WebRtcTransportService. Always shown (green up / dim gray down).
        bool _companionConnected = false;

        // Cached last-rendered values for change detection.
        int _lastHour = -1;
        int _lastMinute = -1;
        int _lastBatteryPercent = int.MinValue;
        int _lastVbusIn = -1; // 0/1; -1 = not yet read
        int _lastBleAdvertising = -1;
        int _lastWifiBars = int.MinValue;
        int _lastCompanionConnected = -1; // 0/1; -1 = not yet read

        public StatusBar(Bitmap fb, int panelWidth, Axp2101Driver axp, Pcf85063Driver rtc)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _axp = axp;
            _rtc = rtc;
        }

        public void SetBleAdvertising(bool on) => _bleAdvertising = on;

        /// <summary>
        /// Sets the WiFi indicator state. -1 hides the icon entirely (radio
        /// not initialized), 0 shows a "no signal" outline, 1-4 shows that
        /// many filled bars in a 4-bar staircase.
        /// </summary>
        public void SetWifiBars(int bars)
        {
            if (bars < -1) bars = -1;
            if (bars > 4) bars = 4;
            _wifiBars = bars;
        }

        /// <summary>Sets the Companion-link indicator. true = the WebRTC/hub link to the Companion app
        /// is up (green), false = not connected (dim gray). Always shown.</summary>
        public void SetCompanionConnected(bool on) => _companionConnected = on;

        /// <summary>
        /// Renders the bar. <paramref name="force"/> bypasses the change-detection
        /// cache - use after a screen-level full repaint where the bar pixels
        /// need to be redrawn from scratch (the screen's <c>fb.Clear()</c> wiped
        /// them first).
        /// </summary>
        public void Render(bool force = false)
        {
            int hour = _lastHour;
            int minute = _lastMinute;
            if (_rtc != null)
            {
                if (_rtc.TryRead(out var t))
                {
                    hour = t.Hour;
                    minute = t.Minute;
                }
                else
                {
                    long elapsedSec = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    hour = (int)((elapsedSec / 3600) % 24);
                    minute = (int)((elapsedSec / 60) % 60);
                }
            }

            int pct = -1;
            int vbus = 0;
            if (_axp != null)
            {
                try { pct = _axp.ReadBatteryPercent(); } catch { pct = -1; }
                try { vbus = _axp.IsVbusPresent() ? 1 : 0; } catch { vbus = 0; }
            }

            int bleVal = _bleAdvertising ? 1 : 0;

            bool changed =
                force
                || hour != _lastHour
                || minute != _lastMinute
                || pct != _lastBatteryPercent
                || vbus != _lastVbusIn
                || bleVal != _lastBleAdvertising
                || _wifiBars != _lastWifiBars
                || (_companionConnected ? 1 : 0) != _lastCompanionConnected;
            if (!changed) return;

            // Clear the bar region.
            _fb.FillRectangle(0, 0, _panelWidth, ReservedHeight, Color.Black);

            // Time on the left, kept inside the corner-rounding safe area.
            string timeStr = TwoDigit(hour) + ":" + TwoDigit(minute);
            int timeX = CornerSafeInset;
            int timeY = (ReservedHeight - SmallFont.GlyphHeight * TimeScale) / 2;
            SmallFont.DrawString(_fb, timeStr, timeX, timeY, TimeScale, Color.White);

            // Icons on the right, drawn right-to-left so we don't have to
            // measure their composite width up front. Right edge also
            // respects the corner-rounding safe area.
            int iconY = (ReservedHeight - IconBoxSize) / 2;
            int cursor = _panelWidth - CornerSafeInset - IconBoxSize;

            // Battery percent text + battery icon. Render even when pct is
            // negative so the layout doesn't jump - draw a hollow battery
            // outline with no fill.
            DrawBatteryIcon(cursor, iconY, IconBoxSize, pct);
            cursor -= IconBoxGap + IconBoxSize;

            // BLE icon: only render when BLE is actually advertising. Otherwise
            // the slot collapses so we don't show a stale "BLE off" glyph.
            if (bleVal == 1)
            {
                DrawBleIcon(cursor, iconY, IconBoxSize);
                cursor -= IconBoxGap + IconBoxSize;
            }

            // WiFi icon: 4-bar staircase. Only render when bars >= 0 (>= 0
            // means we have a radio state to display; -1 collapses the slot).
            if (_wifiBars >= 0)
            {
                DrawWifiIcon(cursor, iconY, IconBoxSize, _wifiBars);
                cursor -= IconBoxGap + IconBoxSize;
            }

            // Companion link icon: ALWAYS shown so the link state is always visible.
            // Green = WebRTC link to the Companion app is up; dim gray = down.
            DrawCompanionIcon(cursor, iconY, IconBoxSize, _companionConnected);
            cursor -= IconBoxGap + IconBoxSize;

            // USB plug indicator - filled square when vbus in, no draw when not.
            if (vbus == 1)
            {
                DrawUsbIcon(cursor, iconY, IconBoxSize);
            }

            // Bottom-edge separator line so the title bar is visually distinct
            // from the screen content below.
            _fb.FillRectangle(0, ReservedHeight - 2, _panelWidth, 2, Color.White);

            // Partial flush of the bar region only - even/odd alignment is
            // applied automatically by the firmware Bitmap.Flush handler.
            _fb.Flush(0, 0, _panelWidth, ReservedHeight);

            _lastHour = hour;
            _lastMinute = minute;
            _lastBatteryPercent = pct;
            _lastVbusIn = vbus;
            _lastBleAdvertising = bleVal;
            _lastWifiBars = _wifiBars;
            _lastCompanionConnected = _companionConnected ? 1 : 0;
        }

        // ----- Icons -----

        // Companion link: two nodes (watch + companion) joined by a bar. Green when the WebRTC link is
        // up, dim gray when down. Drawn with rectangles to match the other driver-free icons.
        void DrawCompanionIcon(int x, int y, int size, bool connected)
        {
            Color color = connected ? Color.LimeGreen : Color.FromArgb(70, 70, 70);
            int cy = y + size / 2;
            int node = size / 3;            // node square side
            int nodeY = cy - node / 2;
            // left node (the watch)
            _fb.FillRectangle(x, nodeY, node, node, color);
            // right node (the companion)
            _fb.FillRectangle(x + size - node, nodeY, node, node, color);
            // connecting bar
            int barH = size / 6;
            _fb.FillRectangle(x + node, cy - barH / 2, size - 2 * node, barH, color);
        }

        void DrawBatteryIcon(int x, int y, int size, int pct)
        {
            // Geometry scales with the box size so doubling ReservedHeight
            // doubles the icon visually without per-icon constants.
            int bodyW = size - 8;
            int bodyH = size - 4;
            int capW = 6;
            int capH = bodyH - 12;
            int strokeT = 4;
            int bodyX = x;
            int bodyY = y + 2;
            int capX = bodyX + bodyW;
            int capY = bodyY + (bodyH - capH) / 2;

            // Outline.
            _fb.FillRectangle(bodyX, bodyY, bodyW, strokeT, Color.White);
            _fb.FillRectangle(bodyX, bodyY + bodyH - strokeT, bodyW, strokeT, Color.White);
            _fb.FillRectangle(bodyX, bodyY, strokeT, bodyH, Color.White);
            _fb.FillRectangle(bodyX + bodyW - strokeT, bodyY, strokeT, bodyH, Color.White);

            // Cap.
            _fb.FillRectangle(capX, capY, capW, capH, Color.White);

            if (pct <= 0) return;
            if (pct > 100) pct = 100;

            int fillPad = strokeT + 2;
            int maxFillW = bodyW - 2 * fillPad;
            int fillW = (maxFillW * pct) / 100;
            fillW &= ~1; // even-align for the CO5300 quirk
            int fillH = bodyH - 2 * fillPad;
            fillH &= ~1;
            if (fillW <= 0 || fillH <= 0) return;
            Color color;
            if (pct >= 50) color = Color.LimeGreen;
            else if (pct >= 20) color = Color.Yellow;
            else color = Color.Red;
            _fb.FillRectangle(bodyX + fillPad, bodyY + fillPad, fillW, fillH, color);
        }

        void DrawBleIcon(int x, int y, int size)
        {
            // Bluetooth glyph approximation using SetPixel-equivalent rectangles.
            // The classic Bluetooth mark is a stylized capital "B" that forms two
            // tilted rune-like halves crossing at the spine. We rasterize it as
            // a centered diamond skeleton: vertical spine, two top diagonals
            // descending right, two bottom diagonals ascending right, where the
            // diagonals touch the spine at quarter / three-quarter height.
            Color color = Color.DodgerBlue;
            int cx = x + size / 2;
            int top = y + 4;
            int bottom = y + size - 4;
            int mid = (top + bottom) / 2;
            int spineT = 3;
            int spineH = bottom - top;
            // Vertical spine
            _fb.FillRectangle(cx - spineT / 2, top, spineT, spineH, color);
            // Diagonals: each is 4-step staircase from spine to right edge,
            // converging at quarter-height (top diagonal) and three-quarter-height
            // (bottom diagonal). The 4 steps form a rough sloped line.
            int rightX = x + size - 4;
            int diagSteps = 5;
            int dx = (rightX - cx) / diagSteps;
            int topDy = (mid - top) / diagSteps;
            int botDy = (bottom - mid) / diagSteps;
            for (int s = 0; s <= diagSteps; s++)
            {
                // Top half: from spine (top) outward and downward to mid
                int sx = cx + s * dx;
                int sy = top + s * topDy;
                _fb.FillRectangle(sx, sy, 3, 3, color);
                // Bottom half: from spine (bottom) outward and upward to mid
                int sy2 = bottom - s * botDy;
                _fb.FillRectangle(sx, sy2 - 2, 3, 3, color);
            }
            // Inner diagonals returning from mid to spine endpoints
            for (int s = 1; s <= diagSteps; s++)
            {
                int sx = cx + (diagSteps - s) * dx;
                int sy = mid - (diagSteps - s) * topDy;
                _fb.FillRectangle(sx, sy, 3, 3, color);
                int sy2 = mid + (diagSteps - s) * botDy;
                _fb.FillRectangle(sx, sy2 - 2, 3, 3, color);
            }
        }

        void DrawWifiIcon(int x, int y, int size, int bars)
        {
            // 4-bar staircase, each bar wider than the next, all bottom-aligned.
            // Filled bars are white; empty bars are dim gray. Matches the
            // Android signal-strength glyph at a 36-px box size.
            int gap = 3;
            int barW = (size - 5 * gap) / 4;
            int baseY = y + size - 4;
            for (int i = 0; i < 4; i++)
            {
                int barH = ((i + 1) * (size - 8)) / 4;
                int barX = x + gap + i * (barW + gap);
                int barY = baseY - barH;
                Color c = i < bars ? Color.White : Color.FromArgb(70, 70, 70);
                _fb.FillRectangle(barX, barY, barW, barH, c);
            }
        }

        void DrawUsbIcon(int x, int y, int size)
        {
            int cx = x + size / 2;
            int cy = y + 4;
            Color color = Color.LimeGreen;
            int strokeT = 4;
            // Vertical stem + two staggered crossbars.
            _fb.FillRectangle(cx - strokeT / 2, cy, strokeT, size - 8, color);
            _fb.FillRectangle(cx - 6, cy + size / 4, 12, strokeT, color);
            _fb.FillRectangle(cx - 10, cy + size / 2, 20, strokeT, color);
        }

        static string TwoDigit(int n)
        {
            if (n < 0) return "00";
            if (n >= 100) n = 99;
            return ((char)('0' + n / 10)).ToString() + ((char)('0' + n % 10)).ToString();
        }
    }
}
