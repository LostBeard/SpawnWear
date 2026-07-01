using nanoFramework.UI;
using SpawnWear.AppContracts;
using System;
using System.Drawing;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// "About" screen - read-only system info page, rebuilt on the SpawnDev.UI widget library
    /// (WidgetScreen): a UIStatusBar + UIPageDots as chrome, a column of UIKeyValue info rows for the
    /// live readouts. Being a WidgetScreen it renders in the proportional font and participates in the
    /// navigator's horizontal slide when swiping between rotation screens.
    /// </summary>
    public class AboutScreen : WidgetScreen
    {
        private readonly IServiceHost _services;
        private readonly DateTime _bootTime;
        const string BuildDate = "2026-05-05";

        private readonly UIPanel _root;
        private readonly UIKeyValue _build, _wifi, _ssid, _bat, _usb, _rtc, _heap, _uptime;

        public AboutScreen(Bitmap fb, int panelWidth, int panelHeight, IServiceHost services)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _services = services;
            _bootTime = DateTime.UtcNow;
            var t = Theme.Current;

            _root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background };

            _root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 6, Width = panelWidth, Height = 40,
                Text = "ABOUT", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            var col = new UIColumn
            {
                X = SafeArea.EdgeInset + 10, Y = StatusBar.ReservedHeight + 56,
                Width = panelWidth - 2 * (SafeArea.EdgeInset + 10), Height = 320, Spacing = 3,
            };
            _build = MakeRow("BUILD"); _wifi = MakeRow("WIFI"); _ssid = MakeRow("SSID"); _bat = MakeRow("BAT");
            _usb = MakeRow("USB"); _rtc = MakeRow("RTC"); _heap = MakeRow("HEAP"); _uptime = MakeRow("UPTIME");
            col.Add(_build); col.Add(_wifi); col.Add(_ssid); col.Add(_bat);
            col.Add(_usb); col.Add(_rtc); col.Add(_heap); col.Add(_uptime);
            _root.Add(col);

            Root = _root;
        }

        private static UIKeyValue MakeRow(string label) => new UIKeyValue { Label = label, Value = "" };

        // Chrome (status bar + page dots) is now the navigator's fixed overlay, not part of the page tree,
        // so these uniform-wiring hooks are no-ops here (the navigator owns SetChrome + the rotation index).
        public void SetPageDots(int index, int total) { }
        public void SetStatusBar(StatusBar bar) { }

        public override void OnResume() { RefreshValues(); base.OnResume(); }

        public override void Tick()
        {
            if (RefreshValues()) Invalidate(); // a readout changed -> repaint the tree (chrome redrawn on top)
            base.Tick();
        }

        // Returns true if any value string changed since the last refresh.
        private bool RefreshValues()
        {
            var wifi = _services != null ? _services.GetWifi() : null;
            var power = _services != null ? _services.GetPower() : null;
            bool changed = false;
            if (Set(_build, BuildDate)) changed = true;
            if (Set(_wifi, wifi != null ? wifi.IpAddress : "")) changed = true;
            if (Set(_ssid, wifi != null ? wifi.ConnectedSsid : "")) changed = true;
            if (Set(_bat, FormatBattery())) changed = true;
            if (Set(_usb, (power != null && power.IsVbusPresent) ? "IN" : "OUT")) changed = true;
            if (Set(_rtc, FormatRtc())) changed = true;
            if (Set(_heap, FormatHeap())) changed = true;
            if (Set(_uptime, FormatUptime())) changed = true;
            return changed;
        }

        private static bool Set(UIKeyValue row, string v) { if (row.Value == v) return false; row.Value = v; return true; }

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
            return r.Year.ToString() + "-" + TwoDigit(r.Month) + "-" + TwoDigit(r.Day) + " " + TwoDigit(r.Hour) + ":" + TwoDigit(r.Minute);
        }

        string FormatHeap()
        {
            try { return (nanoFramework.Runtime.Native.GC.Run(false) / 1024).ToString() + "KB"; }
            catch { return "?"; }
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
            return ((char)('0' + (n / 100) % 10)).ToString() + ((char)('0' + (n / 10) % 10)).ToString() + ((char)('0' + n % 10)).ToString();
        }
    }
}
