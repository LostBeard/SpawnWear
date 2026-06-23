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

    public abstract class WidgetScreen : IScreen, IPressable
    {
        protected readonly IUiSurface Surface;
        protected UIElement Root;

        protected WidgetScreen(IUiSurface surface) { Surface = surface; }

        private bool _needsRender = true;
        private long _pressStartTicks;
        private bool _pressShowing;     // a press-state is currently rendered
        private bool _releaseRequested; // finger lifted; clear the press once it has been visible a bit
        private const int MinPressVisibleMs = 90;

        /// <summary>True while a press release is pending - the event loop keeps ticking fast so the
        /// pressed state stays visible briefly even on a very quick tap (otherwise the finger lifts
        /// before the loop ever renders the pressed frame, and no animation is seen).</summary>
        public bool IsAnimating { get { return _pressShowing && _releaseRequested; } }

        /// <summary>Request a repaint on the next Tick. Rendering is DEFERRED to the event loop rather
        /// than done synchronously inside OnResume/OnTap: the navigator calls OnResume mid-transition
        /// (during the tap that opened the screen), and drawing then left the previous screen partially
        /// visible. Rendering on the next Tick lets the transition + touch handling settle first.</summary>
        protected void RequestRender() { _needsRender = true; }

        private void RenderNow()
        {
            if (Root == null) return;
            Surface.Clear(Theme.Current.Background); // full clear (the launcher does this) before draw
            Root.Layout();
            Root.Draw(Surface);
            Surface.FlushAll();                      // no-arg whole-bitmap flush
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
            if (Root == null) return false;
            bool consumed = Root.OnTap(x, y);
            if (consumed) _needsRender = true; // a widget changed state -> repaint next tick
            return consumed;
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
