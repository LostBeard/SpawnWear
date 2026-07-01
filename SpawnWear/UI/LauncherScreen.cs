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

            // Native rounded tile. FillRoundRectangle is supported on this CO5300 nf-interpreter
            // build (verified via the GFX PROBE screen 2026-06-30) - true smooth corners instead
            // of the old FillRectangle staircase mask + 16-band gradient loop. Flat Material-style
            // fill: clean, seam-free, and AMOLED-friendly. A subtle darker inner base under the
            // face gives a hint of depth (the 2px offset reads as a soft bottom-right edge).
            int radius = _tileSize / 6;
            int faceR = placeholder ? 82 : tile.Background.R;
            int faceG = placeholder ? 82 : tile.Background.G;
            int faceB = placeholder ? 82 : tile.Background.B;
            Color face = Color.FromArgb(faceR, faceG, faceB);
            Color edge = Color.FromArgb((faceR * 55) / 100, (faceG * 55) / 100, (faceB * 55) / 100);
            _fb.FillRoundRectangle(x, y, _tileSize, _tileSize, radius, radius, edge);
            _fb.FillRoundRectangle(x, y, _tileSize, _tileSize - 3, radius, radius, face);

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

        // Rounded red badge (pill/circle) with a small white digit; "9+" if count > 9.
        void DrawBadge(int x, int y, int count)
        {
            int size = 22;
            Color badge = Color.Red;
            Color text = Color.White;
            // A radius of size/2 makes the rounded rect a clean circle - a real notification bubble.
            _fb.FillRoundRectangle(x, y, size, size, size / 2, size / 2, badge);

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

        // ----- Icon primitives (native DrawEllipse / DrawLine / FillRoundRectangle) -----

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

        // Filled circle helper (DrawEllipse's fill path uses a solid start==end gradient).
        void FillCircle(int cx, int cy, int r, Color color) => FillEllipse(cx, cy, r, r, color);

        // Stroked circle helper (2px ring for visibility at watch density).
        void RingCircle(int cx, int cy, int r, Color color)
        {
            _fb.DrawEllipse(color, cx, cy, r, r);
            if (r > 2) _fb.DrawEllipse(color, cx, cy, r - 1, r - 1);
        }

        void DrawClockIcon(int x, int y, int size, Color color)
        {
            int cx = x + size / 2, cy = y + size / 2;
            int r = size / 2 - 2;
            RingCircle(cx, cy, r, color);                       // real circular face
            _fb.DrawLine(color, 3, cx, cy, cx, cy - (r * 6) / 10);   // hour hand up
            _fb.DrawLine(color, 3, cx, cy, cx + (r * 7) / 10, cy);   // minute hand right
            FillCircle(cx, cy, 2, color);                       // hub
        }

        void DrawStatsIcon(int x, int y, int size, Color color)
        {
            // Rounded bar chart - three bars of increasing height.
            int barW = size / 5;
            int gap = (size - 3 * barW) / 4;
            int baseY = y + size - 4;
            int[] heights = { size / 3, (size * 2) / 3, size - 6 };
            int cr = barW / 3;
            for (int i = 0; i < 3; i++)
            {
                int barX = x + gap + i * (barW + gap);
                int h = heights[i];
                _fb.FillRoundRectangle(barX, baseY - h, barW, h, cr, cr, color);
            }
        }

        void DrawSettingsIcon(int x, int y, int size, Color color)
        {
            int cx = x + size / 2, cy = y + size / 2;
            int rOuter = size / 2 - 3;
            RingCircle(cx, cy, rOuter, color);       // gear body ring
            RingCircle(cx, cy, size / 6, color);     // center hole
            // Four cardinal + four diagonal teeth as small rounded stubs (cos45 ~ 0.7).
            int reach = rOuter - 1;
            int diag = (reach * 7) / 10;
            int tr = size / 12; if (tr < 3) tr = 3;
            FillCircle(cx, cy - reach, tr, color);
            FillCircle(cx, cy + reach, tr, color);
            FillCircle(cx - reach, cy, tr, color);
            FillCircle(cx + reach, cy, tr, color);
            FillCircle(cx - diag, cy - diag, tr, color);
            FillCircle(cx + diag, cy - diag, tr, color);
            FillCircle(cx - diag, cy + diag, tr, color);
            FillCircle(cx + diag, cy + diag, tr, color);
        }

        void DrawMusicIcon(int x, int y, int size, Color color)
        {
            // Eighth note: real elliptical head + line stem + flag.
            int headRx = size / 4, headRy = size / 5;
            int hcx = x + headRx + 2, hcy = y + size - headRy - 2;
            FillEllipse(hcx, hcy, headRx, headRy, color);
            int stemX = hcx + headRx - 2;
            int stemTop = y + 2;
            _fb.DrawLine(color, 3, stemX, hcy - headRy, stemX, stemTop);          // stem
            _fb.DrawLine(color, 3, stemX, stemTop, stemX + size / 3, stemTop + size / 5); // flag
        }

        void FillEllipse(int cx, int cy, int rx, int ry, Color color) =>
            _fb.DrawEllipse(color, 1, cx, cy, rx, ry, color, 0, 0, color, 0, 0);

        void DrawGalleryIcon(int x, int y, int size, Color color)
        {
            // Rounded photo frame + sun (real circle) + mountain (lines).
            _fb.DrawRoundRectangle(x, y, size, size, 3, size / 6, size / 6, color);
            FillCircle(x + (size * 3) / 4, y + size / 4, size / 9, color);        // sun
            int baseY = y + size - 5;
            _fb.DrawLine(color, 3, x + 4, baseY, x + (size * 2) / 5, y + size / 2);
            _fb.DrawLine(color, 3, x + (size * 2) / 5, y + size / 2, x + size - 4, baseY);
        }

        void DrawWifiIcon(int x, int y, int size, Color color)
        {
            // Real wifi fan: concentric rings centered on a node near the bottom of the box,
            // clipped to the box so only their UPPER arcs show (the classic radiating-signal
            // shape). Clip is reset to the full framebuffer afterward so later tiles aren't
            // restricted. NOTE: SetClippingRectangle is not yet probe-verified on this build; if it's
            // a no-op the rings just draw as full circles (still legible) - confirm on hardware.
            int cx = x + size / 2;
            int cy = y + size - 5;
            _fb.SetClippingRectangle(x, y, size, size - 2);
            for (int i = 3; i >= 1; i--)
            {
                int r = (i * (size - 6)) / 3;
                _fb.DrawEllipse(color, cx, cy, r, r);
                if (r > 3) _fb.DrawEllipse(color, cx, cy, r - 1, r - 1); // thicken the arc
            }
            _fb.SetClippingRectangle(0, 0, _panelWidth, _panelHeight);
            FillCircle(cx, cy, 3, color); // node
        }
    }
}
