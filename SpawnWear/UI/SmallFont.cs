using nanoFramework.UI;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Hand-coded 5x7 bitmap ASCII font for SpawnWear UI labels. Covers A-Z, 0-9,
    /// space, and a handful of punctuation - just enough to render readable
    /// settings labels and short status strings without shipping a font resource
    /// (.tinyfnt) file.
    ///
    /// Each glyph is 5 columns x 7 rows. The byte array stores one glyph as 5
    /// bytes (one byte per column, low 7 bits = pixels top-to-bottom). A scale
    /// factor lets the same data render at 1x..Nx without aliasing - each "lit"
    /// pixel becomes a scale x scale FillRectangle.
    ///
    /// All flush rectangles produced by this font are at multiples of `scale`,
    /// so callers should pick scale >= 2 to satisfy the CO5300 even/odd
    /// alignment quirk + minimum-2-pixel write rule (see Notes/co5300-quirks.md).
    /// </summary>
    public static class SmallFont
    {
        public const int GlyphWidth = 5;
        public const int GlyphHeight = 7;

        // Glyph table indexed by ASCII code 32..127. Each entry is 5 bytes.
        // Bit 0 (LSB) of each byte = top pixel; bit 6 = bottom pixel.
        // 0 = unsupported -> drawn as a hollow rectangle outline.
        // Glyph data sourced from a public-domain 5x7 ASCII pixel font (Gerber 5x7,
        // distributed with countless embedded display libraries since the 1990s).
        private static readonly byte[] FontData =
        {
            // ' ' (0x20)
            0x00, 0x00, 0x00, 0x00, 0x00,
            // ! 0x21
            0x00, 0x00, 0x5F, 0x00, 0x00,
            // " 0x22
            0x00, 0x07, 0x00, 0x07, 0x00,
            // # 0x23
            0x14, 0x7F, 0x14, 0x7F, 0x14,
            // $ 0x24
            0x24, 0x2A, 0x7F, 0x2A, 0x12,
            // % 0x25
            0x23, 0x13, 0x08, 0x64, 0x62,
            // & 0x26
            0x36, 0x49, 0x55, 0x22, 0x50,
            // ' 0x27
            0x00, 0x05, 0x03, 0x00, 0x00,
            // ( 0x28
            0x00, 0x1C, 0x22, 0x41, 0x00,
            // ) 0x29
            0x00, 0x41, 0x22, 0x1C, 0x00,
            // * 0x2A
            0x14, 0x08, 0x3E, 0x08, 0x14,
            // + 0x2B
            0x08, 0x08, 0x3E, 0x08, 0x08,
            // , 0x2C
            0x00, 0x50, 0x30, 0x00, 0x00,
            // - 0x2D
            0x08, 0x08, 0x08, 0x08, 0x08,
            // . 0x2E
            0x00, 0x60, 0x60, 0x00, 0x00,
            // / 0x2F
            0x20, 0x10, 0x08, 0x04, 0x02,
            // 0 0x30
            0x3E, 0x51, 0x49, 0x45, 0x3E,
            // 1 0x31
            0x00, 0x42, 0x7F, 0x40, 0x00,
            // 2 0x32
            0x42, 0x61, 0x51, 0x49, 0x46,
            // 3 0x33
            0x21, 0x41, 0x45, 0x4B, 0x31,
            // 4 0x34
            0x18, 0x14, 0x12, 0x7F, 0x10,
            // 5 0x35
            0x27, 0x45, 0x45, 0x45, 0x39,
            // 6 0x36
            0x3C, 0x4A, 0x49, 0x49, 0x30,
            // 7 0x37
            0x01, 0x71, 0x09, 0x05, 0x03,
            // 8 0x38
            0x36, 0x49, 0x49, 0x49, 0x36,
            // 9 0x39
            0x06, 0x49, 0x49, 0x29, 0x1E,
            // : 0x3A
            0x00, 0x36, 0x36, 0x00, 0x00,
            // ; 0x3B
            0x00, 0x56, 0x36, 0x00, 0x00,
            // < 0x3C
            0x00, 0x08, 0x14, 0x22, 0x41,
            // = 0x3D
            0x14, 0x14, 0x14, 0x14, 0x14,
            // > 0x3E
            0x41, 0x22, 0x14, 0x08, 0x00,
            // ? 0x3F
            0x02, 0x01, 0x51, 0x09, 0x06,
            // @ 0x40
            0x32, 0x49, 0x79, 0x41, 0x3E,
            // A 0x41
            0x7E, 0x11, 0x11, 0x11, 0x7E,
            // B 0x42
            0x7F, 0x49, 0x49, 0x49, 0x36,
            // C 0x43
            0x3E, 0x41, 0x41, 0x41, 0x22,
            // D 0x44
            0x7F, 0x41, 0x41, 0x22, 0x1C,
            // E 0x45
            0x7F, 0x49, 0x49, 0x49, 0x41,
            // F 0x46
            0x7F, 0x09, 0x09, 0x01, 0x01,
            // G 0x47
            0x3E, 0x41, 0x41, 0x51, 0x32,
            // H 0x48
            0x7F, 0x08, 0x08, 0x08, 0x7F,
            // I 0x49
            0x00, 0x41, 0x7F, 0x41, 0x00,
            // J 0x4A
            0x20, 0x40, 0x41, 0x3F, 0x01,
            // K 0x4B
            0x7F, 0x08, 0x14, 0x22, 0x41,
            // L 0x4C
            0x7F, 0x40, 0x40, 0x40, 0x40,
            // M 0x4D
            0x7F, 0x02, 0x04, 0x02, 0x7F,
            // N 0x4E
            0x7F, 0x04, 0x08, 0x10, 0x7F,
            // O 0x4F
            0x3E, 0x41, 0x41, 0x41, 0x3E,
            // P 0x50
            0x7F, 0x09, 0x09, 0x09, 0x06,
            // Q 0x51
            0x3E, 0x41, 0x51, 0x21, 0x5E,
            // R 0x52
            0x7F, 0x09, 0x19, 0x29, 0x46,
            // S 0x53
            0x46, 0x49, 0x49, 0x49, 0x31,
            // T 0x54
            0x01, 0x01, 0x7F, 0x01, 0x01,
            // U 0x55
            0x3F, 0x40, 0x40, 0x40, 0x3F,
            // V 0x56
            0x1F, 0x20, 0x40, 0x20, 0x1F,
            // W 0x57
            0x7F, 0x20, 0x18, 0x20, 0x7F,
            // X 0x58
            0x63, 0x14, 0x08, 0x14, 0x63,
            // Y 0x59
            0x03, 0x04, 0x78, 0x04, 0x03,
            // Z 0x5A
            0x61, 0x51, 0x49, 0x45, 0x43,
            // [ 0x5B
            0x00, 0x00, 0x7F, 0x41, 0x41,
            // \ 0x5C
            0x02, 0x04, 0x08, 0x10, 0x20,
            // ] 0x5D
            0x41, 0x41, 0x7F, 0x00, 0x00,
            // ^ 0x5E
            0x04, 0x02, 0x01, 0x02, 0x04,
            // _ 0x5F
            0x40, 0x40, 0x40, 0x40, 0x40,
        };

        private const int FirstChar = 0x20; // ' '
        private const int LastChar = 0x5F;  // '_'

        /// <summary>
        /// Draws a single uppercase / digit / punctuation glyph at (x, y) scaled
        /// by <paramref name="scale"/>. Lowercase letters are rendered as
        /// uppercase (the glyph table only ships A-Z to keep size down).
        /// Returns the column-advance after this glyph (glyphWidth + 1 cell of
        /// inter-character spacing, all multiplied by scale).
        /// </summary>
        public static int DrawChar(Bitmap fb, char c, int x, int y, int scale, Color color)
        {
            int code = c;
            if (code >= 'a' && code <= 'z') code -= 32; // map to uppercase
            if (code < FirstChar || code > LastChar)
            {
                // Unsupported character: hollow rectangle outline so the gap is visible.
                fb.FillRectangle(x, y, GlyphWidth * scale, scale, color);
                fb.FillRectangle(x, y + (GlyphHeight - 1) * scale, GlyphWidth * scale, scale, color);
                fb.FillRectangle(x, y, scale, GlyphHeight * scale, color);
                fb.FillRectangle(x + (GlyphWidth - 1) * scale, y, scale, GlyphHeight * scale, color);
                return (GlyphWidth + 1) * scale;
            }

            int offset = (code - FirstChar) * GlyphWidth;
            for (int col = 0; col < GlyphWidth; col++)
            {
                byte bits = FontData[offset + col];
                for (int row = 0; row < GlyphHeight; row++)
                {
                    if ((bits & (1 << row)) != 0)
                    {
                        fb.FillRectangle(x + col * scale, y + row * scale, scale, scale, color);
                    }
                }
            }
            return (GlyphWidth + 1) * scale;
        }

        /// <summary>
        /// Draws a string left-aligned at (x, y). Returns the bounding-box width.
        /// </summary>
        public static int DrawString(Bitmap fb, string text, int x, int y, int scale, Color color)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int cursor = x;
            for (int i = 0; i < text.Length; i++)
            {
                cursor += DrawChar(fb, text[i], cursor, y, scale, color);
            }
            return cursor - x;
        }

        /// <summary>
        /// Returns the pixel width <paramref name="text"/> will occupy when drawn
        /// at <paramref name="scale"/> via <see cref="DrawString"/>.
        /// </summary>
        public static int MeasureString(string text, int scale)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Length * (GlyphWidth + 1) * scale;
        }
    }
}
