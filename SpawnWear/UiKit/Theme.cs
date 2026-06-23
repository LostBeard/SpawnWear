using System.Drawing;

namespace SpawnDev.UI
{
    /// <summary>
    /// Central palette + metrics for the UI library. Widgets read <see cref="Current"/> for their
    /// defaults instead of scattering <c>Color.FromArgb(...)</c> literals across every screen. Swap
    /// <see cref="Current"/> (or hand a Theme to a screen) for light/dark or per-app theming.
    ///
    /// <para>AMOLED note: the panel is black = ~0 mA per off pixel, so backgrounds stay black and only
    /// surfaces (cards/buttons) light up.</para>
    /// </summary>
    public class Theme
    {
        // Surfaces
        public Color Background = Color.Black;                       // screen behind everything
        public Color Surface    = Color.FromArgb(30, 30, 36);       // cards, buttons, rows
        public Color SurfacePressed = Color.FromArgb(60, 60, 72);   // press/active feedback
        public Color Divider    = Color.FromArgb(48, 48, 56);

        // Text / content
        public Color OnSurface  = Color.White;                      // primary text/icon
        public Color Muted      = Color.FromArgb(150, 150, 160);    // secondary text

        // Accents (the launcher blue/purple line)
        public Color Accent     = Color.FromArgb(91, 141, 239);
        public Color AccentPressed = Color.FromArgb(70, 110, 200);
        public Color OnAccent   = Color.White;

        // Status colors
        public Color Good       = Color.LimeGreen;
        public Color Warn       = Color.FromArgb(255, 170, 0);
        public Color Bad        = Color.FromArgb(230, 70, 70);

        // Text scales (SmallFont is 5x7; scale N => 5N x 7N px glyphs)
        public int TitleScale   = 5;
        public int BodyScale    = 4;
        public int SmallScale   = 3;

        // Default metrics
        public int CornerInset  = 6;    // visual padding inside surfaces
        public int RowHeight    = 64;
        public int Gap          = 12;   // default spacing between stacked elements
        public int Radius       = 18;   // default widget corner radius (rounded = polished)

        /// <summary>The active theme. Default is the dark palette.</summary>
        public static Theme Current = new Theme();
    }
}
