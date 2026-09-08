using SpawnWear.AppContracts;
using System;
using System.Drawing;

namespace ClockApp
{
    /// <summary>
    /// SpawnWear reference clock-face app: a digital watch face showing the
    /// 12-hour time, seconds + AM/PM, and the weekday / date, read live from
    /// the PCF85063 RTC via IServiceHost.GetRtc().
    ///
    /// This is the template a dev copies to make their own clock face - it is
    /// a plain ISpawnApp with nothing clock-special baked into the firmware.
    /// Leave the watch on this app (the watch stays on the last screen) and it
    /// is your watch face; swipe back to the launcher to pick another app.
    ///
    /// Rendering uses the firmware's 5x7 SmallFont via IDisplayBuffer.DrawString
    /// (glyph height = 7 * scale); MeasureString gives the scaled width so the
    /// rows center horizontally regardless of scale.
    /// </summary>
    public class ClockApp : ISpawnApp
    {
        const int GlyphH = 7; // firmware SmallFont cell height (unscaled)

        IServiceHost _services;
        int _lastSecond = -1;
        bool _dirty = true;

        public string Name => "CLOCK";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _dirty = true;
            return true;
        }

        public void OnResume(IDisplayBuffer fb) { _dirty = true; _lastSecond = -1; Render(fb); }
        public void OnPause() { }
        public void OnDestroy() { _services = null; }

        public void Tick(IDisplayBuffer fb)
        {
            // Repaint once per second (or when first shown). Cheap - just reads the RTC.
            var rtc = _services != null ? _services.GetRtc() : null;
            int s = rtc != null ? rtc.Second : 0;
            if (_dirty || s != _lastSecond) Render(fb);
        }

        public bool OnTap(int x, int y) { return true; } // a watch face has nothing to tap; consume it

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int h = fb.PanelHeight;
            int top = fb.StatusBarHeight;
            int bottom = h - fb.PageIndicatorHeight;

            var rtc = _services != null ? _services.GetRtc() : null;
            int hh = rtc != null ? rtc.Hour : 0;
            int mm = rtc != null ? rtc.Minute : 0;
            int ss = rtc != null ? rtc.Second : 0;
            _lastSecond = ss;

            fb.Clear(Color.FromArgb(6, 8, 16)); // near-black navy

            // 12-hour clock. No leading zero on the hour (watch convention); 0/12 -> 12.
            bool pm = hh >= 12;
            int h12 = hh % 12; if (h12 == 0) h12 = 12;
            string hm = h12.ToString() + ":" + Pad2(mm);

            // Big time - pick the largest scale that fits the width with a margin, then center.
            int hmScale = 12;
            int hmW = fb.MeasureString(hm, hmScale);
            while (hmW > w - 40 && hmScale > 4) { hmScale--; hmW = fb.MeasureString(hm, hmScale); }
            int hmH = GlyphH * hmScale;

            // Stack: time (big), then seconds+AM/PM, then date - centered as a group in the safe band.
            int subScale = 3;
            int dateScale = 3;
            int subH = GlyphH * subScale;
            int dateH = GlyphH * dateScale;
            int gap1 = 22;
            int gap2 = 18;
            int blockH = hmH + gap1 + subH + gap2 + dateH;
            int blockTop = top + ((bottom - top) - blockH) / 2;

            fb.DrawString(hm, (w - hmW) / 2, blockTop, hmScale, Color.White);

            string sub = Pad2(ss) + "  " + (pm ? "PM" : "AM");
            int subW = fb.MeasureString(sub, subScale);
            fb.DrawString(sub, (w - subW) / 2, blockTop + hmH + gap1, subScale, Color.FromArgb(110, 160, 230));

            string date = Weekday(rtc) + "   " + Month(rtc) + " " + (rtc != null ? rtc.Day : 1).ToString();
            int dateW = fb.MeasureString(date, dateScale);
            fb.DrawString(date, (w - dateW) / 2, blockTop + hmH + gap1 + subH + gap2, dateScale, Color.FromArgb(150, 150, 165));

            fb.Flush();
            _dirty = false;
        }

        static string Pad2(int n) { return n < 10 ? "0" + n.ToString() : n.ToString(); }

        static string Weekday(IRtcService rtc)
        {
            if (rtc == null) return "";
            switch (rtc.Weekday)
            {
                case 0: return "SUN";
                case 1: return "MON";
                case 2: return "TUE";
                case 3: return "WED";
                case 4: return "THU";
                case 5: return "FRI";
                case 6: return "SAT";
                default: return "";
            }
        }

        static string Month(IRtcService rtc)
        {
            if (rtc == null) return "";
            switch (rtc.Month)
            {
                case 1: return "JAN";
                case 2: return "FEB";
                case 3: return "MAR";
                case 4: return "APR";
                case 5: return "MAY";
                case 6: return "JUN";
                case 7: return "JUL";
                case 8: return "AUG";
                case 9: return "SEP";
                case 10: return "OCT";
                case 11: return "NOV";
                case 12: return "DEC";
                default: return "";
            }
        }
    }
}
