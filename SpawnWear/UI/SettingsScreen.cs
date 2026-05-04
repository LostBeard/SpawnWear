using nanoFramework.UI;
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

        private static byte _currentBrightness = 0xFF;

        public SettingsScreen(Bitmap fb, int panelWidth, int panelHeight, RequestSleep requestSleep)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _requestSleep = requestSleep;

            int rowHeight = 60;
            int rows = 3;
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
            var rowDefs = new ListView.Row[]
            {
                _brightnessRow,
                new ListView.Row
                {
                    Label = "SLEEP",
                    Value = "NOW",
                    OnTap = TriggerSleep,
                },
                new ListView.Row
                {
                    Label = "BUILD",
                    Value = "20260503",
                    OnTap = null,
                },
            };
            _list = new ListView(_fb, listX, listY, listWidth, rowHeight, 4, rowDefs);
        }

        public void Tick()
        {
            // Header at the top: "SETTINGS" label centered, only redrawn on full repaint.
            // The list itself partial-flushes individual rows.
            _list.Tick();
        }

        public void Invalidate()
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            // Header.
            const string title = "SETTINGS";
            int scale = 5;
            int titleWidth = SmallFont.MeasureString(title, scale);
            int titleX = (_panelWidth - titleWidth) / 2;
            int titleY = 30;
            SmallFont.DrawString(_fb, title, titleX, titleY, scale, Color.White);

            // Footer hint.
            const string footer = "TAP OUTSIDE TO BACK";
            int footerScale = 2;
            int footerWidth = SmallFont.MeasureString(footer, footerScale);
            int footerX = (_panelWidth - footerWidth) / 2;
            int footerY = _panelHeight - 40;
            SmallFont.DrawString(_fb, footer, footerX, footerY, footerScale, Color.White);

            _fb.Flush();
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
    }
}
