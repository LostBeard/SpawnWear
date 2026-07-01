using nanoFramework.UI;
using SpawnWear.AppContracts;
using System.Drawing;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// WiFi status screen, rebuilt on the SpawnDev.UI widget library (WidgetScreen): a big signal glyph,
    /// a connected/disconnected status line, and SSID / IP / MODE info rows. Chrome (status bar + page
    /// dots) is the navigator's fixed overlay, so swiping to/from it slides the content under it.
    /// </summary>
    public class WifiScreen : WidgetScreen
    {
        private readonly IServiceHost _services;
        private readonly UIIcon _icon;
        private readonly UILabel _status;
        private readonly UIKeyValue _ssid, _ip, _mode;

        public WifiScreen(Bitmap fb, int panelWidth, int panelHeight, IServiceHost services)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _services = services;
            var t = Theme.Current;

            var root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background };
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 6, Width = panelWidth, Height = 40,
                Text = "WIFI", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            _icon = new UIIcon
            {
                X = (panelWidth - 96) / 2, Y = StatusBar.ReservedHeight + 54, Width = 96, Height = 96,
                Icon = UiIcon.Wifi, Color = t.Muted,
            };
            root.Add(_icon);

            _status = new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 158, Width = panelWidth, Height = 36,
                Text = "", Scale = t.BodyScale, Center = true, Color = t.Muted,
            };
            root.Add(_status);

            var col = new UIColumn
            {
                X = SafeArea.EdgeInset + 10, Y = StatusBar.ReservedHeight + 212,
                Width = panelWidth - 2 * (SafeArea.EdgeInset + 10), Height = 140, Spacing = 6,
            };
            _ssid = MakeRow("SSID"); _ip = MakeRow("IP"); _mode = MakeRow("MODE");
            col.Add(_ssid); col.Add(_ip); col.Add(_mode);
            root.Add(col);

            Root = root;
        }

        private static UIKeyValue MakeRow(string label) => new UIKeyValue { Label = label, Value = "" };

        // Chrome is navigator-owned; these uniform-wiring hooks are no-ops here.
        public void SetPageDots(int index, int total) { }
        public void SetStatusBar(StatusBar bar) { }

        public override void OnResume() { Refresh(); base.OnResume(); }
        public override void Tick() { if (Refresh()) Invalidate(); base.Tick(); }

        // Returns true if the displayed content changed.
        private bool Refresh()
        {
            var wifi = _services != null ? _services.GetWifi() : null;
            bool connected = wifi != null && wifi.IsConnected;
            var t = Theme.Current;
            bool changed = false;

            string st = connected ? "CONNECTED" : "DISCONNECTED";
            if (_status.Text != st) { _status.Text = st; changed = true; }
            _status.Color = connected ? t.Good : t.Bad;   // cheap, set each refresh
            _icon.Color = connected ? t.Accent : t.Muted;

            if (SetRow(_ssid, connected ? wifi.ConnectedSsid : "---")) changed = true;
            if (SetRow(_ip, connected ? wifi.IpAddress : "---")) changed = true;
            if (SetRow(_mode, "STATION")) changed = true;
            return changed;
        }

        private static bool SetRow(UIKeyValue row, string v) { if (row.Value == v) return false; row.Value = v; return true; }
    }
}
