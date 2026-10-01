using System.Diagnostics;
using System.Drawing;
using nanoFramework.UI;
using SpawnWear.AppContracts;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Rtc;
using SpawnWear.Drivers.Wifi;
using SpawnWear.UI;

namespace SpawnWear.Services
{
    /// <summary>
    /// Concrete IServiceHost. Constructed once at boot in Program.Main(),
    /// passed to every screen / app that wants to consume system services
    /// through the AppContracts interfaces.
    ///
    /// Each accessor returns a thin shim that wraps the corresponding
    /// driver / service instance. Shims are constructed once at host
    /// construction time and reused on every accessor call - no allocations
    /// in the hot path.
    /// </summary>
    public class ServiceHost : IServiceHost
    {
        readonly IPowerService _power;
        readonly IRtcService _rtc;
        readonly IWifiService _wifi;
        readonly ILogger _logger;
        IDisplayBuffer _display;

        public ServiceHost(Axp2101Driver axp, TimeService time, WifiService wifi, ILogger logger)
        {
            _power = new PowerServiceImpl(axp);
            _rtc = new RtcServiceImpl(time);
            _wifi = new WifiServiceImpl(wifi);
            // Phase 3 LoggerService is created in Program.Main and passed in; fall back to
            // the Debug.WriteLine shim if a caller does not supply one.
            _logger = logger != null ? logger : new DebugLogger();
        }

        public void AttachDisplay(Bitmap fb, int panelWidth, int panelHeight)
        {
            _display = new DisplayBufferImpl(fb, panelWidth, panelHeight);
        }

        public IPowerService GetPower() => _power;
        public IRtcService GetRtc() => _rtc;
        public IWifiService GetWifi() => _wifi;
        public ILogger GetLogger() => _logger;
        public IDisplayBuffer GetDisplay() => _display;
    }

    /// <summary>Wraps a nanoFramework.UI.Bitmap as IDisplayBuffer for apps.
    /// Hides the native bitmap pointer so apps can't accidentally trample
    /// the firmware's framebuffer state outside the rectangle they own.</summary>
    internal class DisplayBufferImpl : IDisplayBuffer
    {
        readonly Bitmap _fb;
        readonly int _panelWidth, _panelHeight;
        public DisplayBufferImpl(Bitmap fb, int w, int h) { _fb = fb; _panelWidth = w; _panelHeight = h; }

        public int PanelWidth => _panelWidth;
        public int PanelHeight => _panelHeight;
        public int StatusBarHeight => StatusBar.ReservedHeight;
        public int PageIndicatorHeight => 60;

        public void Clear(Color background)
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, background);
        }

        public void FillRectangle(int x, int y, int w, int h, Color color)
        {
            _fb.FillRectangle(x, y, w, h, color);
        }

        public void DrawString(string text, int x, int y, int scale, Color color)
        {
            SmallFont.DrawString(_fb, text == null ? "" : text, x, y, scale, color);
        }

        public int MeasureString(string text, int scale)
        {
            return SmallFont.MeasureString(text == null ? "" : text, scale);
        }

        // TextStyle -> font. Small/Large are the native UI faces (NativeFont), Clock is the big managed
        // SpanFont; any of them missing (no SD font) falls back to the 5x7 SmallFont at a similar height.
        static int FallbackScale(TextStyle style)
        {
            return style == TextStyle.Clock ? 12 : style == TextStyle.Large ? 4 : 2;
        }

        static NativeFont Native(TextStyle style)
        {
            NativeFont f = style == TextStyle.Large ? NativeFont.Shared : style == TextStyle.Small ? NativeFont.SharedSmall : null;
            return f != null && f.IsValid ? f : null;
        }

        public void DrawText(string text, int x, int y, TextStyle style, Color color)
        {
            if (text == null || text.Length == 0) return;
            if (style == TextStyle.Clock && SpanFont.Clock != null) { SpanFont.Clock.Draw(_fb, text, x, y, color); return; }
            NativeFont f = Native(style);
            if (f != null) { f.Draw(_fb, text, x, y, color); return; }
            SmallFont.DrawString(_fb, text, x, y, FallbackScale(style), color);
        }

        public int MeasureText(string text, TextStyle style)
        {
            if (text == null) return 0;
            if (style == TextStyle.Clock && SpanFont.Clock != null) return SpanFont.Clock.Measure(text);
            NativeFont f = Native(style);
            if (f != null) return f.Measure(text);
            return SmallFont.MeasureString(text, FallbackScale(style));
        }

        public int TextHeight(TextStyle style)
        {
            if (style == TextStyle.Clock && SpanFont.Clock != null) return SpanFont.Clock.Height;
            NativeFont f = Native(style);
            if (f != null) return f.Height;
            return SmallFont.GlyphHeight * FallbackScale(style);
        }

        public void DrawLine(int x0, int y0, int x1, int y1, int thickness, Color color)
        {
            if (thickness > 3)
            {
                _fb.DrawLine(color, thickness, x0, y0, x1, y1);
                return;
            }
            // Thin lines: parallel 1 px lines offset across the minor axis. The native thick-line path at
            // 2-3 px breaks a diagonal into detached dashes (seen on the analog face's second hand); the
            // 1 px line is solid at every angle.
            int dx = x1 - x0, dy = y1 - y0;
            bool steep = (dy < 0 ? -dy : dy) > (dx < 0 ? -dx : dx);
            int t = thickness < 1 ? 1 : thickness;
            for (int i = 0; i < t; i++)
            {
                int off = i - (t - 1) / 2;
                if (steep) _fb.DrawLine(color, 1, x0 + off, y0, x1 + off, y1);
                else _fb.DrawLine(color, 1, x0, y0 + off, x1, y1 + off);
            }
        }

        public void FillCircle(int cx, int cy, int radius, Color color)
        {
            // DrawEllipse's fill is a gradient; a start == end colour makes it solid.
            _fb.DrawEllipse(color, 1, cx, cy, radius, radius, color, 0, 0, color, 0, 0);
        }

        public void DrawCircle(int cx, int cy, int radius, int thickness, Color color)
        {
            // Concentric 1 px rings (the launcher's RingCircle technique) - a thick outline stroke is
            // not reliable on this graphics build.
            for (int i = 0; i < thickness && radius - i > 0; i++)
                _fb.DrawEllipse(color, cx, cy, radius - i, radius - i);
        }

        public void FillRoundRectangle(int x, int y, int w, int h, int radius, Color color)
        {
            _fb.FillRoundRectangle(x, y, w, h, radius, radius, color);
        }

        public void Flush() { _fb.Flush(); }
        public void Flush(int x, int y, int w, int h) { _fb.Flush(x, y, w, h); }
    }

    /// <summary>Reads battery state from an Axp2101Driver. Read-failures collapse
    /// to -1 / false so callers don't have to wrap every access in try/catch.</summary>
    internal class PowerServiceImpl : IPowerService
    {
        readonly Axp2101Driver _axp;
        public PowerServiceImpl(Axp2101Driver axp) { _axp = axp; }

        public int BatteryPercent
        {
            get { try { return _axp != null ? _axp.ReadBatteryPercent() : -1; } catch { return -1; } }
        }

        public int BatteryMillivolts
        {
            get { try { return _axp != null ? _axp.ReadBatteryMillivolts() : -1; } catch { return -1; } }
        }

        public bool IsVbusPresent
        {
            get { try { return _axp != null && _axp.IsVbusPresent(); } catch { return false; } }
        }
    }

    /// <summary>Reads RTC date/time. IsValid mirrors the OS (oscillator-stop) flag
    /// from the chip; when false the H/M/S/Y/M/D values fall through to a "last
    /// known good" snapshot from the most recent successful read.</summary>
    /// <summary>App-facing clock: LOCAL time (the RTC keeps UTC; TimeService applies the user's zone).</summary>
    internal class RtcServiceImpl : IRtcService
    {
        readonly TimeService _time;
        bool _isValid;
        int _year, _month, _day, _hour, _minute, _second, _weekday;

        public RtcServiceImpl(TimeService time) { _time = time; Refresh(); }

        void Refresh()
        {
            if (_time == null) { _isValid = false; return; }
            try
            {
                if (_time.TryLocalNow(out var t))
                {
                    _isValid = true;
                    _year = t.Year; _month = t.Month; _day = t.Day;
                    _hour = t.Hour; _minute = t.Minute; _second = t.Second;
                    _weekday = t.Weekday;
                }
                else
                {
                    _isValid = false;
                }
            }
            catch { _isValid = false; }
        }

        public bool IsValid { get { Refresh(); return _isValid; } }
        public int Year { get { Refresh(); return _year; } }
        public int Month { get { Refresh(); return _month; } }
        public int Day { get { Refresh(); return _day; } }
        public int Hour { get { Refresh(); return _hour; } }
        public int Minute { get { Refresh(); return _minute; } }
        public int Second { get { Refresh(); return _second; } }
        public int Weekday { get { Refresh(); return _weekday; } }
    }

    /// <summary>Reads WiFi state from the WifiService. SSID readback isn't
    /// surfaced by nanoFramework's WifiAdapter API today, so the shim takes
    /// the configured SSID at construction time and returns it while the
    /// adapter reports IsConnected = true.</summary>
    internal class WifiServiceImpl : IWifiService
    {
        readonly WifiService _wifi;
        public WifiServiceImpl(WifiService wifi) { _wifi = wifi; }

        public bool IsConnected => _wifi != null && _wifi.IsConnected;
        public string IpAddress => _wifi != null && _wifi.IsConnected ? _wifi.IpAddress : "";
        public string ConnectedSsid
        {
            get
            {
                if (_wifi == null || !_wifi.IsConnected) return "";
                return Config.WifiCredentials.Ssid;
            }
        }
    }

    /// <summary>Routes log messages to Debug.WriteLine with a level prefix.
    /// Phase 3's Logger system service replaces this with a ring buffer +
    /// USB-CDC sink + BLE notify sink.</summary>
    internal class DebugLogger : ILogger
    {
        public void Info(string message) { Debug.WriteLine("[INFO] " + message); }
        public void Warn(string message) { Debug.WriteLine("[WARN] " + message); }
        public void Error(string message) { Debug.WriteLine("[ERROR] " + message); }
    }
}
