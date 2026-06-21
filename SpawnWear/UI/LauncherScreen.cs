using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Android-style launcher / home screen. Shows a small grid of app tiles
    /// (icon + label) under the system status bar. Tap a tile to switch the
    /// navigator to that app's screen; tap empty space falls through so the
    /// existing tap-to-cycle navigation still works.
    ///
    /// V1 layout: 1 row of 3 tiles centered on the panel. Each tile is a
    /// square with a primitive icon drawn from rectangles + the app label
    /// underneath in <see cref="SmallFont"/>. Phase 2.5 will let app
    /// "manifests" loaded from the SD card register their own tiles + icon
    /// data.
    /// </summary>
    public class LauncherScreen : IScreen
    {
        /// <summary>Invoked when a non-placeholder tile is tapped. The handler
        /// decides what to do based on the tile: a built-in tile navigates to
        /// its <see cref="Tile.TargetScreenIndex"/>; an app tile (non-null
        /// <see cref="Tile.AppName"/>) loads + launches that installed app.</summary>
        public delegate void ActivateTile(Tile tile);

        /// <summary>Supplies the current tile set. Called at construction and on
        /// every <see cref="OnResume"/> so the launcher reflects apps installed
        /// (or removed) since it was last shown - no restart needed.</summary>
        public delegate Tile[] TileProvider();

        public class Tile
        {
            public string Label;
            public int TargetScreenIndex;
            // Non-null for an installed-app tile: the logical app name to load
            // from the SD library. Built-in/system tiles leave this null and use
            // TargetScreenIndex instead.
            public string AppName;
            public IconKind Icon;
            // Notification badge count. 0 = no badge. >99 displays as ">9" since
            // the small red bubble can't fit three digits at this tile size.
            // Phase 3 will wire this to a NotificationService that aggregates
            // events from BLE + system + per-app sources.
            public int BadgeCount;
            // Tinted background fill. Color.Black = no fill (just the outline).
            // Stock Waveshare firmware uses gradient fills for visual hierarchy;
            // V2 here uses solid color tints as a stepping stone toward that.
            public Color Background = Color.Black;
            // Tiles with no TargetScreenIndex (-1) are placeholders for apps not
            // yet implemented - they render dimmed and ignore taps.
        }

        public enum IconKind { Clock, Stats, Settings, Music, Gallery, Wifi, Empty, App }

        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        Tile[] _tiles;
        readonly TileProvider _tileProvider;
        readonly ActivateTile _activate;
        StatusBar _statusBar;
        int _pageDotIndex = -1;
        int _pageDotCount;

        // Tile geometry (computed in Layout). 3x3 grid: cols * rows tiles, with
        // gaps between them, centered horizontally and below the status bar.
        const int Cols = 3;
        const int Rows = 3;
        int _tileSize;
        int _tileGap;
        int _gridTopY;
        int _gridLeftX;

        public LauncherScreen(Bitmap fb, int panelWidth, int panelHeight, TileProvider tileProvider, ActivateTile activate)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _tileProvider = tileProvider;
            _activate = activate;
            _tiles = tileProvider != null ? tileProvider() : new Tile[0];
        }

        // Re-pull the tile set (built-ins + currently-installed apps). Called
        // whenever the launcher comes to the foreground so a freshly-installed
        // app shows up without a reboot.
        void RefreshTiles()
        {
            if (_tileProvider == null) return;
            var t = _tileProvider();
            if (t != null) _tiles = t;
        }

        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }

        public void Tick()
        {
            _statusBar?.Render(force: false);
        }

        public void Invalidate()
        {
            Layout();
            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            // 3x3 grid. Empty slots (i >= _tiles.Length) render nothing so the
            // launcher gracefully scales from 1 to 9 registered apps.
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Cols; col++)
                {
                    int idx = row * Cols + col;
                    if (idx >= _tiles.Length) break;
                    int x = _gridLeftX + col * (_tileSize + _tileGap);
                    int y = _gridTopY + row * (_tileSize + _tileGap);
                    DrawTile(x, y, _tiles[idx]);
                }
            }

            // Page dots.
            if (_pageDotCount > 1)
            {
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            }

            _fb.Flush();
            _statusBar?.Render(force: true);
        }

        public void OnResume() { RefreshTiles(); Invalidate(); }
        public void OnPause() { }

        public bool OnTap(int x, int y)
        {
            if (_tileSize == 0) Layout();

            int relX = x - _gridLeftX;
            int relY = y - _gridTopY;
            if (relX < 0 || relY < 0) return false;

            int colSlot = relX / (_tileSize + _tileGap);
            int rowSlot = relY / (_tileSize + _tileGap);
            if (colSlot < 0 || colSlot >= Cols) return false;
            if (rowSlot < 0 || rowSlot >= Rows) return false;

            // Reject taps that land in the gap between cells.
            int colInSlot = relX - colSlot * (_tileSize + _tileGap);
            int rowInSlot = relY - rowSlot * (_tileSize + _tileGap);
            if (colInSlot >= _tileSize || rowInSlot >= _tileSize) return false;

            int idx = rowSlot * Cols + colSlot;
            if (idx >= _tiles.Length) return false;

            var tile = _tiles[idx];
            // A placeholder is a system tile with no destination AND no app.
            if (tile.TargetScreenIndex < 0 && tile.AppName == null)
            {
                System.Diagnostics.Debug.WriteLine("[Launcher] tile " + tile.Label + " is a placeholder, ignored");
                return true; // consume so navigator doesn't cycle
            }
            System.Diagnostics.Debug.WriteLine("[Launcher] activate " + tile.Label +
                (tile.AppName != null ? " (app)" : " -> screen " + tile.TargetScreenIndex));
            _activate?.Invoke(tile);
            return true;
        }

        // ----- Layout -----

        void Layout()
        {
            int statusBarH = _statusBar != null ? StatusBar.ReservedHeight : 0;
            int safeBottomReserved = 60; // page dots breathing room
            int availableH = _panelHeight - statusBarH - safeBottomReserved;
            // Corner-rounding safe area inset (per Notes/co5300-quirks.md - the
            // visible AMOLED is inset ~50 px from each panel edge by the case bezel).
            int safeInset = 40;
            int availableW = _panelWidth - 2 * safeInset;

            _tileGap = 14;

            // Labels render INSIDE each tile's bottom strip; no extra vertical
            // space needed between rows beyond _tileGap. This matches the
            // Android launcher pattern (icon top, label bottom, no overflow).
            int sizeFromW = (availableW - (Cols - 1) * _tileGap) / Cols;
            int sizeFromH = (availableH - (Rows - 1) * _tileGap) / Rows;
            _tileSize = sizeFromW < sizeFromH ? sizeFromW : sizeFromH;
            if (_tileSize < 60) _tileSize = 60;

            int totalGridW = Cols * _tileSize + (Cols - 1) * _tileGap;
            int totalGridH = Rows * _tileSize + (Rows - 1) * _tileGap;
            _gridLeftX = (_panelWidth - totalGridW) / 2;
            _gridTopY = statusBarH + (availableH - totalGridH) / 2;
        }

        // ----- Tile rendering -----

        void DrawTile(int x, int y, Tile tile)
        {
            bool placeholder = tile.TargetScreenIndex < 0 && tile.AppName == null;

            // Vertical gradient background drawn as horizontal slices. ~16 bands
            // is enough to look smooth at 100-px tile size. We don't use
            // FillGradientRectangle because the native ESP32 graphics driver in
            // this nf-interpreter build doesn't implement that primitive.
            int bands = 16;
            int bandH = _tileSize / bands;
            int topR = placeholder ? 70 : tile.Background.R;
            int topG = placeholder ? 70 : tile.Background.G;
            int topB = placeholder ? 70 : tile.Background.B;
            int botR = placeholder ? 35 : (topR * 25) / 100;
            int botG = placeholder ? 35 : (topG * 25) / 100;
            int botB = placeholder ? 35 : (topB * 25) / 100;
            for (int b = 0; b < bands; b++)
            {
                int rC = topR + ((botR - topR) * b) / (bands - 1);
                int gC = topG + ((botG - topG) * b) / (bands - 1);
                int bC = topB + ((botB - topB) * b) / (bands - 1);
                Color bandColor = Color.FromArgb(rC, gC, bC);
                int by = y + b * bandH;
                int bh = (b == bands - 1) ? (_tileSize - b * bandH) : bandH;
                _fb.FillRectangle(x, by, _tileSize, bh, bandColor);
            }

            // Stepped quarter-circle corner mask (8-px radius). For each corner,
            // we paint black scanlines whose length tapers as we move away from
            // the panel edge — visually approximates a rounded corner without
            // needing polygon primitives. Pattern (top-left):
            //   row 0: 8 px wide
            //   row 1: 6 px
            //   row 2: 4 px
            //   row 3: 3 px
            //   row 4: 2 px
            //   row 5: 1 px
            int[] cornerLens = new int[] { 8, 6, 4, 3, 2, 1 };
            for (int i = 0; i < cornerLens.Length; i++)
            {
                int len = cornerLens[i];
                // Top edge
                _fb.FillRectangle(x, y + i, len, 1, Color.Black);
                _fb.FillRectangle(x + _tileSize - len, y + i, len, 1, Color.Black);
                // Bottom edge
                _fb.FillRectangle(x, y + _tileSize - 1 - i, len, 1, Color.Black);
                _fb.FillRectangle(x + _tileSize - len, y + _tileSize - 1 - i, len, 1, Color.Black);
            }

            // Layout INSIDE the tile: icon in the top ~65%, label in the bottom ~25%
            // with a small gap between them. This matches the Android launcher
            // shape and prevents labels from overflowing into the next row.
            int labelScale = 2;
            int labelH = SmallFont.GlyphHeight * labelScale;
            int labelStripH = labelH + 6; // 3 px padding above + below
            int iconAreaH = _tileSize - labelStripH - 6; // 6 px top padding
            int iconBoxSize = iconAreaH < (_tileSize * 6) / 10 ? iconAreaH : (_tileSize * 6) / 10;
            int iconX = x + (_tileSize - iconBoxSize) / 2;
            int iconY = y + 6 + (iconAreaH - iconBoxSize) / 2;
            Color iconColor = placeholder ? Color.FromArgb(120, 120, 120) : Color.White;

            switch (tile.Icon)
            {
                case IconKind.Clock: DrawClockIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.Stats: DrawStatsIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.Settings: DrawSettingsIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.Music: DrawMusicIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.Gallery: DrawGalleryIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.Wifi: DrawWifiIcon(iconX, iconY, iconBoxSize, iconColor); break;
                case IconKind.App: DrawAppLetterIcon(iconX, iconY, iconBoxSize, iconColor, tile.Label); break;
                case IconKind.Empty: break;
            }

            // Notification badge (top-right corner of the tile).
            if (tile.BadgeCount > 0)
            {
                DrawBadge(x + _tileSize - 22, y - 4, tile.BadgeCount);
            }

            // Label inside the bottom strip of the tile.
            int labelW = SmallFont.MeasureString(tile.Label, labelScale);
            int labelX = x + (_tileSize - labelW) / 2;
            int labelY = y + _tileSize - labelStripH + 3;
            Color labelColor = placeholder ? Color.FromArgb(120, 120, 120) : Color.White;
            SmallFont.DrawString(_fb, tile.Label, labelX, labelY, labelScale, labelColor);
        }

        // Filled red badge with a small white digit; "9+" if count > 9.
        void DrawBadge(int x, int y, int count)
        {
            int size = 22;
            Color badge = Color.Red;
            Color text = Color.White;
            _fb.FillRectangle(x, y, size, size, badge);
            int t = 2;
            _fb.FillRectangle(x, y, size, t, text);
            _fb.FillRectangle(x, y + size - t, size, t, text);
            _fb.FillRectangle(x, y, t, size, text);
            _fb.FillRectangle(x + size - t, y, t, size, text);

            string display = count > 9 ? "9+" : ((char)('0' + count)).ToString();
            int scale = 2;
            int textW = SmallFont.MeasureString(display, scale);
            int textH = SmallFont.GlyphHeight * scale;
            int textX = x + (size - textW) / 2;
            int textY = y + (size - textH) / 2;
            SmallFont.DrawString(_fb, display, textX, textY, scale, text);
        }

        /// <summary>
        /// Updates a tile's notification badge count and forces a repaint of
        /// the launcher next time it becomes visible. Safe to call from any
        /// thread; the launcher reads BadgeCount fresh on every Invalidate.
        /// </summary>
        public void SetBadge(int slot, int count)
        {
            if (slot < 0 || slot >= _tiles.Length) return;
            _tiles[slot].BadgeCount = count;
        }

        // ----- Icon primitives (rectangles only) -----

        // Generic app icon: the app's first letter, big and centered. Gives each
        // installed app a distinct, recognizable tile without per-app bitmap art.
        void DrawAppLetterIcon(int x, int y, int size, Color color, string label)
        {
            string s = "?";
            if (label != null && label.Length > 0)
            {
                char c = label[0];
                if (c >= 'a' && c <= 'z') c = (char)(c - 32); // upper-case the glyph
                s = c.ToString();
            }
            int scale = size / SmallFont.GlyphHeight;
            if (scale < 2) scale = 2;
            int w = SmallFont.MeasureString(s, scale);
            // Shrink if the glyph would overflow the icon box.
            while (w > size && scale > 2) { scale--; w = SmallFont.MeasureString(s, scale); }
            int h = SmallFont.GlyphHeight * scale;
            SmallFont.DrawString(_fb, s, x + (size - w) / 2, y + (size - h) / 2, scale, color);
        }

        void DrawClockIcon(int x, int y, int size, Color color)
        {
            // Outline circle approximation: 4 corners with rectangles, plus a
            // hands cross in the middle.
            int t = 4;
            // Top / bottom thick caps to suggest curvature.
            int capW = size / 2;
            _fb.FillRectangle(x + (size - capW) / 2, y, capW, t, color);
            _fb.FillRectangle(x + (size - capW) / 2, y + size - t, capW, t, color);
            _fb.FillRectangle(x, y + (size - capW) / 2, t, capW, color);
            _fb.FillRectangle(x + size - t, y + (size - capW) / 2, t, capW, color);
            // 12-3-6-9 markers as small dots.
            int m = 4;
            _fb.FillRectangle(x + size / 2 - m / 2, y + 8, m, m, color);
            _fb.FillRectangle(x + size / 2 - m / 2, y + size - 8 - m, m, m, color);
            _fb.FillRectangle(x + 8, y + size / 2 - m / 2, m, m, color);
            _fb.FillRectangle(x + size - 8 - m, y + size / 2 - m / 2, m, m, color);
            // Hands: vertical (hour) and horizontal-ish (minute).
            int cx = x + size / 2;
            int cy = y + size / 2;
            _fb.FillRectangle(cx - 2, cy - size / 4, 4, size / 4, color);  // hour pointing up
            _fb.FillRectangle(cx, cy - 2, size / 3, 4, color);              // minute pointing right
        }

        void DrawStatsIcon(int x, int y, int size, Color color)
        {
            // Three vertical bars of increasing height, like a tiny bar chart.
            int barW = size / 5;
            int barGap = (size - 3 * barW) / 4;
            int baseY = y + size - 6;
            int[] heights = { size / 3, size * 2 / 3, size - 6 };
            for (int i = 0; i < 3; i++)
            {
                int barX = x + barGap + i * (barW + barGap);
                int h = heights[i];
                _fb.FillRectangle(barX, baseY - h, barW, h, color);
            }
        }

        void DrawSettingsIcon(int x, int y, int size, Color color)
        {
            // Gear-like: a centered square + 8 surrounding rectangles for teeth.
            int t = size / 4;
            int cx = x + size / 2;
            int cy = y + size / 2;
            // Center hole (just an outline).
            int hole = t / 2;
            int holeStroke = 3;
            int sb = size / 2 - 4;
            _fb.FillRectangle(cx - sb / 2, cy - sb / 2, sb, holeStroke, color);
            _fb.FillRectangle(cx - sb / 2, cy + sb / 2 - holeStroke, sb, holeStroke, color);
            _fb.FillRectangle(cx - sb / 2, cy - sb / 2, holeStroke, sb, color);
            _fb.FillRectangle(cx + sb / 2 - holeStroke, cy - sb / 2, holeStroke, sb, color);
            // Teeth: 4 cardinal stubs.
            int toothLen = (size - sb) / 2 - 2;
            int toothW = sb / 3;
            // Top + bottom
            _fb.FillRectangle(cx - toothW / 2, y, toothW, toothLen, color);
            _fb.FillRectangle(cx - toothW / 2, y + size - toothLen, toothW, toothLen, color);
            // Left + right
            _fb.FillRectangle(x, cy - toothW / 2, toothLen, toothW, color);
            _fb.FillRectangle(x + size - toothLen, cy - toothW / 2, toothLen, toothW, color);
        }

        void DrawMusicIcon(int x, int y, int size, Color color)
        {
            // Eighth note: filled head + thick stem + flag.
            int cx = x + size / 4;
            int cy = y + size - size / 4;
            int headW = size / 3;
            int headH = size / 4;
            _fb.FillRectangle(cx - headW / 2, cy - headH / 2, headW, headH, color);
            // Stem.
            int stemX = cx + headW / 2 - 4;
            int stemTopY = y + size / 6;
            int stemH = cy - stemTopY;
            _fb.FillRectangle(stemX, stemTopY, 4, stemH, color);
            // Flag.
            _fb.FillRectangle(stemX, stemTopY, size / 3, 4, color);
            _fb.FillRectangle(stemX + size / 3 - 4, stemTopY, 4, size / 4, color);
        }

        void DrawGalleryIcon(int x, int y, int size, Color color)
        {
            // Photo frame: outline rectangle + a "horizon line" + a "sun" dot.
            int t = 3;
            _fb.FillRectangle(x, y, size, t, color);
            _fb.FillRectangle(x, y + size - t, size, t, color);
            _fb.FillRectangle(x, y, t, size, color);
            _fb.FillRectangle(x + size - t, y, t, size, color);
            // Horizon line at 2/3 height.
            int horizonY = y + (size * 2) / 3;
            _fb.FillRectangle(x + 4, horizonY, size - 8, t, color);
            // Sun.
            int sunSize = size / 5;
            _fb.FillRectangle(x + size - sunSize - 8, y + 8, sunSize, sunSize, color);
        }

        void DrawWifiIcon(int x, int y, int size, Color color)
        {
            // Three concentric arcs approximated as horizontal bars of decreasing
            // width stacked on top of a dot. Bottom = dot, then small arc, mid arc,
            // big arc. Reads as a wifi signal at the panel's density.
            int cx = x + size / 2;
            int t = 4;
            // Dot at bottom-center.
            int dotSize = 6;
            _fb.FillRectangle(cx - dotSize / 2, y + size - dotSize - 2, dotSize, dotSize, color);
            // Three horizontal bars above the dot, increasing width.
            int barCount = 3;
            int gap = 6;
            int baseY = y + size - dotSize - 2 - gap;
            for (int i = 0; i < barCount; i++)
            {
                int width = (size * (i + 1)) / (barCount + 1);
                int yy = baseY - i * (t + gap);
                _fb.FillRectangle(cx - width / 2, yy - t, width, t, color);
            }
        }
    }
}
