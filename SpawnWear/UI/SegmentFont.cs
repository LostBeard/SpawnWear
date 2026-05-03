using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Code-only 7-segment digit renderer for the SpawnWear watch face V1.
    ///
    /// Avoids shipping a font resource (.tinyfnt) - the pattern is hand-coded as
    /// a per-digit segment-mask plus rectangle geometry, drawn directly with
    /// <see cref="Bitmap.FillRectangle"/>. Designed for the HH:MM:SS clock
    /// readout where only digits 0-9 and the colon ':' are needed.
    ///
    /// Each digit uses the canonical 7-segment layout:
    ///
    ///   aaa
    ///  b   c
    ///  b   c
    ///   ggg
    ///  d   e
    ///  d   e
    ///   fff
    ///
    /// Segments are referenced by bit index in <see cref="DigitMasks"/>:
    ///   bit 0 = a (top)        bit 1 = b (upper-left)
    ///   bit 2 = c (upper-right) bit 3 = g (middle)
    ///   bit 4 = d (lower-left)  bit 5 = e (lower-right)
    ///   bit 6 = f (bottom)
    ///
    /// Power note: every "off" pixel on the AMOLED draws ~zero current. Drawing
    /// digits in white on a black background means the lit pixel count IS the
    /// power budget for the face. A typical HH:MM:SS readout at this size lights
    /// ~12% of the digits-region pixels, which itself is a small fraction of
    /// the panel.
    /// </summary>
    public static class SegmentFont
    {
        // 7-segment bitmask per digit 0-9. Bits map to segments per the comment above.
        private static readonly byte[] DigitMasks =
        {
            0b01110111, // 0 - a b c d e f
            0b00100100, // 1 - c e
            0b01011101, // 2 - a c g d f
            0b01101101, // 3 - a c g e f
            0b00101110, // 4 - b c g e
            0b01101011, // 5 - a b g e f
            0b01111011, // 6 - a b g d e f
            0b00100101, // 7 - a c e
            0b01111111, // 8 - a b c g d e f
            0b01101111, // 9 - a b c g e f
        };

        /// <summary>
        /// Draws a single digit (0-9) at the specified top-left coordinate. The bounding
        /// box is <paramref name="width"/> x <paramref name="height"/> with all segments
        /// drawn at <paramref name="thickness"/> pixels. Caller is responsible for the
        /// background fill.
        /// </summary>
        public static void DrawDigit(Bitmap fb, int digit, int x, int y, int width, int height, int thickness, Color color)
        {
            if (digit < 0 || digit > 9) return;

            byte mask = DigitMasks[digit];
            int midY = y + (height / 2);
            int rightX = x + width - thickness;
            int bottomY = y + height - thickness;

            // a (top horizontal)
            if ((mask & 0x01) != 0) fb.FillRectangle(x, y, width, thickness, color);
            // f (bottom horizontal)
            if ((mask & 0x40) != 0) fb.FillRectangle(x, bottomY, width, thickness, color);
            // g (middle horizontal)
            if ((mask & 0x08) != 0) fb.FillRectangle(x, midY - (thickness / 2), width, thickness, color);
            // b (upper-left vertical)
            if ((mask & 0x02) != 0) fb.FillRectangle(x, y, thickness, (height / 2) + (thickness / 2), color);
            // c (upper-right vertical)
            if ((mask & 0x04) != 0) fb.FillRectangle(rightX, y, thickness, (height / 2) + (thickness / 2), color);
            // d (lower-left vertical)
            if ((mask & 0x10) != 0) fb.FillRectangle(x, midY - (thickness / 2), thickness, (height / 2) + (thickness / 2), color);
            // e (lower-right vertical)
            if ((mask & 0x20) != 0) fb.FillRectangle(rightX, midY - (thickness / 2), thickness, (height / 2) + (thickness / 2), color);
        }

        /// <summary>
        /// Draws a colon ':' separator centered horizontally in a glyph-sized box. The
        /// dot diameter is <paramref name="thickness"/>; the two dots sit at 1/3 and 2/3
        /// of the height so the colon visually tracks the seven-segment glyph height.
        /// </summary>
        public static void DrawColon(Bitmap fb, int x, int y, int width, int height, int thickness, Color color)
        {
            int dotX = x + (width - thickness) / 2;
            int topDotY = y + (height / 3) - (thickness / 2);
            int bottomDotY = y + (2 * height / 3) - (thickness / 2);
            fb.FillRectangle(dotX, topDotY, thickness, thickness, color);
            fb.FillRectangle(dotX, bottomDotY, thickness, thickness, color);
        }

        /// <summary>
        /// Draws a fixed-width HH:MM:SS string. Caller picks the digit cell width and
        /// height; total render width is 6*digitWidth + 2*colonWidth + 7*spacing. Returns
        /// the bounding rectangle so the caller can pass it back to
        /// <c>fb.Flush(x, y, w, h)</c> for partial-screen blit.
        /// </summary>
        public static void DrawHhMmSs(
            Bitmap fb,
            int hours, int minutes, int seconds,
            int x, int y,
            int digitWidth, int digitHeight,
            int colonWidth, int spacing,
            int thickness,
            Color color)
        {
            int cursor = x;
            DrawTwoDigits(fb, hours, cursor, y, digitWidth, digitHeight, spacing, thickness, color);
            cursor += 2 * (digitWidth + spacing);
            DrawColon(fb, cursor, y, colonWidth, digitHeight, thickness, color);
            cursor += colonWidth + spacing;
            DrawTwoDigits(fb, minutes, cursor, y, digitWidth, digitHeight, spacing, thickness, color);
            cursor += 2 * (digitWidth + spacing);
            DrawColon(fb, cursor, y, colonWidth, digitHeight, thickness, color);
            cursor += colonWidth + spacing;
            DrawTwoDigits(fb, seconds, cursor, y, digitWidth, digitHeight, spacing, thickness, color);
        }

        private static void DrawTwoDigits(Bitmap fb, int value, int x, int y, int digitWidth, int digitHeight, int spacing, int thickness, Color color)
        {
            int tens = (value / 10) % 10;
            int ones = value % 10;
            DrawDigit(fb, tens, x, y, digitWidth, digitHeight, thickness, color);
            DrawDigit(fb, ones, x + digitWidth + spacing, y, digitWidth, digitHeight, thickness, color);
        }

        /// <summary>
        /// Computes the bounding rectangle width that <see cref="DrawHhMmSs"/> will
        /// occupy with the given parameters. Useful for centering on the panel.
        /// </summary>
        public static int HhMmSsWidth(int digitWidth, int colonWidth, int spacing)
        {
            // 6 digits + 2 colons + 7 inter-glyph spacings (between every pair).
            return (6 * digitWidth) + (2 * colonWidth) + (7 * spacing);
        }
    }
}
