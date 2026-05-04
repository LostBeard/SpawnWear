// dotnet run tools/bin-to-png.cs <in.bin> <out.png>
// Reads "w=W h=H\n" header followed by raw RGB565 BE pixels and writes PNG.
#:package SkiaSharp@2.88.8
using System;
using System.IO;
using SkiaSharp;

if (args.Length < 2) { Console.Error.WriteLine("usage: bin-to-png <in.bin> <out.png>"); return 1; }
var bytes = File.ReadAllBytes(args[0]);
int nl = 0; while (bytes[nl] != (byte)'\n') nl++;
var hdr = System.Text.Encoding.ASCII.GetString(bytes, 0, nl);
int w = 0, h = 0;
foreach (var part in hdr.Split(' ')) {
    if (part.StartsWith("w=")) w = int.Parse(part.Substring(2));
    else if (part.StartsWith("h=")) h = int.Parse(part.Substring(2));
}
Console.Error.WriteLine($"hdr: {hdr.Trim()} -> {w}x{h}, payload {bytes.Length - nl - 1} bytes");
var info = new SKImageInfo(w, h, SKColorType.Rgba8888);
using var bmp = new SKBitmap(info);
var pixels = new byte[w * h * 4];
int off = nl + 1;
for (int i = 0; i < w * h; i++) {
    byte hi = bytes[off + i*2], lo = bytes[off + i*2 + 1];
    int v = (hi << 8) | lo;
    int r5 = (v >> 11) & 0x1F, g6 = (v >> 5) & 0x3F, b5 = v & 0x1F;
    pixels[i*4 + 0] = (byte)((r5 << 3) | (r5 >> 2));
    pixels[i*4 + 1] = (byte)((g6 << 2) | (g6 >> 4));
    pixels[i*4 + 2] = (byte)((b5 << 3) | (b5 >> 2));
    pixels[i*4 + 3] = 255;
}
System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
using var img = SKImage.FromBitmap(bmp);
using var data = img.Encode(SKEncodedImageFormat.Png, 100);
using var stream = File.OpenWrite(args[1]);
data.SaveTo(stream);
Console.Error.WriteLine($"wrote {args[1]}");
return 0;
