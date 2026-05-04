using nanoFramework.UI;
using SpawnWear.AppContracts;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// "About" screen - read-only system info page. Designed as the canonical
    /// recovery surface that lives in the core firmware so the user always
    /// has visibility into watch state even if the SD card is removed,
    /// corrupted, or unmounted.
    ///
    /// Renders a vertical stack of LABEL: VALUE rows in SmallFont 2x scale:
    ///   BUILD:   2026-05-05
    ///   WIFI:    192.168.1.171
    ///   SSID:    SDN2
    ///   BAT:     087% 4123mV
    ///   USB:     IN / OUT
    ///   RTC:     2026-05-05 14:23   (or "INVALID" if OS flag set)
    ///   HEAP:    NNNN KB
    ///   UPTIME:  HH:MM:SS
    ///
    /// First-pass implementation does a full repaint on every visible Tick;
    /// future polish can add per-row cached values + partial flush like
    /// StatsScreen does. The full-screen flush takes ~25 ms; at 1 Hz it's
    /// imperceptible and keeps this code short.
    /// </summary>
    public class AboutScreen : IScreen
    {
        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        readonly IServiceHost _services;
        readonly DateTime _bootTime;

        // Compile-time build identity. Bumping these is a one-line edit on
        // every redeploy; eventually we'll inject the value at build time
        // via a generated constant or a manifest file.
        const string BuildDate = "2026-05-05";

        const int LabelScale = 2;
        const int RowGap = 6;
        const int MarginX = 28;

        int _pageDotIndex = -1;
        int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        bool _needsRepaint = true;

        public AboutScreen(Bitmap framebuffer, int panelWidth, int panelHeight, IServiceHost services)
        {
            _fb = framebuffer;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _services = services;
            _bootTime = DateTime.UtcNow;
        }

        public void Invalidate() { _needsRepaint = true; }
        public void OnResume() => Invalidate();
        public void OnPause() { }
        public bool OnTap(int x, int y) => false; // navigator handles long-press = home

        public void Tick()
        {
            if (!_needsRepaint)
            {
                // Idle redraw cadence - refresh once per second so live values
                // (battery percent, RTC time, uptime) update.
                _needsRepaint = true;
            }

            int contentTop = _statusBar != null ? StatusBar.ReservedHeight : 0;
            int rowH = SmallFont.GlyphHeight * LabelScale;
            int rowStride = rowH + RowGap;

            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            // Lay out from top of content area; leave 60 px at the bottom for
            // page dots.
            int y = contentTop + 16;

            var wifi = _services != null ? _services.GetWifi() : null;
            var power = _services != null ? _services.GetPower() : null;

            DrawRow(MarginX, y, "BUILD",  BuildDate);
            y += rowStride;
            DrawRow(MarginX, y, "WIFI",   wifi != null ? wifi.IpAddress : "");
            y += rowStride;
            DrawRow(MarginX, y, "SSID",   wifi != null ? wifi.ConnectedSsid : "");
            y += rowStride;
            DrawRow(MarginX, y, "BAT",    FormatBattery());
            y += rowStride;
            DrawRow(MarginX, y, "USB",    (power != null && power.IsVbusPresent) ? "IN" : "OUT");
            y += rowStride;
            DrawRow(MarginX, y, "RTC",    FormatRtc());
            y += rowStride;
            DrawRow(MarginX, y, "HEAP",   FormatHeap());
            y += rowStride;
            DrawRow(MarginX, y, "UPTIME", FormatUptime());

            if (_pageDotCount > 1)
            {
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            }

            _fb.Flush();
            _statusBar?.Render(force: true);
            _needsRepaint = false;
        }

        void DrawRow(int x, int y, string label, string value)
        {
            // Label in dim gray, value in white. Two columns: label left, value
            // right of a fixed indent so they line up across rows.
            const int LabelColW = 110;
            SmallFont.DrawString(_fb, label, x, y, LabelScale, Color.FromArgb(170, 170, 170));
            SmallFont.DrawString(_fb, value == null ? "" : value, x + LabelColW, y, LabelScale, Color.White);
        }

        string FormatBattery()
        {
            var p = _services != null ? _services.GetPower() : null;
            if (p == null) return "---";
            int pct = p.BatteryPercent;
            int mv = p.BatteryMillivolts;
            string pctStr = pct < 0 ? "---" : ThreeDigit(pct) + "%";
            string mvStr = mv < 0 ? "---" : mv.ToString() + "MV";
            return pctStr + " " + mvStr;
        }

        string FormatRtc()
        {
            var r = _services != null ? _services.GetRtc() : null;
            if (r == null || !r.IsValid) return "INVALID";
            // YYYY-MM-DD HH:MM
            return r.Year.ToString() + "-" +
                TwoDigit(r.Month) + "-" + TwoDigit(r.Day) + " " +
                TwoDigit(r.Hour) + ":" + TwoDigit(r.Minute);
        }

        string FormatHeap()
        {
            // nanoFramework.Runtime.Native.GC has Run(false) returning bytes free.
            // Format as KB rounded down.
            try
            {
                uint freeBytes = nanoFramework.Runtime.Native.GC.Run(false);
                return (freeBytes / 1024).ToString() + "KB";
            }
            catch
            {
                return "?";
            }
        }

        string FormatUptime()
        {
            long elapsedSec = (DateTime.UtcNow.Ticks - _bootTime.Ticks) / TimeSpan.TicksPerSecond;
            int h = (int)(elapsedSec / 3600);
            int m = (int)((elapsedSec / 60) % 60);
            int s = (int)(elapsedSec % 60);
            return TwoDigit(h) + ":" + TwoDigit(m) + ":" + TwoDigit(s);
        }

        static string TwoDigit(int n)
        {
            if (n < 0) n = 0;
            if (n >= 100) return n.ToString();
            return ((char)('0' + n / 10)).ToString() + ((char)('0' + n % 10)).ToString();
        }

        static string ThreeDigit(int n)
        {
            if (n < 0) n = 0;
            if (n >= 1000) return n.ToString();
            return ((char)('0' + (n / 100) % 10)).ToString() +
                   ((char)('0' + (n / 10) % 10)).ToString() +
                   ((char)('0' + n % 10)).ToString();
        }
    }
}
