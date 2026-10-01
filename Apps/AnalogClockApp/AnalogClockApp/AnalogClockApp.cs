using SpawnWear.AppContracts;
using System.Drawing;

namespace AnalogClockApp
{
    /// <summary>
    /// SpawnWear analog watch face: a 60-mark dial (bars at the hours, dots at the minutes) with
    /// rounded hour + minute hands, a sweeping accent second hand, the brand above center and the date
    /// below it, on an AMOLED black background. Time is read live from the PCF85063 RTC.
    ///
    /// Faces are ordinary apps: leave the watch on this app and it is your watch face (the digital
    /// face is the Clock app).
    ///
    /// Drawing: the dial marks are painted only when the face is shown; each second the dial's inner
    /// disc (everything inside the marks) is cleared and the text + hands redrawn, then only that
    /// square is flushed. Hand lengths stay inside the cleared disc so they never leave trails on the
    /// marks. Nothing is drawn above StatusBarHeight.
    /// </summary>
    public class AnalogClockApp : ISpawnApp
    {
        // Properties, not static readonly fields: static fields in an app assembly crash the
        // firmware's Assembly.Load (see AppTemplate's MyApp.cs).
        static Color Background => Color.Black;
        static Color MarkColor => Color.FromArgb(200, 200, 210);
        static Color MinorColor => Color.FromArgb(90, 90, 100);
        static Color HandColor => Color.White;
        static Color SecondColor => Color.FromArgb(255, 112, 67); // deep orange accent
        static Color TextColor => Color.FromArgb(150, 150, 162);

        // sin(6 deg * i) * 1000 for i = 0..15 (a quarter turn in 60ths); Sin/Cos below unfold it.
        // A switch, not System.Math (the watch's mscorlib has no trig) and NOT a static array: static
        // fields and array initializers (a hidden static data field) in an app assembly make the
        // firmware's Assembly.Load fail and then panic - see AppTemplate's MyApp.cs.
        static int SinQ(int i)
        {
            switch (i)
            {
                case 0: return 0;
                case 1: return 105;
                case 2: return 208;
                case 3: return 309;
                case 4: return 407;
                case 5: return 500;
                case 6: return 588;
                case 7: return 669;
                case 8: return 743;
                case 9: return 809;
                case 10: return 866;
                case 11: return 914;
                case 12: return 951;
                case 13: return 978;
                case 14: return 995;
                default: return 1000;
            }
        }

        IServiceHost _services;
        bool _dirty = true;
        int _lastSecond = -1;
        int _cx, _cy, _r, _inner;

        public string Name => "ANALOG";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _dirty = true;
            return true;
        }

        public void OnResume(IDisplayBuffer fb) { _dirty = true; Tick(fb); }
        public void OnPause() { }
        public void OnDestroy() { _services = null; }
        public bool OnTap(int x, int y) { return true; } // a watch face has nothing to tap; consume it

        public void Tick(IDisplayBuffer fb)
        {
            var rtc = _services != null ? _services.GetRtc() : null;
            int ss = rtc != null ? rtc.Second : 0;
            if (_dirty)
            {
                RenderDial(fb);
                RenderInner(fb, rtc, false);
                fb.Flush();
                _dirty = false;
            }
            else if (ss != _lastSecond)
            {
                RenderInner(fb, rtc, true);
            }
        }

        void RenderDial(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int top = fb.StatusBarHeight;
            fb.FillRectangle(0, top, w, fb.PanelHeight - top, Background);
            _cx = w / 2;
            _cy = top + (fb.PanelHeight - top) / 2;
            int rw = w / 2 - 20, rh = (fb.PanelHeight - top) / 2 - 20;
            _r = rw < rh ? rw : rh;
            _inner = _r - 28; // the per-second clear radius: just inside the longest mark

            for (int p = 0; p < 60; p++)
            {
                if (p % 5 == 0)
                {
                    // Hour bars; the quarter hours are longer.
                    int len = p % 15 == 0 ? 26 : 18;
                    Line(fb, p, _r - len, _r, 5, MarkColor);
                }
                else
                {
                    fb.FillCircle(_cx + Sin(p) * (_r - 5) / 1000, _cy - Cos(p) * (_r - 5) / 1000, 2, MinorColor);
                }
            }
        }

        void RenderInner(IDisplayBuffer fb, IRtcService rtc, bool flush)
        {
            int hh = rtc != null ? rtc.Hour : 0;
            int mm = rtc != null ? rtc.Minute : 0;
            int ss = rtc != null ? rtc.Second : 0;

            fb.FillCircle(_cx, _cy, _inner, Background);

            int th = fb.TextHeight(TextStyle.Small);
            const string Brand = "SpawnWear";
            fb.DrawText(Brand, _cx - fb.MeasureText(Brand, TextStyle.Small) / 2, _cy - _r / 2 - th / 2, TextStyle.Small, TextColor);
            string date = Weekday(rtc) + " " + (rtc != null ? rtc.Day : 1).ToString();
            fb.DrawText(date, _cx - fb.MeasureText(date, TextStyle.Small) / 2, _cy + _r / 2 - th / 2, TextStyle.Small, TextColor);

            // Hour hand advances one 60th every 12 minutes; rounded tips are discs at the line ends.
            int hourPos = (hh % 12) * 5 + mm / 12;
            Hand(fb, hourPos, 0, (_inner * 62) / 100, 10, HandColor);
            Hand(fb, mm, 0, (_inner * 90) / 100, 6, HandColor);
            // Second hand with a short counterweight tail.
            Line(fb, ss, -24, _inner - 6, 2, SecondColor);
            fb.FillCircle(_cx, _cy, 9, HandColor);
            fb.FillCircle(_cx, _cy, 6, SecondColor);
            fb.FillCircle(_cx, _cy, 2, Background);

            if (flush) fb.Flush(_cx - _inner, _cy - _inner, 2 * _inner + 1, 2 * _inner + 1);
            _lastSecond = ss;
        }

        // A radial line at dial position p (0..59, 0 = 12 o'clock) from radius r0 to r1 (r0 < 0 = a tail).
        void Line(IDisplayBuffer fb, int p, int r0, int r1, int thickness, Color color)
        {
            fb.DrawLine(_cx + Sin(p) * r0 / 1000, _cy - Cos(p) * r0 / 1000,
                        _cx + Sin(p) * r1 / 1000, _cy - Cos(p) * r1 / 1000, thickness, color);
        }

        void Hand(IDisplayBuffer fb, int p, int r0, int r1, int thickness, Color color)
        {
            Line(fb, p, r0, r1, thickness, color);
            fb.FillCircle(_cx + Sin(p) * r1 / 1000, _cy - Cos(p) * r1 / 1000, thickness / 2, color);
        }

        static int Sin(int p)
        {
            p %= 60;
            if (p < 0) p += 60;
            if (p <= 15) return SinQ(p);
            if (p <= 30) return SinQ(30 - p);
            if (p <= 45) return -SinQ(p - 30);
            return -SinQ(60 - p);
        }

        static int Cos(int p) { return Sin(p + 15); }

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
    }
}
