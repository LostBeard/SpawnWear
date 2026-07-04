using nanoFramework.UI;
using System.Drawing;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// Android-style quick-settings panel: a 2-column grid of quick-toggle TILES (WiFi, Bluetooth,
    /// Companion link, HTTP server) that light up when on, plus a full-width brightness slider - built
    /// entirely from the SpawnDev.UI widget library (UITile / UIRow / UIColumn / UISlider). Opened by a
    /// downward swipe from the home-screen status bar; dismissed by the BOOT side button (navigator pop)
    /// or tapping out.
    ///
    /// The screen is decoupled from the concrete system services: the host (Program.cs, which owns the
    /// services) passes getters to read live state when the panel opens and toggle/change handlers wired
    /// to the real services. This mirrors how LauncherScreen/SettingsScreen take delegates.
    /// </summary>
    public class QuickSettingsScreen : WidgetScreen
    {
        /// <summary>Reads a live on/off state (called on every OnResume so the tiles reflect reality).</summary>
        public delegate bool BoolGetter();
        /// <summary>Reads a live 0-100 value (brightness) when the panel opens.</summary>
        public delegate int IntGetter();

        private readonly UITile _wifi;
        private readonly UITile _ble;
        private readonly UITile _companion;
        private readonly UITile _http;
        private readonly UISlider _brightness;

        private readonly BoolGetter _getWifi;
        private readonly BoolGetter _getBle;
        private readonly BoolGetter _getCompanion;
        private readonly BoolGetter _getHttp;
        private readonly IntGetter _getBrightness;

        public QuickSettingsScreen(
            Bitmap fb, int panelWidth, int panelHeight,
            BoolGetter getWifi, UITile.ToggleHandler setWifi,
            BoolGetter getBle, UITile.ToggleHandler setBle,
            BoolGetter getCompanion, UITile.ToggleHandler setCompanion,
            BoolGetter getHttp, UITile.ToggleHandler setHttp,
            IntGetter getBrightness, UISlider.ChangeHandler setBrightness,
            UIListRow.TapHandler openSettings)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _getWifi = getWifi;
            _getBle = getBle;
            _getCompanion = getCompanion;
            _getHttp = getHttp;
            _getBrightness = getBrightness;

            var t = Theme.Current;

            var root = new UIPanel
            {
                X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background,
            };

            // Title just under the status bar.
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 6, Width = panelWidth, Height = 46,
                Text = "QUICK SETTINGS", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            // A 2-column tile grid then a full-width brightness slider, stacked in the safe band below the
            // title. Each UIRow splits its width between two tiles; the column spaces the rows + slider.
            var col = new UIColumn
            {
                X = SafeArea.EdgeInset,
                Y = StatusBar.ReservedHeight + 66,
                Width = panelWidth - 2 * SafeArea.EdgeInset,
                Height = panelHeight - StatusBar.ReservedHeight - 120,
                Spacing = t.Gap,
            };

            const int TileH = 96;

            _wifi = new UITile { Text = "WIFI", Icon = UiIcon.Wifi, Toggled = setWifi };
            _ble = new UITile { Text = "BLUETOOTH", Icon = UiIcon.Bluetooth, Toggled = setBle };
            var row1 = new UIRow { Height = TileH, Spacing = t.Gap };
            row1.Add(_wifi);
            row1.Add(_ble);

            _companion = new UITile { Text = "COMPANION", Icon = UiIcon.Companion, Toggled = setCompanion };
            _http = new UITile { Text = "HTTP", Icon = UiIcon.Http, Toggled = setHttp };
            // TODO (TJ 2026-07-02): add a VOLUME control here once the audio service (ES8311) exists -
            // most likely a second full-width UISlider beside BRIGHT, or a 5th tile.
            var row2 = new UIRow { Height = TileH, Spacing = t.Gap };
            row2.Add(_companion);
            row2.Add(_http);

            _brightness = new UISlider { Text = "BRIGHT", Scale = t.BodyScale, Min = 10, Max = 100, Changed = setBrightness };

            col.Add(row1);
            col.Add(row2);
            col.Add(_brightness);

            // Full-width SETTINGS row opens the Settings screen. With the launcher an apps-only drawer
            // (no screen carousel), this drop-down is the way into Settings.
            if (openSettings != null)
            {
                col.Add(new UIListRow { Label = "SETTINGS", Height = 56, Tapped = openSettings });
            }

            root.Add(col);
            Root = root;
        }

        /// <summary>Pull live service state so the tiles/slider reflect reality each time the panel opens.</summary>
        public override void OnResume()
        {
            if (_getWifi != null) _wifi.On = _getWifi();
            if (_getBle != null) _ble.On = _getBle();
            if (_getCompanion != null) _companion.On = _getCompanion();
            if (_getHttp != null) _http.On = _getHttp();
            if (_getBrightness != null) _brightness.Value = _getBrightness();
            base.OnResume();
        }
    }
}
