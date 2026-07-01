#:package System.Drawing.Common@8.0.0
// Headless .tinyfnt generator for nanoFramework (CLR_GFX_Font).
// Layout (from nf-interpreter CLR_GFX_Font::CreateInstance, authoritative):
//   FontDescription (24B) : FontMetrics(16B) + ranges(u16) chars(u16) flags(u16) pad(u16)
//   BitmapDescription(12B): width(u32) height(u32) flags(u16) bpp(u8) type(u8)
//   Ranges  (ranges+1)*12 : indexOfFirstChar(u32) firstChar(u16) lastChar(u16) rangeOffset(u32)
//   Chars   (chars+1)*4   : offset(u16) marginLeft(i8) marginRight(i8)
//   Atlas                 : 1bpp, ((width+31)/32)*height*4 bytes, LSB-first bit per pixel
// v1: single ASCII range 0x20..0x7E, 1bpp (no anti-alias yet - proves DrawText first).
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

string family = args.Length > 0 ? args[0] : "Segoe UI";
int emPx = args.Length > 1 ? int.Parse(args[1]) : 22;
string outPath = args.Length > 2 ? args[2] : "spawnfont.tinyfnt";
FontStyle style = args.Length > 3 && args[3] == "bold" ? FontStyle.Bold : FontStyle.Regular;

const int firstChar = 0x20, lastChar = 0x7E;
int nChars = lastChar - firstChar + 1;

using var probe = new Bitmap(8, 8);
using var pg = Graphics.FromImage(probe);
using var font = new Font(family, emPx, style, GraphicsUnit.Pixel);
float fontH = font.GetHeight(pg);
int height = (int)Math.Ceiling(fontH);
// Ascent/descent from font metrics (design units -> pixels).
var ff = font.FontFamily;
int emH = ff.GetEmHeight(style);
int ascentDU = ff.GetCellAscent(style), descentDU = ff.GetCellDescent(style);
int ascent = (int)Math.Round(ascentDU * fontH / (ascentDU + descentDU));
int descent = height - ascent;

// Render each glyph to a tight bitmap, capture its advance width (StringFormat with no padding).
pg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit; // 1bpp-friendly, no AA
var sf = StringFormat.GenericTypographic;
sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

int[] adv = new int[nChars];
bool[][,] ink = new bool[nChars][,];
int atlasW = 0;
for (int i = 0; i < nChars; i++)
{
    string s = ((char)(firstChar + i)).ToString();
    var size = pg.MeasureString(s, font, new PointF(0, 0), sf);
    int w = Math.Max(1, (int)Math.Ceiling(size.Width));
    if (firstChar + i == ' ') w = Math.Max(4, emPx / 4); // give space a sane advance
    adv[i] = w;
    // render glyph
    var gb = new bool[w, height];
    using (var cell = new Bitmap(w + 2, height + 2))
    using (var cg = Graphics.FromImage(cell))
    {
        cg.Clear(Color.Black);
        cg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        cg.DrawString(s, font, Brushes.White, new PointF(0, 0), sf);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < w; x++)
                gb[x, y] = cell.GetPixel(Math.Min(x, cell.Width - 1), Math.Min(y, cell.Height - 1)).R > 100;
    }
    ink[i] = gb;
    atlasW += w;
}

// Build 1bpp atlas: word-packed rows, LSB-first (bit (x%32) of word[y*wiw + x/32]).
int wiw = (atlasW + 31) / 32;
uint[] atlas = new uint[wiw * height];
int[] charOffset = new int[nChars + 1];
int cx = 0;
for (int i = 0; i < nChars; i++)
{
    charOffset[i] = cx;
    var gb = ink[i];
    for (int y = 0; y < height; y++)
        for (int x = 0; x < adv[i]; x++)
            if (gb[x, y]) { int ax = cx + x; atlas[y * wiw + ax / 32] |= (1u << (ax % 32)); }
    cx += adv[i];
}
charOffset[nChars] = atlasW; // sentinel

using var ms = new MemoryStream();
var bw = new BinaryWriter(ms);
// FontDescription: FontMetrics
bw.Write((ushort)height);      // m_height
bw.Write((short)0);            // m_offset
bw.Write((short)ascent);       // m_ascent
bw.Write((short)descent);      // m_descent
bw.Write((short)0);            // m_internalLeading
bw.Write((short)0);            // m_externalLeading
bw.Write((short)(atlasW / nChars)); // m_aveCharWidth
int maxAdv = 0; foreach (var a in adv) maxAdv = Math.Max(maxAdv, a);
bw.Write((short)maxAdv);       // m_maxCharWidth
bw.Write((ushort)1);           // m_ranges
bw.Write((ushort)nChars);      // m_characters
bw.Write((ushort)0);           // m_flags (no FontEx/AA)
bw.Write((ushort)0);           // m_pad
// BitmapDescription
bw.Write((uint)atlasW);        // m_width
bw.Write((uint)height);        // m_height
bw.Write((ushort)0);           // m_flags
bw.Write((byte)1);             // m_bitsPerPixel
bw.Write((byte)0);             // m_type (nanoCLRBitmap)
// Ranges (ranges+1): range 0 + sentinel
bw.Write((uint)0); bw.Write((ushort)firstChar); bw.Write((ushort)lastChar); bw.Write((uint)0);
bw.Write((uint)nChars); bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((uint)0); // sentinel range, rangeOffset 0
// Chars (chars+1): offset,marginLeft,marginRight
for (int i = 0; i <= nChars; i++) { bw.Write((ushort)charOffset[i]); bw.Write((sbyte)0); bw.Write((sbyte)0); }
// Atlas
foreach (uint w in atlas) bw.Write(w);
bw.Flush();
File.WriteAllBytes(outPath, ms.ToArray());
Console.WriteLine($"font='{family}' emPx={emPx} height={height} ascent={ascent} descent={descent} chars={nChars} atlasW={atlasW} wiw={wiw} bytes={ms.Length} -> {outPath}");
