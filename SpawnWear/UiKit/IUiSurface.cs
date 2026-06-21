using System.Drawing;

namespace SpawnDev.UI
{
    /// <summary>
    /// The one drawing primitive the whole UI library targets. Widgets draw against
    /// this and never touch a framebuffer or a canvas directly, so the same widget
    /// tree renders on the watch (WatchSurface -> nf Bitmap) and in the browser
    /// (CanvasSurface -> HTML5 canvas, the Blazor WASM simulator).
    ///
    /// Coordinates are top-left origin, pixels. Text is drawn with its top-left at
    /// (x, y). Lives in SpawnWear for now; extracts to a SpawnDev.UI package later.
    /// </summary>
    public interface IUiSurface
    {
        int Width { get; }
        int Height { get; }

        void Clear(Color color);
        // Method names mirror GameUI's UIRenderer (DrawRect/DrawText/MeasureText)
        // so widgets + app concepts stay aligned with the WebGPU game UI.
        void DrawRect(int x, int y, int w, int h, Color color);
        void DrawText(string text, int x, int y, int scale, Color color);

        int MeasureText(string text, int scale);
        int TextHeight(int scale);

        /// <summary>Pushes a region of the backing buffer to the physical surface.</summary>
        void Flush(int x, int y, int w, int h);
        /// <summary>Pushes the whole surface.</summary>
        void FlushAll();
    }
}
