using nanoFramework.UI;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Text;

namespace SpawnWear.UI
{
    /// <summary>
    /// Dev-time screenshot capture - downsamples the FullScreen Bitmap and
    /// emits the pixels as base64 chunks over Debug.WriteLine. The host-side
    /// tool <c>tools/nf-screenshot.cs</c> pairs with this to reassemble a
    /// PNG from the chunks.
    ///
    /// Trigger: BOOT button (GPIO0). No production use - the wire-protocol
    /// throughput is fine for occasional dev debugging but not for live
    /// streaming. Phase 4 will replace this with an SD-card dump or a BLE
    /// transfer once the supporting services land.
    /// </summary>
    public static class Screenshot
    {
        // Downsample factor: read every Nth pixel in both dimensions. 2 = quarter
        // resolution (~205 x 251 = 51 KB at RGB565). Larger = smaller / faster.
        const int Downsample = 2;
        // Bytes per chunk. The wire protocol hard-wraps Debug.WriteLine around
        // 128 chars per line, so chunks larger than ~90 raw bytes (=120 base64
        // chars) get split across multiple lines. The host parser
        // (tools/nf-screenshot.cs) was updated to handle wrap-continuation
        // lines, so we use 256 here for ~3x faster transmission - cuts a full
        // capture from ~60s to ~20s.
        const int ChunkBytes = 256;

        /// <summary>
        /// Captures a thumbnail of the FullScreen Bitmap and dumps it via
        /// Debug.WriteLine as a header line, N base64 chunks, and a footer
        /// line. Returns the number of bytes captured.
        /// </summary>
        public static int Capture(Bitmap fb, int panelWidth, int panelHeight)
        {
            if (fb == null) return 0;

            int dsW = panelWidth / Downsample;
            int dsH = panelHeight / Downsample;
            int totalBytes = dsW * dsH * 2; // 16 bpp packed RGB565

            byte[] buffer = new byte[totalBytes];
            int writeIdx = 0;
            for (int y = 0; y < dsH; y++)
            {
                int srcY = y * Downsample;
                for (int x = 0; x < dsW; x++)
                {
                    int srcX = x * Downsample;
                    Color px = fb.GetPixel(srcX, srcY);
                    ushort rgb565 = ToRgb565(px);
                    buffer[writeIdx++] = (byte)(rgb565 >> 8);
                    buffer[writeIdx++] = (byte)(rgb565 & 0xFF);
                }
            }

            int chunkCount = (totalBytes + ChunkBytes - 1) / ChunkBytes;
            Debug.WriteLine("[SCREENSHOT_BEGIN] w=" + dsW + " h=" + dsH + " fmt=rgb565be chunks=" + chunkCount);
            int offset = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                int len = (offset + ChunkBytes > totalBytes) ? (totalBytes - offset) : ChunkBytes;
                string b64 = Convert.ToBase64String(buffer, offset, len);
                Debug.WriteLine("[SCREENSHOT_CHUNK] " + b64);
                offset += len;
            }
            Debug.WriteLine("[SCREENSHOT_END]");
            return totalBytes;
        }

        static ushort ToRgb565(Color c)
        {
            int r = (c.R >> 3) & 0x1F;
            int g = (c.G >> 2) & 0x3F;
            int b = (c.B >> 3) & 0x1F;
            return (ushort)((r << 11) | (g << 5) | b);
        }
    }
}
