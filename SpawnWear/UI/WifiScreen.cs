using nanoFramework.UI;
using SpawnWear.AppContracts;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// WiFi status screen. Read-only V1; Phase 4 Settings → WiFi adds
    /// connect / disconnect / SSID-list scan once an on-screen keyboard
    /// exists.
    ///
    /// Power model: full repaint only on Invalidate / OnResume. Per-tick
    /// just refreshes the status bar.
    /// </summary>
    public class WifiScreen : IScreen
    {
        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        readonly IServiceHost _services;

        int _pageDotIndex = -1;
        int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        bool _needsRepaint = true;

        const int LabelScale = 3;
        const int LabelColW = 110;
        const int RowGap = 10;
        const int MarginX = 28;

        public WifiScreen(Bitmap framebuffer, int panelWidth, int panelHeight, IServiceHost services)
        {
            _fb = framebuffer;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
            _services = services;
        }

        public void Invalidate() { _needsRepaint = true; }
        public void OnResume() => Invalidate();
        public void OnPause() { }
        public bool OnTap(int x, int y) => false;

        public void Tick()
        {
            if (_needsRepaint)
            {
                FullRepaint();
                _needsRepaint = false;
                return;
            }
            _statusBar?.Render(force: false);
        }

        void FullRepaint()
        {
            var wifi = _services != null ? _services.GetWifi() : null;
            bool connected = wifi != null && wifi.IsConnected;
            string ssid = connected ? wifi.ConnectedSsid : "---";
            string ip = connected ? wifi.IpAddress : "---";

            int contentTop = _statusBar != null ? StatusBar.ReservedHeight : 0;

            _fb.Clear();
            _fb.FillRectangle(0, 0, _panelWidth, _panelHeight, Color.Black);

            // Centered big signal-bar glyph at the top of the content area.
            int iconY = contentTop + 24;
            int iconBoxSize = 96;
            int iconX = (_panelWidth - iconBoxSize) / 2;
            DrawBigSignalBars(iconX, iconY, iconBoxSize, connected ? 4 : 0);

            // Status string under the icon.
            string status = connected ? "CONNECTED" : "DISCONNECTED";
            Color statusColor = connected ? Color.LimeGreen : Color.FromArgb(180, 70, 70);
            int statusW = SmallFont.MeasureString(status, LabelScale);
            int statusY = iconY + iconBoxSize + 12;
            SmallFont.DrawString(_fb, status, (_panelWidth - statusW) / 2, statusY, LabelScale, statusColor);

            // Detail rows below.
            int rowsTop = statusY + SmallFont.GlyphHeight * LabelScale + 32;
            int rowH = SmallFont.GlyphHeight * LabelScale;
            int rowStride = rowH + RowGap;

            DrawRow(MarginX, rowsTop, "SSID", ssid);
            DrawRow(MarginX, rowsTop + rowStride, "IP", ip);
            DrawRow(MarginX, rowsTop + 2 * rowStride, "MODE", "STATION");

            if (_pageDotCount > 1)
            {
                PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            }

            _fb.Flush();
            _statusBar?.Render(force: true);
        }

        void DrawRow(int x, int y, string label, string value)
        {
            SmallFont.DrawString(_fb, label, x, y, LabelScale, Color.FromArgb(170, 170, 170));
            SmallFont.DrawString(_fb, value == null ? "" : value, x + LabelColW, y, LabelScale, Color.White);
        }

        void DrawBigSignalBars(int x, int y, int size, int bars)
        {
            int gap = 8;
            int barW = (size - 5 * gap) / 4;
            int baseY = y + size - 4;
            for (int i = 0; i < 4; i++)
            {
                int barH = ((i + 1) * (size - 12)) / 4;
                int barX = x + gap + i * (barW + gap);
                int barY = baseY - barH;
                if (i < bars)
                {
                    _fb.FillRectangle(barX, barY, barW, barH, Color.White);
                }
                else
                {
                    int t = 2;
                    Color outline = Color.FromArgb(70, 70, 70);
                    _fb.FillRectangle(barX, barY, barW, t, outline);
                    _fb.FillRectangle(barX, barY + barH - t, barW, t, outline);
                    _fb.FillRectangle(barX, barY, t, barH, outline);
                    _fb.FillRectangle(barX + barW - t, barY, t, barH, outline);
                }
            }
        }
    }
}
