using nanoFramework.UI;
using SpawnDev.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// Proof screen for the widget framework. Extends <see cref="WidgetScreen"/> (which owns the
    /// lifecycle/draw/hit-test), builds a themed tree with a <see cref="UIColumn"/> laying out a
    /// button + a real <see cref="UISwitch"/> + a status label inside the round-corner
    /// <see cref="SafeArea"/> band - no hand-positioned pixels. Tap the button to count; flip the
    /// switch to see toggle state; a miss falls through so the navigator pops/advances.
    /// </summary>
    public class UiKitDemoScreen : WidgetScreen
    {
        private readonly UILabel _status;
        private int _taps;

        public UiKitDemoScreen(Bitmap fb, int panelWidth, int panelHeight)
            : base(new WatchSurface(fb, panelWidth, panelHeight))
        {
            var t = Theme.Current;

            var root = new UIPanel
            {
                X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = t.Background,
            };

            // Title: centered content is safe anywhere vertically (never clips the round corners).
            root.Add(new UILabel
            {
                X = 0, Y = 100, Width = panelWidth, Height = 50,
                Text = "UI KIT", Scale = t.TitleScale, Center = true, Color = t.OnSurface,
            });

            // Content column inside the safe band, below the title. The column lays out its children
            // top-to-bottom at the column's width - the screen no longer positions each one.
            var col = new UIColumn
            {
                X = SafeArea.EdgeInset, Y = 185,
                Width = panelWidth - 2 * SafeArea.EdgeInset, Height = 300,
                Spacing = t.Gap,
            };

            var button = new UIButton
            {
                Height = 80, Text = "TAP +1", Scale = t.BodyScale,
                Background = t.Accent, Foreground = t.OnAccent,
            };
            button.Clicked = OnButtonClicked;
            col.Add(button);

            var sw = new UISwitch { Text = "FEEDBACK", Scale = t.BodyScale };
            sw.Toggled = OnToggled;
            col.Add(sw);

            _status = new UILabel
            {
                Height = 44, Text = "TAPS: 0", Scale = t.BodyScale, Center = true, Color = t.Muted,
            };
            col.Add(_status);

            root.Add(col);
            Root = root;
        }

        private void OnButtonClicked()
        {
            _taps++;
            _status.Text = "TAPS: " + _taps;
            RequestRender();
        }

        private void OnToggled(bool on)
        {
            _status.Text = on ? "FEEDBACK ON" : "FEEDBACK OFF";
            RequestRender();
        }

        public override void OnResume()
        {
            _taps = 0;
            _status.Text = "TAPS: 0";
            base.OnResume();
        }
    }
}
