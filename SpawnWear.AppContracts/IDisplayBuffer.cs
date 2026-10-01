using System.Drawing;

namespace SpawnWear.AppContracts
{
    /// <summary>The firmware's UI type faces, for <see cref="IDisplayBuffer.DrawText"/>.</summary>
    public enum TextStyle
    {
        /// <summary>~18 px: labels, hints, secondary text.</summary>
        Small = 0,
        /// <summary>~30 px: titles, prominent values.</summary>
        Large = 1,
        /// <summary>~120 px clock digits - only '0'-'9' and ':'.</summary>
        Clock = 2,
    }

    /// <summary>
    /// Drawable surface apps render into. Mirrors the subset of
    /// nanoFramework.UI.Bitmap that's safe for app code.
    ///
    /// Coordinates are panel-relative pixels (0..PanelWidth-1, 0..PanelHeight-1).
    /// Apps SHOULD reserve space for the system status bar (top StatusBarHeight
    /// pixels) and the page indicator (bottom PageIndicatorHeight pixels) -
    /// the firmware keeps drawing into those regions on every tick. Apps that
    /// scribble there will see their pixels overwritten by the next status-bar
    /// or page-dot refresh.
    ///
    /// Apps SHOULD call Flush at the end of OnResume / on visible state changes
    /// to push pending pixels to the panel; the firmware doesn't auto-flush
    /// on the app's behalf.
    /// </summary>
    public interface IDisplayBuffer
    {
        int PanelWidth { get; }
        int PanelHeight { get; }
        int StatusBarHeight { get; }
        int PageIndicatorHeight { get; }

        void Clear(Color background);
        void FillRectangle(int x, int y, int w, int h, Color color);
        void DrawString(string text, int x, int y, int scale, Color color);
        int MeasureString(string text, int scale);

        /// <summary>Draws text in one of the firmware's UI faces (the same proportional fonts the
        /// system screens use). <paramref name="y"/> is the top of the glyph box; see
        /// <see cref="TextHeight"/>. <see cref="TextStyle.Clock"/> only has the digits 0-9 and ':'.
        /// Falls back to the 5x7 font at a similar size when the SD-card font is missing.</summary>
        void DrawText(string text, int x, int y, TextStyle style, Color color);
        /// <summary>Pixel width <paramref name="text"/> would draw at in <paramref name="style"/>.</summary>
        int MeasureText(string text, TextStyle style);
        /// <summary>Glyph box height of <paramref name="style"/> (for Clock: the digits' ink height).</summary>
        int TextHeight(TextStyle style);

        /// <summary>Straight line <paramref name="thickness"/> pixels wide.</summary>
        void DrawLine(int x0, int y0, int x1, int y1, int thickness, Color color);
        /// <summary>Solid disc centered on (cx, cy).</summary>
        void FillCircle(int cx, int cy, int radius, Color color);
        /// <summary>Ring centered on (cx, cy): <paramref name="thickness"/> pixels drawn inward from
        /// <paramref name="radius"/>.</summary>
        void DrawCircle(int cx, int cy, int radius, int thickness, Color color);
        /// <summary>Filled rectangle with circular corners of <paramref name="radius"/>.</summary>
        void FillRoundRectangle(int x, int y, int w, int h, int radius, Color color);

        /// <summary>Push pending pixels to the panel.</summary>
        void Flush();

        /// <summary>Partial flush. The firmware applies CO5300 even/odd
        /// alignment automatically; pass any rectangle.</summary>
        void Flush(int x, int y, int w, int h);
    }
}
