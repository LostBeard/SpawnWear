using nanoFramework.UI;
using SpawnWear.Drivers.Imu;
using SpawnWear.Drivers.Power;
using System.Drawing;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// Settings screen, rebuilt on the SpawnDev.UI widget library (WidgetScreen): tappable UIListRows for
    /// brightness preset, BLE/WiFi toggles, live IMU orientation, the Companion/UI-Kit/GFX-Probe sub-pages,
    /// and force-sleep. Chrome (status bar + page dots) is the navigator's fixed overlay, so swiping to/from
    /// Settings slides the content under it; opening a sub-page slides it down over Settings.
    /// </summary>
    public class SettingsScreen : WidgetScreen
    {
        public delegate void RequestSleep();
        /// <summary>Performs an on/off toggle and returns the resulting state.</summary>
        public delegate bool ToggleAction(bool desiredOn);
        /// <summary>Opens a sub-page (pushed onto the navigator) - e.g. Companion.</summary>
        public delegate void OpenPage();

        private readonly RequestSleep _requestSleep;
        private readonly Qmi8658Driver _imu;
        private readonly ToggleAction _bleToggle;
        private readonly ToggleAction _wifiToggle;
        private readonly OpenPage _openCompanion;
        private readonly OpenPage _openUiKit;
        private readonly OpenPage _openGfxProbe;
        private bool _bleOn;
        private bool _wifiOn;
        private int _motionThrottle;

        private readonly UIListRow _brightRow, _bleRow, _wifiRow, _motionRow;
        private static byte _currentBrightness = 0xFF;

        public SettingsScreen(Bitmap fb, int panelWidth, int panelHeight, RequestSleep requestSleep, Qmi8658Driver imu,
            ToggleAction bleToggle, bool bleOn, ToggleAction wifiToggle, bool wifiOn, OpenPage openCompanion,
            OpenPage openUiKit, OpenPage openGfxProbe)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _requestSleep = requestSleep;
            _imu = imu;
            _bleToggle = bleToggle;
            _bleOn = bleOn;
            _wifiToggle = wifiToggle;
            _wifiOn = wifiOn;
            _openCompanion = openCompanion;
            _openUiKit = openUiKit;
            _openGfxProbe = openGfxProbe;
            var t = Theme.Current;

            var root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background };
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 4, Width = panelWidth, Height = 38,
                Text = "SETTINGS", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            // Comfortable rows (easy touch targets) with the large font, in a SCROLLING column - the list
            // is taller than its viewport and scrolls with a vertical drag. The viewport ends above the
            // fixed page dots.
            const int rowH = 64;
            int viewTop = StatusBar.ReservedHeight + 48;
            int viewBottom = panelHeight - 46; // clear of the page-dots chrome
            var col = new UIScrollColumn
            {
                X = SafeArea.EdgeInset, Y = viewTop,
                Width = panelWidth - 2 * SafeArea.EdgeInset, Height = viewBottom - viewTop, Spacing = 8,
            };
            _brightRow = Row("BRIGHT", BrightnessLabel(_currentBrightness), ToggleBrightness, rowH);
            _bleRow = Row("BLE", _bleToggle != null ? OnOff(_bleOn) : "N/A", ToggleBle, rowH);
            _wifiRow = Row("WIFI", _wifiToggle != null ? OnOff(_wifiOn) : "N/A", ToggleWifi, rowH);
            _motionRow = Row("MOTION", _imu != null ? "----" : "N/A", null, rowH); // informational
            col.Add(_brightRow); col.Add(_bleRow); col.Add(_wifiRow); col.Add(_motionRow);
            col.Add(Row("COMPANION", ">", OpenCompanion, rowH));
            col.Add(Row("UI KIT", ">", OpenUiKit, rowH));
            col.Add(Row("GFX PROBE", ">", OpenGfxProbe, rowH));
            col.Add(Row("SLEEP", "NOW", TriggerSleep, rowH));
            root.Add(col);
            ScrollTarget = col; // vertical drag scrolls this list

            Root = root;
        }

        private static UIListRow Row(string label, string value, UIListRow.TapHandler tap, int h)
            => new UIListRow { Label = label, Value = value, Tapped = tap, Height = h, Scale = Theme.Current.TitleScale };

        // Chrome is navigator-owned; these uniform-wiring hooks are no-ops here.
        public void SetPageDots(int index, int total) { }
        public void SetStatusBar(StatusBar bar) { }

        public override void Tick()
        {
            // Live orientation from the IMU, throttled to ~1/8 ticks. Repaint only when the label changes.
            if (_imu != null && (++_motionThrottle & 0x07) == 0)
            {
                if (_imu.TryRead(out var s))
                {
                    string v = OrientationLabel(s);
                    if (_motionRow.Value != v) { _motionRow.Value = v; Invalidate(); }
                }
            }
            base.Tick();
        }

        // ----- Actions -----

        private void ToggleBrightness()
        {
            byte next;
            if (_currentBrightness == 0xFF) next = 0x80;
            else if (_currentBrightness == 0x80) next = 0x40;
            else next = 0xFF;
            _currentBrightness = next;
            DisplayControl.SetBrightness(_currentBrightness);
            _brightRow.Value = BrightnessLabel(_currentBrightness);
            Invalidate();
        }

        private void TriggerSleep() { if (_requestSleep != null) _requestSleep(); }
        private void OpenCompanion() { if (_openCompanion != null) _openCompanion(); }
        private void OpenUiKit() { if (_openUiKit != null) _openUiKit(); }
        private void OpenGfxProbe() { if (_openGfxProbe != null) _openGfxProbe(); }

        private void ToggleBle()
        {
            if (_bleToggle == null) return;
            _bleOn = _bleToggle(!_bleOn);
            _bleRow.Value = OnOff(_bleOn);
            Invalidate();
        }

        private void ToggleWifi()
        {
            if (_wifiToggle == null) return;
            _wifiOn = _wifiToggle(!_wifiOn);
            _wifiRow.Value = OnOff(_wifiOn);
            Invalidate();
        }

        private static string OnOff(bool on) { return on ? "ON" : "OFF"; }

        private static string BrightnessLabel(byte level)
        {
            if (level == 0xFF) return "HIGH";
            if (level == 0x80) return "MID";
            return "LOW";
        }

        // Dominant-axis orientation from the gravity vector.
        private static string OrientationLabel(Qmi8658Driver.ImuSample s)
        {
            float ax = s.AccelX < 0 ? -s.AccelX : s.AccelX;
            float ay = s.AccelY < 0 ? -s.AccelY : s.AccelY;
            float az = s.AccelZ < 0 ? -s.AccelZ : s.AccelZ;
            if (az >= ax && az >= ay) return s.AccelZ >= 0 ? "FACE UP" : "FACE DN";
            if (ay >= ax) return s.AccelY >= 0 ? "TOP UP" : "TOP DN";
            return s.AccelX >= 0 ? "TILT R" : "TILT L";
        }
    }
}
