using SpawnWear.AppContracts;
using System;
using System.Drawing;

namespace ClockApp
{
    /// <summary>
    /// SpawnWear digital watch face (the default face): big 12-hour HH:MM in the firmware's clock
    /// digits, the date above it, seconds + AM/PM below, and the battery level at the bottom, on an
    /// AMOLED black background. Time is read live from the PCF85063 RTC via IServiceHost.GetRtc().
    ///
    /// Faces are ordinary apps: leave the watch on this app and it is your watch face (the analog
    /// face is the AnalogClock app). This is also the template a dev copies to make their own face.
    ///
    /// Drawing: the full face is repainted only when shown and when the minute changes; each second
    /// only the seconds strip is redrawn and partial-flushed. Nothing is drawn above
    /// StatusBarHeight, so the system status bar is never wiped.
    /// </summary>
    public class ClockApp : ISpawnApp
    {
        // Properties, not static readonly fields: static fields in an app assembly crash the
        // firmware's Assembly.Load (see AppTemplate's MyApp.cs).
        static Color Background => Color.Black;
        static Color TimeColor => Color.White;
        static Color DateColor => Color.FromArgb(170, 170, 182);
        static Color SecondsColor => Color.FromArgb(79, 195, 247); // light blue accent
        static Color DimColor => Color.FromArgb(120, 120, 132);

        IServiceHost _services;
        int _lastSecond = -1;
        int _lastMinute = -1;
        bool _dirty = true;
        int _subY; // top of the seconds strip, set by the full render

        public string Name => "CLOCK";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _dirty = true;
            return true;
        }

        public void OnResume(IDisplayBuffer fb) { _dirty = true; Tick(fb); }
        public void OnPause() { }
        public void OnDestroy() { _services = null; }

        public void Tick(IDisplayBuffer fb)
        {
            var rtc = _services != null ? _services.GetRtc() : null;
            int hh = rtc != null ? rtc.Hour : 0;
            int mm = rtc != null ? rtc.Minute : 0;
            int ss = rtc != null ? rtc.Second : 0;
            if (_dirty || mm != _lastMinute) RenderFull(fb, rtc, hh, mm, ss);
            else if (ss != _lastSecond) RenderSeconds(fb, hh, ss, true);
        }

        public bool OnTap(int x, int y) { return true; } // a watch face has nothing to tap; consume it

        void RenderFull(IDisplayBuffer fb, IRtcService rtc, int hh, int mm, int ss)
        {
            int w = fb.PanelWidth;
            int top = fb.StatusBarHeight;
            int bottom = fb.PanelHeight - fb.PageIndicatorHeight;
            fb.FillRectangle(0, top, w, fb.PanelHeight - top, Background);

            int h12 = hh % 12; if (h12 == 0) h12 = 12; // 12-hour, no leading zero on the hour
            string time = h12.ToString() + ":" + Pad2(mm);
            string date = Weekday(rtc) + ", " + Month(rtc) + " " + (rtc != null ? rtc.Day : 1).ToString();

            // Vertical stack centered in the band between the status bar and the page dots:
            // date, gap, TIME, gap, seconds + AM/PM.
            int dateH = fb.TextHeight(TextStyle.Small);
            int timeH = fb.TextHeight(TextStyle.Clock);
            int subH = fb.TextHeight(TextStyle.Large);
            const int Gap1 = 18, Gap2 = 20;
            int blockH = dateH + Gap1 + timeH + Gap2 + subH;
            int y = top + ((bottom - top) - blockH) / 2;

            fb.DrawText(date, (w - fb.MeasureText(date, TextStyle.Small)) / 2, y, TextStyle.Small, DateColor);
            y += dateH + Gap1;
            fb.DrawText(time, (w - fb.MeasureText(time, TextStyle.Clock)) / 2, y, TextStyle.Clock, TimeColor);
            y += timeH + Gap2;
            _subY = y;
            RenderSeconds(fb, hh, ss, false);
            RenderBattery(fb, bottom);

            fb.Flush();
            _lastMinute = mm;
            _dirty = false;
        }

        // "27  PM", seconds in the accent colour, centered as one group.
        void RenderSeconds(IDisplayBuffer fb, int hh, int ss, bool flush)
        {
            int w = fb.PanelWidth;
            int h = fb.TextHeight(TextStyle.Large);
            string sec = Pad2(ss);
            string ampm = "  " + (hh >= 12 ? "PM" : "AM");
            int secW = fb.MeasureText(sec, TextStyle.Large);
            int x = (w - secW - fb.MeasureText(ampm, TextStyle.Large)) / 2;
            fb.FillRectangle(0, _subY, w, h, Background);
            fb.DrawText(sec, x, _subY, TextStyle.Large, SecondsColor);
            fb.DrawText(ampm, x + secW, _subY, TextStyle.Large, DimColor);
            if (flush) fb.Flush(0, _subY, w, h);
            _lastSecond = ss;
        }

        // Battery: a small pill gauge + percentage, centered just above the page-dot band.
        void RenderBattery(IDisplayBuffer fb, int bottom)
        {
            var power = _services != null ? _services.GetPower() : null;
            int pct = power != null ? power.BatteryPercent : -1;
            if (pct < 0) return;
            if (pct > 100) pct = 100;
            string label = pct.ToString() + "%";
            int textW = fb.MeasureText(label, TextStyle.Small);
            int textH = fb.TextHeight(TextStyle.Small);
            const int PillW = 28, PillH = 14, Gap = 8;
            int x = (fb.PanelWidth - PillW - Gap - textW) / 2;
            int y = bottom - textH - 6;
            int pillY = y + (textH - PillH) / 2;
            Color fill = pct >= 50 ? Color.FromArgb(102, 187, 106) : pct >= 20 ? Color.FromArgb(255, 202, 40) : Color.FromArgb(239, 83, 80);
            fb.FillRoundRectangle(x, pillY, PillW, PillH, 5, Color.FromArgb(70, 70, 80));
            int fw = ((PillW - 4) * pct) / 100;
            if (fw > 0) fb.FillRoundRectangle(x + 2, pillY + 2, fw, PillH - 4, 3, fill);
            fb.DrawText(label, x + PillW + Gap, y, TextStyle.Small, DateColor);
        }

        static string Pad2(int n) { return n < 10 ? "0" + n.ToString() : n.ToString(); }

        static string Weekday(IRtcService rtc)
        {
            if (rtc == null) return "";
            switch (rtc.Weekday)
            {
                case 0: return "Sun";
                case 1: return "Mon";
                case 2: return "Tue";
                case 3: return "Wed";
                case 4: return "Thu";
                case 5: return "Fri";
                case 6: return "Sat";
                default: return "";
            }
        }

        static string Month(IRtcService rtc)
        {
            if (rtc == null) return "";
            switch (rtc.Month)
            {
                case 1: return "Jan";
                case 2: return "Feb";
                case 3: return "Mar";
                case 4: return "Apr";
                case 5: return "May";
                case 6: return "Jun";
                case 7: return "Jul";
                case 8: return "Aug";
                case 9: return "Sep";
                case 10: return "Oct";
                case 11: return "Nov";
                case 12: return "Dec";
                default: return "";
            }
        }
    }
}
