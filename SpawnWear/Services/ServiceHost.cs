using System.Diagnostics;
using SpawnWear.AppContracts;
using SpawnWear.Drivers.Power;
using SpawnWear.Drivers.Rtc;
using SpawnWear.Drivers.Wifi;

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

        public ServiceHost(Axp2101Driver axp, Pcf85063Driver rtc, WifiService wifi)
        {
            _power = new PowerServiceImpl(axp);
            _rtc = new RtcServiceImpl(rtc);
            _wifi = new WifiServiceImpl(wifi);
            _logger = new DebugLogger();
        }

        public IPowerService GetPower() => _power;
        public IRtcService GetRtc() => _rtc;
        public IWifiService GetWifi() => _wifi;
        public ILogger GetLogger() => _logger;
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
    internal class RtcServiceImpl : IRtcService
    {
        readonly Pcf85063Driver _rtc;
        bool _isValid;
        int _year, _month, _day, _hour, _minute, _second, _weekday;

        public RtcServiceImpl(Pcf85063Driver rtc) { _rtc = rtc; Refresh(); }

        void Refresh()
        {
            if (_rtc == null) { _isValid = false; return; }
            try
            {
                if (_rtc.TryRead(out var t))
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
