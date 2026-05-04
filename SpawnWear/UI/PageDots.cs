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
        const int InactiveDotSize = 10;
        const int ActiveDotWidth = 28;  // pill shape - 2.8x as wide as inactive
        const int ActiveDotHeight = 10;
        const int DotGap = 12;
        const int BottomMargin = 24;
        const int CornerRadius = 4;     // notch size for rounded pill caps

        public static void Render(Bitmap fb, int panelWidth, int panelHeight, int activeIndex, int total)
        {
            if (total <= 1) return;

            // Width = sum of inactive dots + active pill + gaps
            int totalWidth =
                (total - 1) * InactiveDotSize +
                ActiveDotWidth +
                (total - 1) * DotGap;
            int startX = (panelWidth - totalWidth) / 2;
            int y = panelHeight - BottomMargin - ActiveDotHeight;

            int cursor = startX;
            for (int i = 0; i < total; i++)
            {
                if (i == activeIndex)
                {
                    // Active = pill shape (wide rounded rectangle).
                    fb.FillRectangle(cursor, y, ActiveDotWidth, ActiveDotHeight, Color.White);
                    // Notch the corners so it reads as a pill.
                    fb.FillRectangle(cursor, y, 1, 1, Color.Black);
                    fb.FillRectangle(cursor + ActiveDotWidth - 1, y, 1, 1, Color.Black);
                    fb.FillRectangle(cursor, y + ActiveDotHeight - 1, 1, 1, Color.Black);
                    fb.FillRectangle(cursor + ActiveDotWidth - 1, y + ActiveDotHeight - 1, 1, 1, Color.Black);
                    cursor += ActiveDotWidth + DotGap;
                }
                else
                {
                    // Inactive = small filled circle (square at this resolution),
                    // dimmed gray so it doesn't compete with the active pill.
                    int dotY = y + (ActiveDotHeight - InactiveDotSize) / 2;
                    fb.FillRectangle(cursor, dotY, InactiveDotSize, InactiveDotSize, Color.FromArgb(110, 110, 110));
                    // Notch corners on inactive too.
                    fb.FillRectangle(cursor, dotY, 1, 1, Color.Black);
                    fb.FillRectangle(cursor + InactiveDotSize - 1, dotY, 1, 1, Color.Black);
                    fb.FillRectangle(cursor, dotY + InactiveDotSize - 1, 1, 1, Color.Black);
                    fb.FillRectangle(cursor + InactiveDotSize - 1, dotY + InactiveDotSize - 1, 1, 1, Color.Black);
                    cursor += InactiveDotSize + DotGap;
                }
            }
        }
    }
}
