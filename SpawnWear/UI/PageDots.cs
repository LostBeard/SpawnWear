using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Small horizontal row of dots rendered at the bottom of a screen as a
    /// visual indicator of "which page am I on" - one dot per registered
    /// screen, with the active one filled solid white and the others drawn
    /// as 1-pixel outline rings. Standard pattern from phone home screens.
    ///
    /// Caller passes the panel size + current/total page indices and the
    /// helper figures out the layout. Renders straight to the supplied
    /// framebuffer; doesn't issue a Flush on its own (the screen's own
    /// full repaint covers it).
    /// </summary>
    public static class PageDots
    {
        const int DotDiameter = 10;
        const int DotGap = 14;
        const int BottomMargin = 24;

        public static void Render(Bitmap fb, int panelWidth, int panelHeight, int activeIndex, int total)
        {
            if (total <= 1) return;

            int totalWidth = total * DotDiameter + (total - 1) * DotGap;
            int startX = (panelWidth - totalWidth) / 2;
            int y = panelHeight - BottomMargin - DotDiameter;

            for (int i = 0; i < total; i++)
            {
                int x = startX + i * (DotDiameter + DotGap);
                if (i == activeIndex)
                {
                    // Filled circle approximated by a square - the panel is
                    // dense enough that 10x10 reads as a dot at arm's length.
                    fb.FillRectangle(x, y, DotDiameter, DotDiameter, Color.White);
                }
                else
                {
                    // Outline ring: 4 thin sides, leaving the center black.
                    int t = 2;
                    fb.FillRectangle(x, y, DotDiameter, t, Color.White);
                    fb.FillRectangle(x, y + DotDiameter - t, DotDiameter, t, Color.White);
                    fb.FillRectangle(x, y, t, DotDiameter, Color.White);
                    fb.FillRectangle(x + DotDiameter - t, y, t, DotDiameter, Color.White);
                }
            }
        }
    }
}
