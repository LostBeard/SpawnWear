using nanoFramework.UI;
using SpawnWear.Drivers.Power;
using System;
using System.Drawing;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// Stats / diagnostics screen, rebuilt on the SpawnDev.UI widget library (WidgetScreen): battery
    /// percent, battery voltage, and uptime as info rows. Chrome (status bar + page dots) is the
    /// navigator's fixed overlay, so swiping to/from it slides the content under it.
    /// </summary>
    public class StatsScreen : WidgetScreen
    {
        private readonly Axp2101Driver _axp;
        private readonly UIKeyValue _battery, _voltage, _uptime;

        public StatsScreen(Bitmap framebuffer, int panelWidth, int panelHeight, Axp2101Driver axp = null)
            : base(new WatchSurface(framebuffer, panelWidth, panelHeight))
        {
            _axp = axp;
            var t = Theme.Current;

            var root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background };
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 6, Width = panelWidth, Height = 40,
                Text = "STATS", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            var col = new UIColumn
            {
                X = SafeArea.EdgeInset + 12, Y = StatusBar.ReservedHeight + 90,
                Width = panelWidth - 2 * (SafeArea.EdgeInset + 12), Height = 220, Spacing = 18,
            };
            _battery = new UIKeyValue { Label = "BATTERY", Value = "", Height = 44 };
            _voltage = new UIKeyValue { Label = "VOLTAGE", Value = "", Height = 44 };
            _uptime = new UIKeyValue { Label = "UPTIME", Value = "", Height = 44 };
            col.Add(_battery); col.Add(_voltage); col.Add(_uptime);
            root.Add(col);

            Root = root;
        }

        // Chrome is navigator-owned; these uniform-wiring hooks are no-ops here.
        public void SetPageDots(int index, int total) { }
        public void SetStatusBar(StatusBar bar) { }

        public override void OnResume() { Refresh(); base.OnResume(); }
        public override void Tick() { if (Refresh()) Invalidate(); base.Tick(); }

        private bool Refresh()
        {
            int pct = -1, mv = -1;
            if (_axp != null)
            {
                try { pct = _axp.ReadBatteryPercent(); } catch { pct = -1; }
                try { mv = _axp.ReadBatteryMillivolts(); } catch { mv = -1; }
            }
            int uptimeSec = (int)((DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond) % 86400);
            int h = (uptimeSec / 3600) % 24, m = (uptimeSec / 60) % 60, s = uptimeSec % 60;

            bool changed = false;
            if (Set(_battery, pct < 0 ? "---" : pct.ToString() + "%")) changed = true;
            if (Set(_voltage, mv < 0 ? "---" : mv.ToString() + " MV")) changed = true;
            if (Set(_uptime, Two(h) + ":" + Two(m) + ":" + Two(s))) changed = true;
            return changed;
        }

        private static bool Set(UIKeyValue row, string v) { if (row.Value == v) return false; row.Value = v; return true; }

        static string Two(int n)
        {
            if (n < 0) n = 0;
            if (n >= 100) return n.ToString();
            return ((char)('0' + n / 10)).ToString() + ((char)('0' + n % 10)).ToString();
        }
    }
}
