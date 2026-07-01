using nanoFramework.UI;
using SpawnDev.WebRTC;
using System.Drawing;

namespace SpawnWear.UI
{
    /// <summary>
    /// Real proportional font rendered through the native CLR_GFX_Font engine via the
    /// NativeText interop - a big step up from the blocky hand-rolled 5x7 SmallFont.
    ///
    /// A .tinyfnt is loaded once (from the SD card at runtime, so the font can be swapped
    /// without a firmware rebuild). Drawing renders the text natively into a BMP, which we
    /// wrap with <c>new Bitmap(buf, Bmp)</c>, key the black background out with MakeTransparent,
    /// and blit onto the framebuffer with DrawImage (all confirmed working on this CO5300 build).
    /// </summary>
    public class NativeFont
    {
        readonly int _handle;
        readonly int _height;

        public bool IsValid { get { return _handle >= 0; } }
        public int Height { get { return _height; } }

        // ---- Shared UI font ----
        // The whole UI draws through ONE loaded font. The native NativeText engine keeps only a
        // small pool of static font buffers (nanoFramework bans malloc), so every screen must reuse
        // this single instance rather than each loading its own .tinyfnt into a separate slot.
        // Loaded lazily from the SD card the first time it's asked for. If the file is missing or
        // the load fails, Shared stays null and callers fall back to SmallFont - the UI degrades to
        // the 5x7 bitmap font instead of throwing.
        // The native NativeText engine has exactly TWO static font slots (SW_MAX_FONTS = 2), so the
        // UI standardizes on two sizes loaded from the SD card:
        //   Shared      - the large ~30px face: status-bar clock + screen titles.
        //   SharedSmall - the ~18px face: tile labels, list rows, value pairs, hints/footers.
        // Both load lazily and independently; either can be null (missing SD / bad font) and callers
        // fall back to SmallFont.
        const string SharedFontPath = "D:\\spawnsans.tinyfnt";
        const string SharedSmallFontPath = "D:\\spawnsans-sm.tinyfnt";
        static NativeFont _shared;
        static bool _sharedTried;
        static NativeFont _sharedSmall;
        static bool _sharedSmallTried;

        static NativeFont Load(string path)
        {
            try
            {
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                var f = new NativeFont(bytes);
                if (f.IsValid) return f;
            }
            catch { /* no SD / no file / bad font -> null, callers fall back */ }
            return null;
        }

        /// <summary>The large (~30px) UI face for the clock + titles, or null if unavailable.
        /// Callers must null-check (and check <see cref="IsValid"/>) and fall back to SmallFont.</summary>
        public static NativeFont Shared
        {
            get
            {
                if (!_sharedTried) { _sharedTried = true; _shared = Load(SharedFontPath); }
                return _shared;
            }
        }

        /// <summary>The small (~18px) UI face for tile labels, list rows and hints, or null if
        /// unavailable. Callers must null-check and fall back to SmallFont.</summary>
        public static NativeFont SharedSmall
        {
            get
            {
                if (!_sharedSmallTried) { _sharedSmallTried = true; _sharedSmall = Load(SharedSmallFontPath); }
                return _sharedSmall;
            }
        }

        /// <summary>Draws horizontally-centered text using <paramref name="font"/> when it's valid,
        /// otherwise SmallFont at <paramref name="fallbackScale"/>. <paramref name="y"/> is the top of
        /// the glyph box in both paths. Pass <see cref="Shared"/> for titles or <see cref="SharedSmall"/>
        /// for hints/footers.</summary>
        public static void DrawCentered(NativeFont font, Bitmap fb, string text, int panelWidth, int y, Color color, int fallbackScale)
        {
            if (font != null && font.IsValid)
            {
                int w = font.Measure(text);
                font.Draw(fb, text, (panelWidth - w) / 2, y, color);
            }
            else
            {
                int w = SmallFont.MeasureString(text, fallbackScale);
                SmallFont.DrawString(fb, text, (panelWidth - w) / 2, y, fallbackScale, color);
            }
        }

        /// <summary>Load a .tinyfnt from raw bytes (e.g. File.ReadAllBytes("D:\\font.tinyfnt")).</summary>
        public NativeFont(byte[] tinyFnt)
        {
            _handle = (tinyFnt != null && tinyFnt.Length > 0) ? NativeText.CreateFont(tinyFnt) : -1;
            _height = _handle >= 0 ? NativeText.FontHeight(_handle) : 0;
        }

        /// <summary>Pixel width the text would render to.</summary>
        public int Measure(string text)
        {
            if (_handle < 0 || text == null) return 0;
            int w = NativeText.MeasureText(_handle, text);
            return w < 0 ? 0 : w;
        }

        /// <summary>Draw <paramref name="text"/> onto <paramref name="fb"/> at (x, y) in
        /// <paramref name="color"/>. The black glyph background is keyed out, so avoid pure-black text.</summary>
        public void Draw(Bitmap fb, string text, int x, int y, Color color)
        {
            if (_handle < 0 || text == null || text.Length == 0) return;
            int w = NativeText.MeasureText(_handle, text);
            if (w <= 0) return;
            int rowBytes = (w * 3 + 3) & ~3;      // BMP rows padded to 4 bytes (24bpp)
            int total = 54 + rowBytes * _height;  // 54 = BMP file+info headers
            byte[] buf = new byte[total];
            int n = NativeText.RenderText(_handle, text, color.ToArgb(), buf);
            if (n <= 0) return;
            var bmp = new Bitmap(buf, Bitmap.BitmapImageType.Bmp);
            bmp.MakeTransparent(Color.Black);
            fb.DrawImage(new System.Drawing.Point(x, y), bmp);
            bmp.Dispose();
        }

        public void Release()
        {
            if (_handle >= 0) NativeText.ReleaseFont(_handle);
        }
    }
}
