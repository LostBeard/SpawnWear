using nanoFramework.UI;
using System;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Dev probe: exercises the native nanoFramework.Graphics Bitmap primitives the
    /// production UI has NEVER used (it hand-rolls everything from FillRectangle because
    /// FillGradientRectangle was found unimplemented on this CO5300 nf-interpreter build).
    /// Each primitive is drawn inside a white-framed box (drawn with FillRectangle, which
    /// is known-good) so the result is unambiguous:
    ///   - shape appears in the box  -> the native primitive WORKS on this firmware
    ///   - box is empty              -> the primitive is a silent no-op
    ///   - "ERR" beside the box      -> the primitive threw (logged too)
    /// This decides the whole UI-facelift approach: real icons (DrawImage), smooth tiles
    /// (FillRoundRectangle), circles (DrawEllipse), lines (DrawLine).
    /// TJ reads the screen and reports which boxes rendered.
    /// </summary>
    public class GraphicsProbeScreen : IScreen
    {
        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;

        int _pageDotIndex = -1;
        int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        bool _needsRepaint = true;

        static readonly Color Frame = Color.FromArgb(90, 90, 90);
        static readonly Color LabelC = Color.FromArgb(200, 200, 200);
        static readonly Color OkC = Color.FromArgb(40, 220, 90);
        static readonly Color ErrC = Color.FromArgb(240, 60, 60);
        static readonly Color Accent = Color.FromArgb(60, 150, 255);

        public GraphicsProbeScreen(Bitmap fb, int panelWidth, int panelHeight)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
        }

        public void Invalidate() { _needsRepaint = true; }
        public void OnResume() => Invalidate();
        public void OnPause() { }
        public bool OnTap(int x, int y) => false; // tap outside cycles back via navigator

        public void Tick()
        {
            if (_needsRepaint) { FullRepaint(); _needsRepaint = false; return; }
            _statusBar?.Render(force: false);
        }

        void FullRepaint()
        {
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            int top = _statusBar != null ? StatusBar.ReservedHeight : 0;
            const string title = "GFX PROBE";
            NativeFont.DrawCentered(NativeFont.Shared, _fb, title, _panelWidth, top + 8, Color.White, 3);

            // Vertical list of primitive tests. Box on the right, label on the left, status
            // pip far right. Rows sit inside the safe band (below title, above bottom corner).
            int rowY = top + 40;
            int stride = 40;
            int boxX = 210, boxW = 90, boxH = 30;
            int labelX = 30;

            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "FillRect", DrawFillRect);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "RoundRect", DrawRoundRect);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "Ellipse", DrawEllipseCell);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "Line", DrawLineCell);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "RectOutln", DrawRectOutline);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "Gradient", DrawGradient);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "DrawImage", DrawImageCell);
            RunCell(labelX, boxX, ref rowY, stride, boxW, boxH, "SetPixel", DrawSetPixel);

            // Native-font sample loaded from SD (D:\spawnsans.tinyfnt): proves the whole
            // .tinyfnt -> NativeText -> BMP -> DrawImage path. Compared against the 5x7 SmallFont.
            int fy = rowY + 4;
            NativeFont nativeFont = NativeFont.Shared;
            if (nativeFont != null && nativeFont.IsValid)
            {
                SmallFont.DrawString(_fb, "5x7", 30, fy + 4, 2, LabelC);
                SmallFont.DrawString(_fb, "SpawnWear 0123", 88, fy + 4, 2, Color.White);
                SmallFont.DrawString(_fb, "TTF", 30, fy + 30, 2, LabelC);
                nativeFont.Draw(_fb, "SpawnWear 0123", 88, fy + 26, Accent);
            }
            else
            {
                SmallFont.DrawString(_fb, "native font: no SD file / load failed", 30, fy + 4, 2, ErrC);
            }

            if (_pageDotCount > 1)
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);

            _fb.Flush();
            _statusBar?.Render(force: true);
        }

        delegate void CellDraw(int x, int y, int w, int h);

        void RunCell(int labelX, int boxX, ref int rowY, int stride, int boxW, int boxH, string name, CellDraw draw)
        {
            int y = rowY;
            SmallFont.DrawString(_fb, name, labelX, y + 8, 2, LabelC);

            // Frame the test box with FillRectangle (known-good) so an empty box = no-op.
            FrameBox(boxX, y, boxW, boxH, Frame);

            bool threw = false;
            try { draw(boxX + 1, y + 1, boxW - 2, boxH - 2); }
            catch { threw = true; }

            // Status pip + text at far right.
            int pipX = boxX + boxW + 14;
            _fb.FillRectangle(pipX, y + 10, 12, 12, threw ? ErrC : OkC);
            SmallFont.DrawString(_fb, threw ? "ERR" : "OK", pipX + 18, y + 8, 2, threw ? ErrC : OkC);

            rowY += stride;
        }

        // FillRectangle frame = 4 thin rects (this primitive is the known-good baseline).
        void FrameBox(int x, int y, int w, int h, Color c)
        {
            _fb.FillRectangle(x, y, w, 1, c);
            _fb.FillRectangle(x, y + h - 1, w, 1, c);
            _fb.FillRectangle(x, y, 1, h, c);
            _fb.FillRectangle(x + w - 1, y, 1, h, c);
        }

        // ----- the primitives under test (drawn inside the given box) -----

        void DrawFillRect(int x, int y, int w, int h) => _fb.FillRectangle(x, y, w, h, Accent);

        void DrawRoundRect(int x, int y, int w, int h) =>
            _fb.FillRoundRectangle(x, y, w, h, h / 2, h / 2, Accent);

        void DrawEllipseCell(int x, int y, int w, int h)
        {
            int rx = w / 2 - 1, ry = h / 2 - 1;
            int cx = x + w / 2, cy = y + h / 2;
            // filled ellipse: solid gradient (start==end) so it fills, not just outlines.
            _fb.DrawEllipse(Accent, 1, cx, cy, rx, ry, Accent, 0, 0, Accent, 0, 0);
        }

        void DrawLineCell(int x, int y, int w, int h)
        {
            _fb.DrawLine(Accent, 3, x, y, x + w, y + h);
            _fb.DrawLine(Accent, 3, x, y + h, x + w, y);
        }

        void DrawRectOutline(int x, int y, int w, int h) =>
            _fb.DrawRectangle(x, y, w, h, 2, Accent);

        void DrawGradient(int x, int y, int w, int h) =>
            _fb.FillGradientRectangle(x, y, w, h, Accent, x, y, Color.FromArgb(255, 40, 120), x, y + h);

        void DrawImageCell(int x, int y, int w, int h)
        {
            // Build a small pattern bitmap and blit it - this is the real-icon path.
            int s = h - 2;
            var img = new Bitmap(s, s);
            img.FillRectangle(0, 0, s, s, Color.FromArgb(255, 180, 40));
            img.FillRectangle(s / 4, s / 4, s / 2, s / 2, Color.FromArgb(40, 40, 200));
            _fb.DrawImage(new System.Drawing.Point(x + 2, y + 1), img);
        }

        void DrawSetPixel(int x, int y, int w, int h)
        {
            for (int i = 0; i < w; i += 2)
                for (int j = 0; j < h; j += 2)
                    _fb.SetPixel(x + i, y + j, Accent);
        }
    }
}
