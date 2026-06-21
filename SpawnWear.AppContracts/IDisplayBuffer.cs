using System.Drawing;

namespace SpawnWear.AppContracts
{
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

        /// <summary>Push pending pixels to the panel.</summary>
        void Flush();

        /// <summary>Partial flush. The firmware applies CO5300 even/odd
        /// alignment automatically; pass any rectangle.</summary>
        void Flush(int x, int y, int w, int h);
    }
}
