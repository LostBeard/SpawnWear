#:package System.Drawing.Common@8.0.0
using System.Drawing;
using System.Drawing.Imaging;

// usage: dotnet run tools/fonts/decode-shot.cs <shot.bin> [out.png]   (shot.bin = curl http://<watch>/screenshot.bin)
string inPath = args.Length > 0 ? args[0] : "shot.bin";
string outPng = args.Length > 1 ? args[1] : Path.ChangeExtension(inPath, ".png");
byte[] all = File.ReadAllBytes(inPath);
// Header: ASCII "w=W h=H\n"
int nl = Array.IndexOf(all, (byte)'\n');
string hdr = System.Text.Encoding.ASCII.GetString(all, 0, nl);
var parts = hdr.Replace("w=", "").Replace("h=", "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
int w = int.Parse(parts[0]), h = int.Parse(parts[1]);
int off = nl + 1;
Console.WriteLine($"header='{hdr}' w={w} h={h} pixelBytes={all.Length - off} expected={w*h*2}");
using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
for (int y = 0; y < h; y++)
for (int x = 0; x < w; x++)
{
    int i = off + (y * w + x) * 2;
    int v = (all[i] << 8) | all[i + 1]; // big-endian
    int r = (v >> 11) & 0x1F, g = (v >> 5) & 0x3F, b = v & 0x1F;
    bmp.SetPixel(x, y, Color.FromArgb((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2)));
}
bmp.Save(outPng, ImageFormat.Png);
Console.WriteLine("saved " + outPng);
