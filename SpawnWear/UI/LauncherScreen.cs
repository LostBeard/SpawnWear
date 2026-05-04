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
        public delegate void LaunchApp(int navigatorIndex);

        public class Tile
        {
            public string Label;
            public int TargetScreenIndex;
            public IconKind Icon;
            // Notification badge count. 0 = no badge. >99 displays as ">9" since
            // the small red bubble can't fit three digits at this tile size.
            // Phase 3 will wire this to a NotificationService that aggregates
            // events from BLE + system + per-app sources.
            public int BadgeCount;
        }

        public enum IconKind { Clock, Stats, Settings }

        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        readonly Tile[] _tiles;
        readonly LaunchApp _launch;
        StatusBar _statusBar;
        int _pageDotIndex = -1;
        int _pageDotCount;

        // Tile geometry (computed in Layout).
        int _tileSize;
        int _tileGap;
        int _tilesY;
        int _tilesStartX;

        public LauncherScreen(Bitmap fb, int panelWidth, int panelHeight, Tile[] tiles, LaunchApp launch)
        {
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _tiles = tiles;
            _launch = launch;
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

            // Tiles row.
            for (int i = 0; i < _tiles.Length; i++)
            {
                int x = _tilesStartX + i * (_tileSize + _tileGap);
                DrawTile(x, _tilesY, _tiles[i]);
            }

            // Page dots.
            if (_pageDotCount > 1)
            {
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            }

            _fb.Flush();
            _statusBar?.Render(force: true);
        }

        public void OnResume() => Invalidate();
        public void OnPause() { }

        public bool OnTap(int x, int y)
        {
            // Re-run layout in case Invalidate hasn't been called yet.
            if (_tileSize == 0) Layout();

            if (y < _tilesY || y >= _tilesY + _tileSize) return false;
            int relX = x - _tilesStartX;
            if (relX < 0) return false;
            int slot = relX / (_tileSize + _tileGap);
            if (slot < 0 || slot >= _tiles.Length) return false;
            int slotStart = slot * (_tileSize + _tileGap);
            int slotEnd = slotStart + _tileSize;
            if (relX < slotStart || relX >= slotEnd) return false; // landed in the gap

            var tile = _tiles[slot];
            System.Diagnostics.Debug.WriteLine("[Launcher] launching " + tile.Label + " -> screen " + tile.TargetScreenIndex);
            _launch?.Invoke(tile.TargetScreenIndex);
            return true;
        }

        // ----- Layout -----

        void Layout()
        {
            int statusBarH = _statusBar != null ? StatusBar.ReservedHeight : 0;
            int safeBottomReserved = 80; // page dots + footer breathing room
            int availableH = _panelHeight - statusBarH - safeBottomReserved;
            // Corner-rounding safe area: don't push tiles all the way to the edges
            int safeInset = 50;
            int availableW = _panelWidth - 2 * safeInset;

            // 3 tiles + 2 gaps. Pick a tile size that leaves a comfortable label
            // (label ~ 40 px) below each tile inside the row.
            int tileLabelH = 40;
            int rowH = availableH;
            int tileSizeFromHeight = rowH - tileLabelH;
            int tileSizeFromWidth = (availableW - 2 * 24) / _tiles.Length; // 24 px gaps
            _tileSize = tileSizeFromHeight < tileSizeFromWidth ? tileSizeFromHeight : tileSizeFromWidth;
            if (_tileSize > 130) _tileSize = 130;
            _tileGap = 24;

            int totalW = _tiles.Length * _tileSize + (_tiles.Length - 1) * _tileGap;
            _tilesStartX = (_panelWidth - totalW) / 2;
            _tilesY = statusBarH + (availableH - _tileSize - tileLabelH) / 2;
        }

        // ----- Tile rendering -----

        void DrawTile(int x, int y, Tile tile)
        {
            // Outline square as the tile background.
            int t = 3;
            _fb.FillRectangle(x, y, _tileSize, t, Color.White);
            _fb.FillRectangle(x, y + _tileSize - t, _tileSize, t, Color.White);
            _fb.FillRectangle(x, y, t, _tileSize, Color.White);
            _fb.FillRectangle(x + _tileSize - t, y, t, _tileSize, Color.White);

            // Icon area = inner ~70% square centered.
            int iconBoxSize = (_tileSize * 7) / 10;
            int iconX = x + (_tileSize - iconBoxSize) / 2;
            int iconY = y + (_tileSize - iconBoxSize) / 2;

            switch (tile.Icon)
            {
                case IconKind.Clock: DrawClockIcon(iconX, iconY, iconBoxSize); break;
                case IconKind.Stats: DrawStatsIcon(iconX, iconY, iconBoxSize); break;
                case IconKind.Settings: DrawSettingsIcon(iconX, iconY, iconBoxSize); break;
            }

            // Notification badge (top-right corner of the tile).
            if (tile.BadgeCount > 0)
            {
                DrawBadge(x + _tileSize - 26, y - 6, tile.BadgeCount);
            }

            // Label centered under the tile.
            int labelScale = 3;
            int labelW = SmallFont.MeasureString(tile.Label, labelScale);
            int labelX = x + (_tileSize - labelW) / 2;
            int labelY = y + _tileSize + 8;
            SmallFont.DrawString(_fb, tile.Label, labelX, labelY, labelScale, Color.White);
        }

        // Filled red badge with a small white digit; "9+" if count > 9.
        void DrawBadge(int x, int y, int count)
        {
            int size = 32;
            // Solid red square (Phase 3 swaps to circle once we have per-pixel ops).
            _fb.FillRectangle(x, y, size, size, Color.Red);
            // White outline so the badge pops against any tile color.
            int t = 2;
            _fb.FillRectangle(x, y, size, t, Color.White);
            _fb.FillRectangle(x, y + size - t, size, t, Color.White);
            _fb.FillRectangle(x, y, t, size, Color.White);
            _fb.FillRectangle(x + size - t, y, t, size, Color.White);

            string display = count > 9 ? "9+" : ((char)('0' + count)).ToString();
            int scale = 3;
            int textW = SmallFont.MeasureString(display, scale);
            int textH = SmallFont.GlyphHeight * scale;
            int textX = x + (size - textW) / 2;
            int textY = y + (size - textH) / 2;
            SmallFont.DrawString(_fb, display, textX, textY, scale, Color.White);
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

        void DrawClockIcon(int x, int y, int size)
        {
            // Outline circle approximation: 4 corners with rectangles, plus a
            // hands cross in the middle.
            int t = 4;
            // Top / bottom thick caps to suggest curvature.
            int capW = size / 2;
            _fb.FillRectangle(x + (size - capW) / 2, y, capW, t, Color.White);
            _fb.FillRectangle(x + (size - capW) / 2, y + size - t, capW, t, Color.White);
            _fb.FillRectangle(x, y + (size - capW) / 2, t, capW, Color.White);
            _fb.FillRectangle(x + size - t, y + (size - capW) / 2, t, capW, Color.White);
            // 12-3-6-9 markers as small dots.
            int m = 4;
            _fb.FillRectangle(x + size / 2 - m / 2, y + 8, m, m, Color.White);
            _fb.FillRectangle(x + size / 2 - m / 2, y + size - 8 - m, m, m, Color.White);
            _fb.FillRectangle(x + 8, y + size / 2 - m / 2, m, m, Color.White);
            _fb.FillRectangle(x + size - 8 - m, y + size / 2 - m / 2, m, m, Color.White);
            // Hands: vertical (hour) and horizontal-ish (minute).
            int cx = x + size / 2;
            int cy = y + size / 2;
            _fb.FillRectangle(cx - 2, cy - size / 4, 4, size / 4, Color.White);  // hour pointing up
            _fb.FillRectangle(cx, cy - 2, size / 3, 4, Color.White);              // minute pointing right
        }

        void DrawStatsIcon(int x, int y, int size)
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
                _fb.FillRectangle(barX, baseY - h, barW, h, Color.White);
            }
        }

        void DrawSettingsIcon(int x, int y, int size)
        {
            // Gear-like: a centered square + 8 surrounding rectangles for teeth.
            int t = size / 4;
            int cx = x + size / 2;
            int cy = y + size / 2;
            // Center hole (just an outline).
            int hole = t / 2;
            int holeStroke = 3;
            int sb = size / 2 - 4;
            _fb.FillRectangle(cx - sb / 2, cy - sb / 2, sb, holeStroke, Color.White);
            _fb.FillRectangle(cx - sb / 2, cy + sb / 2 - holeStroke, sb, holeStroke, Color.White);
            _fb.FillRectangle(cx - sb / 2, cy - sb / 2, holeStroke, sb, Color.White);
            _fb.FillRectangle(cx + sb / 2 - holeStroke, cy - sb / 2, holeStroke, sb, Color.White);
            // Teeth: 4 cardinal stubs.
            int toothLen = (size - sb) / 2 - 2;
            int toothW = sb / 3;
            // Top + bottom
            _fb.FillRectangle(cx - toothW / 2, y, toothW, toothLen, Color.White);
            _fb.FillRectangle(cx - toothW / 2, y + size - toothLen, toothW, toothLen, Color.White);
            // Left + right
            _fb.FillRectangle(x, cy - toothW / 2, toothLen, toothW, Color.White);
            _fb.FillRectangle(x + size - toothLen, cy - toothW / 2, toothLen, toothW, Color.White);
        }
    }
}
