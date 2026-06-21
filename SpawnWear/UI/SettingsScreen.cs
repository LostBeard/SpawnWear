using nanoFramework.UI;
using SpawnWear.Drivers.Imu;
using SpawnWear.Drivers.Power;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// First Phase 2 list-driven screen. Three tappable rows let the user toggle
    /// brightness preset, force-sleep, and identify the firmware build:
    ///
    ///   BRIGHTNESS  HIGH / MID / LOW
    ///   SLEEP       (action - immediate panel sleep, same as BOOT button)
    ///   BUILD       2026-05-03
    ///
    /// Tap on the "BRIGHTNESS" row cycles through three presets and applies the
    /// new level via DisplayControl.SetBrightness immediately.
    /// Tap on "SLEEP" rewinds the idle clock so the next OnTick transitions the
    /// state machine to ScreenState.Sleep (panel SLPIN).
    /// Tap on "BUILD" is a no-op for now; future versions will show full build
    /// info on long-press.
    /// Taps outside any row let the navigator cycle to the next screen.
    /// </summary>
    public class SettingsScreen : IScreen
    {
        public delegate void RequestSleep();

        private readonly Bitmap _fb;
        private readonly int _panelWidth;
        private readonly int _panelHeight;
        private readonly RequestSleep _requestSleep;
        private readonly ListView _list;
        private readonly ListView.Row _brightnessRow;
        private readonly ListView.Row _motionRow;
        private readonly Qmi8658Driver _imu;
        private int _motionThrottle;
        private int _pageDotIndex = -1;
        private int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        private StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        private static byte _currentBrightness = 0xFF;

        public SettingsScreen(Bitmap fb, int panelWidth, int panelHeight, RequestSleep requestSleep, Qmi8658Driver imu)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _requestSleep = requestSleep;
            _imu = imu;

            int rowHeight = 60;
            int rows = 4;
            int listHeight = rows * rowHeight;
            int listWidth = panelWidth - 40;
            int listX = (panelWidth - listWidth) / 2;
            int listY = (panelHeight - listHeight) / 2;

            _brightnessRow = new ListView.Row
            {
                Label = "BRIGHTNESS",
                Value = BrightnessLabel(_currentBrightness),
                OnTap = ToggleBrightness,
            };
            _motionRow = new ListView.Row
            {
                Label = "MOTION",
                Value = _imu != null ? "----" : "N/A",
                OnTap = null, // informational - live orientation from the QMI8658 IMU
            };
            var rowDefs = new ListView.Row[]
            {
                _brightnessRow,
                _motionRow,
                new ListView.Row
                {
                    Label = "SLEEP",
                    Value = "NOW",
                    OnTap = TriggerSleep,
                },
                new ListView.Row
                {
                    Label = "BUILD",
                    Value = "20260620",
                    OnTap = null,
                },
            };
            _list = new ListView(_fb, listX, listY, listWidth, rowHeight, 4, rowDefs);
        }

        public void Tick()
        {
            _statusBar?.Render(force: false);

            // Live orientation from the IMU, throttled to ~1/8 ticks so we are not
            // hammering the I2C bus. ListView only repaints the row when the derived
            // label actually changes, so a stationary watch does not flicker.
            if (_imu != null && (++_motionThrottle & 0x07) == 0)
            {
                if (_imu.TryRead(out var s))
                {
                    _motionRow.Value = OrientationLabel(s);
                }
            }

            _list.Tick();
        }

        public void Invalidate()
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            // Header. Sits below the status bar.
            int statusBarHeight = _statusBar != null ? StatusBar.ReservedHeight : 0;
            const string title = "SETTINGS";
            int scale = 5;
            int titleWidth = SmallFont.MeasureString(title, scale);
            int titleX = (_panelWidth - titleWidth) / 2;
            int titleY = statusBarHeight + 16;
            SmallFont.DrawString(_fb, title, titleX, titleY, scale, Color.White);

            // Footer hint - drawn ABOVE the page-dots row.
            const string footer = "TAP OUTSIDE TO BACK";
            int footerScale = 2;
            int footerWidth = SmallFont.MeasureString(footer, footerScale);
            int footerX = (_panelWidth - footerWidth) / 2;
            int footerY = _panelHeight - 60;
            SmallFont.DrawString(_fb, footer, footerX, footerY, footerScale, Color.White);

            if (_pageDotCount > 1)
            {
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            }

            _fb.Flush();
            _statusBar?.Render(force: true);
            _list.Invalidate();
        }

        public void OnResume() => Invalidate();

        public void OnPause() { /* no resources */ }

        public bool OnTap(int x, int y)
        {
            // Let the list view try first; if the tap landed on a row it consumes
            // the gesture. Otherwise return false so the navigator cycles to the
            // next screen.
            return _list.HandleTap(x, y);
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
            _brightnessRow.Value = BrightnessLabel(_currentBrightness);
        }

        private void TriggerSleep()
        {
            if (_requestSleep != null) _requestSleep();
        }

        private static string BrightnessLabel(byte level)
        {
            if (level == 0xFF) return "HIGH";
            if (level == 0x80) return "MID";
            return "LOW";
        }

        // Dominant-axis orientation from the gravity vector: ~1 g on one axis tells us
        // which way the watch is facing; the largest-magnitude axis wins.
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
