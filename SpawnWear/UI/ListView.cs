using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Simple vertical list view for a small set of rows. Each row has a label,
    /// an optional value-string drawn right-aligned, and a callback invoked when
    /// the user taps that row. No scrolling in V1 - caller must size the list to
    /// fit the panel. Phase 3 adds scroll + flick support once the IMU is online.
    ///
    /// Rendering uses <see cref="SmallFont"/> at scale 4 so glyphs are 20x28
    /// pixels with even-aligned bounds. The selected row gets a white outline
    /// rectangle to give a visible focus indicator.
    /// </summary>
    public class ListView
    {
        public delegate void RowAction();

        public class Row
        {
            public string Label;
            public string Value;     // displayed right-aligned; caller mutates between ticks
            public RowAction OnTap;  // null = row is informational only
        }

        private readonly Bitmap _fb;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _rowHeight;
        private readonly int _scale;
        private readonly Row[] _rows;

        // Cached per-row last-rendered value strings, so partial flushes only
        // touch rows whose displayed value changed.
        private readonly string[] _lastRowValues;
        private bool _needsFullRepaint = true;
        private int _selectedIndex = -1;

        public ListView(Bitmap fb, int x, int y, int width, int rowHeight, int fontScale, Row[] rows)
        {
            _fb = fb;
            _x = x;
            _y = y;
            _width = width;
            _rowHeight = rowHeight;
            _scale = fontScale;
            _rows = rows;
            _lastRowValues = new string[rows.Length];
        }

        public int RowCount => _rows.Length;
        public int TotalHeight => _rowHeight * _rows.Length;

        /// <summary>Forces a full repaint on the next <see cref="Tick"/>.</summary>
        public void Invalidate()
        {
            _needsFullRepaint = true;
            for (int i = 0; i < _lastRowValues.Length; i++) _lastRowValues[i] = null;
        }

        /// <summary>
        /// Repaints rows whose displayed value changed since the last call. On the
        /// first call (or after Invalidate), repaints every row.
        /// </summary>
        public void Tick()
        {
            for (int i = 0; i < _rows.Length; i++)
            {
                string newValue = _rows[i].Value;
                if (_needsFullRepaint || newValue != _lastRowValues[i] || i == _selectedIndex)
                {
                    DrawRow(i, newValue);
                    FlushRow(i);
                    _lastRowValues[i] = newValue;
                }
            }
            _needsFullRepaint = false;
        }

        /// <summary>
        /// Hit-tests the tap against the row layout. Returns true and triggers the
        /// row's OnTap callback if the tap landed on a row; false otherwise (so
        /// the caller can let the navigator cycle to the next screen).
        /// </summary>
        public bool HandleTap(int x, int y)
        {
            System.Diagnostics.Debug.WriteLine("[List] tap=(" + x + "," + y + ") bounds=(x:" + _x + ".." + (_x + _width) + ", y:" + _y + ".." + (_y + _rowHeight * _rows.Length) + ")");
            if (x < _x || x >= _x + _width) return false;
            int relY = y - _y;
            if (relY < 0 || relY >= _rowHeight * _rows.Length) return false;
            int idx = relY / _rowHeight;
            if (_rows[idx].OnTap == null)
            {
                System.Diagnostics.Debug.WriteLine("[List] row " + idx + " has no OnTap");
                return false;
            }
            _selectedIndex = idx;
            try { _rows[idx].OnTap(); }
            catch { /* swallow - UI taps must not crash the main loop */ }
            // Force a redraw of all rows so any cross-row state (e.g. ValueGetter
            // returning a different value because the tap toggled something) gets
            // refreshed.
            Invalidate();
            return true;
        }

        private void DrawRow(int idx, string value)
        {
            int rowY = _y + idx * _rowHeight;

            // Even-align the row clear region for the CO5300 quirk.
            int alignedX = _x & ~1;
            int alignedY = rowY & ~1;
            int alignedRight = (_x + _width - 1) | 1;
            int alignedBottom = (rowY + _rowHeight - 1) | 1;
            int alignedW = alignedRight - alignedX + 1;
            int alignedH = alignedBottom - alignedY + 1;

            _fb.FillRectangle(alignedX, alignedY, alignedW, alignedH, Color.Black);

            // Selection indicator: 4-px white bar on the left edge.
            if (idx == _selectedIndex)
            {
                _fb.FillRectangle(alignedX, alignedY, 4, alignedH, Color.White);
            }

            // Label - left-aligned, 8px from the start (after the selection bar).
            int textY = rowY + (_rowHeight - SmallFont.GlyphHeight * _scale) / 2;
            int labelX = _x + 12;
            SmallFont.DrawString(_fb, _rows[idx].Label, labelX, textY, _scale, Color.White);

            // Value - right-aligned with 8px right padding.
            if (!string.IsNullOrEmpty(value))
            {
                int valueWidth = SmallFont.MeasureString(value, _scale);
                int valueX = _x + _width - valueWidth - 8;
                SmallFont.DrawString(_fb, value, valueX, textY, _scale, Color.White);
            }
        }

        private void FlushRow(int idx)
        {
            int rowY = _y + idx * _rowHeight;
            // Bitmap.Flush in the firmware now applies even/odd alignment itself
            // (Bitmap native handler 2026-05-03 commit 89a4a947), but pass already-aligned
            // bounds anyway so the alignment is visible to the reader and matches the
            // FillRectangle clear region above.
            int alignedX = _x & ~1;
            int alignedY = rowY & ~1;
            int alignedRight = (_x + _width - 1) | 1;
            int alignedBottom = (rowY + _rowHeight - 1) | 1;
            _fb.Flush(alignedX, alignedY, alignedRight - alignedX + 1, alignedBottom - alignedY + 1);
        }
    }
}
