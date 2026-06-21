using nanoFramework.UI;
using SpawnDev.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Proof screen for the new UI library: builds a UIElement tree (panel + label
    /// + button + counter) and renders it through an IUiSurface (WatchSurface here,
    /// a Blazor 2D canvas later). Tapping the button increments the counter via the
    /// widget's Clicked event; tapping elsewhere falls through so the navigator pops
    /// back. This is the watch-first milestone for the GameUI-mirrored UI lib.
    /// </summary>
    public class UiKitDemoScreen : IScreen
    {
        private readonly WatchSurface _surface;
        private readonly UIPanel _root;
        private readonly UILabel _count;
        private int _taps;

        public UiKitDemoScreen(Bitmap fb, int panelWidth, int panelHeight)
        {
            _surface = new WatchSurface(fb, panelWidth, panelHeight);

            _root = new UIPanel { X = 0, Y = 0, Width = panelWidth, Height = panelHeight, Background = Color.Black };

            // All inside the rounded-corner safe band (y 100..402), horizontally centered.
            _root.Add(new UILabel
            {
                X = 0, Y = 110, Width = panelWidth, Height = 44,
                Text = "UI KIT", Scale = 5, Center = true, Color = Color.White,
            });

            var button = new UIButton
            {
                X = (panelWidth - 220) / 2, Y = 200, Width = 220, Height = 76,
                Text = "TAP +1", Scale = 4, Background = Color.Gray, Foreground = Color.White,
            };
            button.Clicked = OnButtonClicked;
            _root.Add(button);

            _count = new UILabel
            {
                X = 0, Y = 310, Width = panelWidth, Height = 40,
                Text = "TAPS: 0", Scale = 4, Center = true, Color = Color.White,
            };
            _root.Add(_count);
        }

        private void OnButtonClicked()
        {
            _taps++;
            _count.Text = "TAPS: " + _taps;
            Render();
        }

        private void Render()
        {
            _root.Draw(_surface);   // root panel fills the screen black, then children
            _surface.FlushAll();
        }

        public void Tick() { /* static between taps */ }

        public void Invalidate() => Render();

        public void OnResume()
        {
            _taps = 0;
            _count.Text = "TAPS: 0";
            Render();
        }

        public void OnPause() { }

        // Button consumes -> stay; a miss returns false so the navigator pops back.
        public bool OnTap(int x, int y) => _root.OnTap(x, y);
    }
}
