using nanoFramework.UI;
using SpawnDev.UI;
using SpawnWear.Services;

namespace SpawnWear.UI
{
    /// <summary>
    /// Settings -> TIME ZONE: a scrolling list of the built-in zones (name + standard UTC offset; DST is
    /// applied automatically where the zone observes it). The current zone is marked; tapping a row
    /// selects + persists it and returns to Settings. Opens scrolled so the current zone is in view.
    /// </summary>
    public class TimeZoneScreen : WidgetScreen
    {
        public delegate void Done();

        readonly TimeService _time;
        readonly Done _done;
        readonly UIListRow[] _rows;
        readonly UIScrollColumn _col;
        const int RowH = 64, Spacing = 8;

        public TimeZoneScreen(Bitmap fb, int panelWidth, int panelHeight, TimeService time, Done done)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            _time = time;
            _done = done;
            var t = Theme.Current;
            var root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background };
            root.Add(new UILabel
            {
                X = 0, Y = StatusBar.ReservedHeight + 4, Width = panelWidth, Height = 38,
                Text = "TIME ZONE", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });
            int viewTop = StatusBar.ReservedHeight + 48;
            int viewBottom = panelHeight - 46;
            _col = new UIScrollColumn
            {
                X = SafeArea.EdgeInset, Y = viewTop,
                Width = panelWidth - 2 * SafeArea.EdgeInset, Height = viewBottom - viewTop, Spacing = Spacing,
            };
            _rows = new UIListRow[TimeService.ZoneCount];
            for (int i = 0; i < _rows.Length; i++)
            {
                int zone = i; // captured per row
                _rows[i] = new UIListRow
                {
                    Value = TimeService.ZoneOffsetLabel(i), Height = RowH, Scale = t.TitleScale,
                    Tapped = () => Pick(zone),
                };
                _col.Add(_rows[i]);
            }
            root.Add(_col);
            ScrollTarget = _col;
            Root = root;
        }

        public override void OnResume()
        {
            int cur = _time.Zone;
            for (int i = 0; i < _rows.Length; i++)
                _rows[i].Label = (i == cur ? "* " : "") + TimeService.ZoneName(i);
            // Bring the current zone near the top of the viewport (Layout clamps to the list's range).
            _col.ScrollOffset = cur * (RowH + Spacing) - RowH;
            base.OnResume();
        }

        void Pick(int zone)
        {
            _time.SetZone(zone);
            if (_done != null) _done();
        }

        // Chrome is navigator-owned; uniform-wiring hooks are no-ops here.
        public void SetStatusBar(StatusBar bar) { }
    }
}
