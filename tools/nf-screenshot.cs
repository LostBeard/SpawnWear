// Reads stdin (piped from nf-deploy.cs / nf-attach.cs which surface every
// Debug.WriteLine the watch emits), watches for [SCREENSHOT_BEGIN] / [SCREENSHOT_CHUNK]
// / [SCREENSHOT_END] markers produced by SpawnWear.UI.Screenshot.Capture(), and
// writes the reassembled PNG to disk so the agent / user can look at what is
// on the watch without taking a phone photo.
//
// Usage example:
//     dotnet run tools/nf-deploy.cs SpawnWear/bin/Debug COM9 120 | dotnet run tools/nf-screenshot.cs
//
// While the pipeline is up, every BOOT-button press on the watch causes a new
// screenshots/screenshot-<timestamp>.png to drop next to the project root.
//
// Long-term plan: once WiFi + HTTP server land on the watch, drop this whole
// path - just hit `http://watch.local/screenshot.png` from any browser.

#:package System.Drawing.Common@8.0.0

using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;

string outDir = args.Length >= 1 ? args[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots");
Directory.CreateDirectory(outDir);
Console.Error.WriteLine($"[host] watching stdin for screenshots; output -> {Path.GetFullPath(outDir)}");

var beginRe = new Regex(@"\[SCREENSHOT_BEGIN\]\s+w=(\d+)\s+h=(\d+)\s+fmt=(\S+)\s+chunks=(\d+)");
var chunkRe = new Regex(@"\[SCREENSHOT_CHUNK\]\s+(\S+)");
var base64Re = new Regex(@"^[A-Za-z0-9+/=]+$"); // wrap-continuation lines look like this
var endMarker = "[SCREENSHOT_END]";

int currentW = 0, currentH = 0;
int expectedChunks = 0;
int receivedChunks = 0;
List<byte> currentBytes = null;
string pendingBase64 = null; // accumulator for wrapped chunk lines
int shotIndex = 0;

void FinalizePending()
{
    if (string.IsNullOrEmpty(pendingBase64) || currentBytes == null) { pendingBase64 = null; return; }
    try
    {
        byte[] chunk = Convert.FromBase64String(pendingBase64);
        currentBytes.AddRange(chunk);
        receivedChunks++;
        if (receivedChunks % 100 == 0)
        {
            Console.Error.WriteLine($"[host]   {receivedChunks}/{expectedChunks} chunks...");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[host] chunk decode failed: {ex.Message}");
    }
    pendingBase64 = null;
}

string line;
while ((line = Console.In.ReadLine()) != null)
{
    Console.WriteLine(line); // pass-through so tee-style usage still works
    string trimmed = line.Trim();
    // The nf-deploy.cs prefix is "[runtime] " - strip if present.
    if (trimmed.StartsWith("[runtime] ")) trimmed = trimmed.Substring("[runtime] ".Length);

    var bm = beginRe.Match(trimmed);
    if (bm.Success)
    {
        FinalizePending();
        currentW = int.Parse(bm.Groups[1].Value);
        currentH = int.Parse(bm.Groups[2].Value);
        expectedChunks = int.Parse(bm.Groups[4].Value);
        receivedChunks = 0;
        currentBytes = new List<byte>(currentW * currentH * 2);
        Console.Error.WriteLine($"[host] capture begin {currentW}x{currentH} chunks={expectedChunks}");
        continue;
    }

    var cm = chunkRe.Match(trimmed);
    if (cm.Success && currentBytes != null)
    {
        FinalizePending();
        pendingBase64 = cm.Groups[1].Value;
        continue;
    }

    // Wrap continuation: bare base64 line (no marker) appended to the in-flight chunk.
    if (pendingBase64 != null && base64Re.IsMatch(trimmed))
    {
        pendingBase64 += trimmed;
        continue;
    }

    if (trimmed == endMarker && currentBytes != null && currentW > 0)
    {
        FinalizePending();
        var bmpBytes = currentBytes.ToArray();
        Directory.CreateDirectory(outDir);
        string filename = Path.Combine(outDir, $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}-{shotIndex++}.png");
        SaveRgb565BePng(bmpBytes, currentW, currentH, filename);
        Console.Error.WriteLine($"[host] saved {filename} ({receivedChunks}/{expectedChunks} chunks, {bmpBytes.Length} bytes)");
        currentBytes = null;
        currentW = 0;
        currentH = 0;
        expectedChunks = 0;
        receivedChunks = 0;
        continue;
    }
}

return 0;

static void SaveRgb565BePng(byte[] data, int w, int h, string path)
{
    using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
    int idx = 0;
    for (int y = 0; y < h; y++)
    {
        for (int x = 0; x < w; x++)
        {
            if (idx + 1 >= data.Length) break;
            int hi = data[idx++];
            int lo = data[idx++];
            int rgb565 = (hi << 8) | lo;
            int r = (rgb565 >> 11) & 0x1F; r = (r << 3) | (r >> 2);
            int g = (rgb565 >> 5) & 0x3F;  g = (g << 2) | (g >> 4);
            int b = rgb565 & 0x1F;          b = (b << 3) | (b >> 2);
            bmp.SetPixel(x, y, Color.FromArgb(r, g, b));
        }
    }
    bmp.Save(path, ImageFormat.Png);
}
