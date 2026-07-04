using System;
using SpawnWear.UI;       // IScreen
using SpawnWear.Drivers;  // BoardPins (corner radius)

namespace SpawnDev.UI
{
    /// <summary>
    /// <see cref="IScreen"/> base that hosts a retained-mode <see cref="UIElement"/> tree on an
    /// <see cref="IUiSurface"/>. A subclass builds its <see cref="Root"/> tree (with <see cref="Theme"/>
    /// + the layout containers) and this base drives the lifecycle: lay out + paint on resume /
    /// invalidate, and route taps into the tree's hit-test. Replaces the per-screen hand-rolled
    /// draw/flush/hit-test boilerplate.
    /// </summary>
    /// <summary>A screen wanting raw finger press/release (in addition to IScreen's classified OnTap) -
    /// for press-state animation, drag, scroll. The event loop dispatches OnPress on finger-down and
    /// OnRelease on finger-up. Old immediate-mode screens simply don't implement it.</summary>
    public interface IPressable
    {
        void OnPress(int x, int y);
        void OnRelease();
    }

    /// <summary>A screen whose content can be scrolled vertically by a finger drag. The event loop calls
    /// OnScroll with the finger's vertical delta while dragging.</summary>
    public interface IScrollable
    {
        void OnScroll(int fingerDy);
    }

    public abstract class WidgetScreen : IScreen, IPressable, IScrollable
    {
        /// <summary>A scrolling list on this screen, if any - set by a subclass. When set, vertical drags
        /// scroll it (see <see cref="OnScroll"/>).</summary>
        protected UIScrollColumn ScrollTarget;

        protected readonly IUiSurface Surface;
        protected UIElement Root;

        protected WidgetScreen(IUiSurface surface) { Surface = surface; }

        private bool _needsRender = true;
        private long _pressStartTicks;
        private bool _pressShowing;     // a press-state is currently rendered
        private bool _releaseRequested; // finger lifted; clear the press once it has been visible a bit
        private const int MinPressVisibleMs = 90;

        /// <summary>True while a press release is pending - the event loop keeps ticking fast (16ms) so the
        /// pressed state stays visible briefly even on a very quick tap. (Screen-to-screen slide
        /// transitions are owned by the navigator, not the screen.)</summary>
        public bool IsAnimating { get { return _pressShowing && _releaseRequested; } }

        /// <summary>Request a repaint on the next Tick. Rendering is DEFERRED to the event loop rather
        /// than done synchronously inside OnResume/OnTap: the navigator calls OnResume mid-transition
        /// (during the tap that opened the screen), and drawing then left the previous screen partially
        /// visible. Rendering on the next Tick lets the transition + touch handling settle first.</summary>
        protected void RequestRender() { _needsRender = true; }

        /// <summary>Static chrome (status bar + page dots) drawn ON TOP of the page content, at fixed
        /// screen positions. Set by the navigator on rotation pages; null on modal overlays. It is drawn
        /// in <see cref="RenderNow"/> (so a normal repaint includes it) but NOT in
        /// <see cref="RenderNoFlush"/> (so the off-display snapshot the slide captures is page-content
        /// ONLY - the chrome therefore never slides; the navigator redraws it static over the slide).</summary>
        public System.Action ChromeDrawer;

        private void RenderNow()
        {
            if (Root == null) return;
            Surface.ClearClip();                     // drop any scroll-viewport clip left by a prior render
            Surface.Clear(Theme.Current.Background); // full clear (the launcher does this) before draw
            Root.Layout();
            Root.Draw(Surface);
            if (ChromeDrawer != null) ChromeDrawer(); // fixed chrome on top (part of this flush = no flicker)
            Surface.FlushAll();                      // no-arg whole-bitmap flush
        }

        /// <summary>Draw the tree to the framebuffer WITHOUT flushing it to the display, and WITHOUT the
        /// chrome. The navigator captures this into an off-display buffer for a slide transition, so the
        /// snapshot is page content only - the chrome stays fixed and is drawn separately over the slide.
        /// Clears the deferred-render flag since the tree is now painted.</summary>
        public void RenderNoFlush()
        {
            if (Root == null) return;
            Surface.ClearClip();                     // drop any scroll-viewport clip left by a prior render
            Surface.Clear(Theme.Current.Background);
            Root.Layout();
            Root.Draw(Surface);
            _needsRender = false;
        }

        public virtual void OnResume() { _needsRender = true; }
        public virtual void OnPause() { }
        public virtual void Tick()
        {
            if (_releaseRequested)
            {
                long heldMs = (DateTime.UtcNow.Ticks - _pressStartTicks) / TimeSpan.TicksPerMillisecond;
                if (heldMs >= MinPressVisibleMs)
                {
                    if (Root != null) Root.OnRelease();
                    _pressShowing = false;
                    _releaseRequested = false;
                    _needsRender = true;
                }
            }
            if (_needsRender) { _needsRender = false; RenderNow(); }
        }
        public virtual void Invalidate() { _needsRender = true; }

        public virtual bool OnTap(int x, int y)
        {
            if (Root != null && Root.OnTap(x, y)) _needsRender = true; // a widget consumed -> repaint
            // Always consume: a tap on the screen background does nothing. Back is the BOOT side button,
            // not an accidental tap on empty space (which used to pop the screen).
            return true;
        }

        // IPressable: raw finger down/up -> press-state in the tree (e.g. a button darkens while held).
        public virtual void OnPress(int x, int y)
        {
            if (Root != null && Root.OnPress(x, y))
            {
                _pressStartTicks = DateTime.UtcNow.Ticks;
                _pressShowing = true;
                _releaseRequested = false;
                _needsRender = true;
            }
        }

        public virtual void OnRelease()
        {
            // Defer the visual release so the pressed frame is guaranteed at least MinPressVisibleMs on
            // screen (Tick does the actual clear). On a quick tap the finger lifts before the loop ever
            // rendered the press; deferring keeps it visible.
            if (_pressShowing) _releaseRequested = true;
            else if (Root != null) Root.OnRelease();
        }

        // IScrollable: a vertical finger drag scrolls the screen's scroll list (if it has one).
        public virtual void OnScroll(int fingerDy)
        {
            if (ScrollTarget != null)
            {
                if (Root != null) Root.OnRelease(); // a drag cancels any pending press-state on a row
                _pressShowing = false; _releaseRequested = false;
                ScrollTarget.Scroll(fingerDy);
                _needsRender = true;
            }
        }
    }

    /// <summary>
    /// Round-corner safe area for the watch's ~100px-radius AMOLED. Pixels inside the four corner
    /// quarter-circles are clipped by the glass, so full-width content that reaches the left/right
    /// edges must stay in the safe vertical band [CornerRadius, Height-CornerRadius]. Centered content
    /// (the center column never clips) is fine anywhere. This bakes the constant in one place so
    /// screens stop scattering magic insets.
    /// </summary>
    public static class SafeArea
    {
        public static int CornerRadius => BoardPins.LcdCornerRadius; // 100
        public static int EdgeInset = 20; // keep full-width rows off the very edge

        /// <summary>The safe rect for full-width content on a panel of the given size: x inset by
        /// <see cref="EdgeInset"/>, y in [CornerRadius, Height-CornerRadius]. Size a full-width
        /// <see cref="UIColumn"/> to this band.</summary>
        public static void Band(int panelW, int panelH, out int x, out int y, out int w, out int h)
        {
            x = EdgeInset;
            y = CornerRadius;
            w = panelW - 2 * EdgeInset;
            h = panelH - 2 * CornerRadius;
        }
    }
}
