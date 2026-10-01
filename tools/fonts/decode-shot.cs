#:package System.Drawing.Common@8.0.0
using System.Drawing;
using System.Drawing.Imaging;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")] // System.Drawing: a Windows dev tool

// usage: dotnet run tools/fonts/decode-shot.cs <shot.bin> [out.png]   (shot.bin = curl http://<watch>/screenshot.bin)
string inPath = args.Length > 0 ? args[0] : "shot.bin";
string outPng = args.Length > 1 ? args[1] : Path.ChangeExtension(inPath, ".png");
byte[] all = File.ReadAllBytes(inPath);
// Header: ASCII "w=W h=H[ fmt=bgra32]\n". No fmt = RGB565 big-endian (the slow per-pixel path);
// fmt=bgra32 = the watch's native 32 bpp GetBitmap() bytes (0xAARRGGBB little-endian -> B G R A).
int nl = Array.IndexOf(all, (byte)'\n');
string hdr = System.Text.Encoding.ASCII.GetString(all, 0, nl);
int w = 0, h = 0;
string fmt = "rgb565";
foreach (var p in hdr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
{
    if (p.StartsWith("w=")) w = int.Parse(p[2..]);
    else if (p.StartsWith("h=")) h = int.Parse(p[2..]);
    else if (p.StartsWith("fmt=")) fmt = p[4..];
}
int bpp = fmt == "bgra32" ? 4 : 2;
int off = nl + 1;
Console.WriteLine($"header='{hdr}' w={w} h={h} fmt={fmt} pixelBytes={all.Length - off} expected={w * h * bpp}");
using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
for (int y = 0; y < h; y++)
for (int x = 0; x < w; x++)
{
    int i = off + (y * w + x) * bpp;
    if (bpp == 4)
    {
        bmp.SetPixel(x, y, Color.FromArgb(all[i + 2], all[i + 1], all[i]));
        continue;
    }
    int v = (all[i] << 8) | all[i + 1]; // big-endian
    int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
    bmp.SetPixel(x, y, Color.FromArgb((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)));
}
bmp.Save(outPng, ImageFormat.Png);
Console.WriteLine("saved " + outPng);
