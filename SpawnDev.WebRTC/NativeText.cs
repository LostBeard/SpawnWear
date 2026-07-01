using System.Runtime.CompilerServices;

namespace SpawnDev.WebRTC
{
    /// <summary>
    /// Native real-font text rendering for the SpawnWear watch. nanoFramework's native
    /// CLR_GFX_Font (DrawText + .tinyfnt) gives proportional, optionally anti-aliased text -
    /// a big jump over the hand-rolled 5x7 SmallFont. The stock resource pipeline can't load a
    /// font here without a version-matched ResourceManager package, so this interop loads a
    /// .tinyfnt from a byte[] at runtime (SD card) and renders through the native font engine.
    ///
    /// <para>Handle-based like <see cref="PeerConnection"/> - the interop boundary carries only
    /// ints / strings / byte[]. A font is created once (<see cref="CreateFont"/>) and referenced
    /// by handle. Text is rendered into a caller-allocated buffer in nanoFramework's native
    /// bitmap format (<see cref="RenderText"/>); the managed side wraps it with
    /// <c>new Bitmap(buf, BitmapImageType.NanoCLRBitmap)</c>, keys out the background with
    /// MakeTransparent, and blits it onto the framebuffer with DrawImage.</para>
    /// </summary>
    public static class NativeText
    {
        /// <summary>Load a .tinyfnt (raw bytes) into the native font engine. Returns a handle
        /// &gt;= 0, or -1 on failure (bad data / out of slots).</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int CreateFont(byte[] tinyFntData);

        /// <summary>Pixel width the given text would render to in the font, or -1 on bad handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int MeasureText(int fontHandle, string text);

        /// <summary>Pixel height (line height) of the font, or -1 on bad handle.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int FontHeight(int fontHandle);

        /// <summary>Render <paramref name="text"/> in <paramref name="argb"/> onto a fresh native
        /// bitmap and serialize it (nanoFramework native bitmap format) into
        /// <paramref name="outNanoBitmap"/>, which must be at least
        /// 12 + ((MeasureText+31)/32)*4 (native-bpp rounding) * FontHeight bytes - size it from
        /// MeasureText/FontHeight. Returns the number of bytes written, or -1 on error / buffer
        /// too small. The background is left as color 0 (black) so MakeTransparent(Black) keys it out.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int RenderText(int fontHandle, string text, int argb, byte[] outNanoBitmap);

        /// <summary>Free a font handle created by <see cref="CreateFont"/>.</summary>
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void ReleaseFont(int fontHandle);
    }
}
