using System.Diagnostics;
using nanoFramework.UI;

namespace SpawnWear.UI
{
    /// <summary>
    /// Screen manager with two layers:
    ///
    /// 1. A flat <b>rotation</b> of registered top-level <see cref="IScreen"/>s
    ///    (watchface, settings, ...) cycled by tap-outside (<see cref="Next"/>)
    ///    and snapped home by long-press (<see cref="GoHome"/>).
    /// 2. A <b>push/pop stack</b> on top of the rotation for sub-pages (e.g.
    ///    Settings -> Companion). <see cref="Push"/> overlays a screen;
    ///    a tap that the screen doesn't consume <see cref="Pop"/>s back one
    ///    level, and only at the base (depth 0) does an unconsumed tap rotate
    ///    to the next top-level screen.
    ///
    /// The visible screen is always <see cref="Current"/>. Lifecycle is strict:
    /// exactly one screen is "resumed" at a time; Push/Pop/rotation pause the
    /// outgoing screen and resume the incoming one. The caller triggers a Tick
    /// after any transition to paint the newly-active screen.
    /// </summary>
    public class ScreenNavigator
    {
        private readonly IScreen[] _screens;
        private int _activeIndex;

        // Sub-page overlay stack. Depth 0 == on the rotation layer.
        private readonly IScreen[] _stack = new IScreen[8];
        private int _depth;

        // ---- Slide transitions (snapshot-composited so BOTH the outgoing and incoming screens are
        // visible at once). The navigator snapshots the framebuffer before/after a Push/Pop, then each
        // frame blits the static screen as the base and the moving (overlay) screen at a rising/falling
        // y offset. Falls back to an instant Push/Pop if the framebuffer isn't set or a snapshot fails.
        private Bitmap _fb;
        private int _w, _h;
        // Two PERSISTENT full-screen buffers allocated once at boot (heap is clean/contiguous then) and
        // reused for every transition - a per-transition `new Bitmap` fails once the heap is fragmented.
        private Bitmap _snapUnder;     // the screen BENEATH the overlay (captured at open, reused at close)
        private Bitmap _snapOver;      // the overlay screen
        private bool _canTransition;   // false if the boot allocation failed -> instant Push/Pop
        private bool _animOpen;        // current overlay was opened via PushAnimated -> _snapUnder is valid
        private bool _popOnDone;       // pop the overlay when the current (close) transition finishes
        // Generalized 2-layer composite slide. Each layer is one of the snapshots, positioned by a base
        // offset plus a shared animated offset (only if that layer moves), along one axis:
        //   Overlay push/pop  -> base static, overlay moves on Y (vertical drop-down).
        //   Rotation next/prev -> both layers move together on X (horizontal filmstrip).
        private Bitmap _layer1, _layer2;
        private int _l1Base, _l2Base;  // each layer's base offset on the axis
        private bool _l1Moves, _l2Moves;
        private bool _axisX;           // true = slide horizontally (X), false = vertically (Y)
        private int _transOffset;      // current animated offset along the axis
        private int _transTarget;      // final offset
        private int _transStep;        // signed per-frame delta
        private bool _transitioning;
        // Chrome drawn over an axisX slide: 1 = full (status bar + carousel dots, for screen rotation),
        // 2 = status bar only (launcher page slide - the launcher bakes its own app-page dots into the
        // sliding content). Y-axis overlays draw no chrome regardless.
        private int _transChromeMode = 1;
        private const int SlideStepPx = 90; // per-frame slide distance (30=slow .. 60=2x .. 90=3x .. 120=4x)

        /// <summary>Give the navigator the shared framebuffer and pre-allocate the two transition buffers
        /// while the heap is still clean. If the allocation fails, Push/Pop stay instant.</summary>
        public void SetFramebuffer(Bitmap fb, int w, int h)
        {
            _fb = fb; _w = w; _h = h;
            string diag;
            try
            {
                _snapUnder = new Bitmap(w, h);
                _snapOver = new Bitmap(w, h);
                _canTransition = true;
                diag = "canTransition=True (2 x " + (w * h * 2) + " bytes allocated)";
            }
            catch (System.Exception ex)
            {
                _snapUnder = null; _snapOver = null; _canTransition = false;
                diag = "canTransition=False FAILED: " + ex.GetType().Name + " " + ex.Message;
            }
            Debug.WriteLine("[Nav] " + diag);
            // Self-diagnostic readable without any user gesture: get transdiag.txt over the console.
            try { System.IO.File.WriteAllText("D:\\transdiag.txt", diag); } catch { }
        }

        /// <summary>True while a slide transition is animating; the main loop calls
        /// <see cref="TickTransition"/> each frame instead of the current screen's Tick.</summary>
        public bool IsTransitioning => _transitioning;

        // Reset any clip left on the framebuffer by a scroll-list render, so captures/composites aren't
        // limited to the scroll viewport. Defensive (SetClippingRectangle unverified on this firmware).
        private void ClearFbClip() { try { _fb.SetClippingRectangle(0, 0, _w, _h); } catch { } }

        private void CaptureInto(Bitmap dst)
        {
            ClearFbClip();
            dst.DrawImage(new System.Drawing.Point(0, 0), _fb);
        }

        // ---- Fixed chrome (status bar + page dots) drawn over the page content. It does NOT slide: each
        // rotation page's RenderNow draws it on top (via ChromeDrawer), the off-display snapshot excludes
        // it, and the transition compositor redraws it static over the sliding content.
        private StatusBar _chromeBar;
        public void SetChrome(StatusBar bar) { _chromeBar = bar; }

        /// <summary>Draw the fixed chrome into the framebuffer (no flush): just the status bar at top.
        /// (There is no screen carousel anymore, so no carousel page dots - the launcher draws its own
        /// app-page dots. Kept as the WidgetScreen ChromeDrawer so every widget screen shows the bar.)</summary>
        public void DrawChrome()
        {
            if (_chromeBar != null) _chromeBar.Render(force: true, flush: false);
        }

        /// <summary>Cheap per-tick live update of the status-bar clock for a WidgetScreen rotation page
        /// (partial flush, change-detection) - hand-rolled screens still do this themselves.</summary>
        public void TickChrome()
        {
            if (_depth == 0 && _chromeBar != null && (Current as SpawnDev.UI.WidgetScreen) != null)
                _chromeBar.Render(false, true);
        }

        public ScreenNavigator(IScreen[] screens)
        {
            _screens = screens;
            _activeIndex = 0;
            // Rotation pages that are WidgetScreens carry the fixed chrome (drawn on top of their content,
            // never captured into the slide snapshot). Modal overlays (pushed later) leave ChromeDrawer
            // null so they cover the full screen with no chrome.
            for (int i = 0; i < screens.Length; i++)
            {
                var ws = screens[i] as SpawnDev.UI.WidgetScreen;
                if (ws != null) ws.ChromeDrawer = DrawChrome;
            }
            // First screen is implicitly active. Caller is responsible for
            // calling Invalidate / Tick on the first iteration.
        }

        /// <summary>The screen currently visible: the top of the sub-page stack
        /// if any sub-page is pushed, otherwise the active rotation screen.</summary>
        public IScreen Current => _depth > 0 ? _stack[_depth - 1] : _screens[_activeIndex];

        public int CurrentIndex => _activeIndex;

        /// <summary>How many sub-pages are stacked on the rotation (0 = base).</summary>
        public int StackDepth => _depth;

        /// <summary>
        /// Overlays <paramref name="screen"/> as a sub-page on top of whatever is
        /// visible. Pauses the outgoing screen and resumes the pushed one. Used by
        /// e.g. Settings to open the Companion sub-page.
        /// </summary>
        public void Push(IScreen screen)
        {
            if (screen == null || _depth >= _stack.Length) return;
            _animOpen = false; // a plain (unanimated) push - no captured "beneath" to reuse on close
            SafePause(Current);
            _stack[_depth++] = screen;
            Debug.WriteLine("[Nav] Push -> depth " + _depth);
            SafeResume(screen);
        }

        /// <summary>
        /// Pops the top sub-page and resumes whatever is underneath. Returns false
        /// if there was nothing to pop (already at the rotation base).
        /// </summary>
        public bool Pop()
        {
            if (_depth == 0) return false;
            _animOpen = false;
            SafePause(_stack[_depth - 1]);
            _stack[--_depth] = null;
            Debug.WriteLine("[Nav] Pop -> depth " + _depth);
            SafeResume(Current);
            return true;
        }

        /// <summary>Finalizes an animated close WITHOUT forcing the beneath screen's full repaint. The
        /// slide already ended on a complete image of the beneath (the reused <c>_snapUnder</c> snapshot),
        /// so a full OnResume/Invalidate would only re-clear-and-redraw it - flashing a black top strip
        /// for one frame before the status bar redraws. Instead we pause the overlay and let the beneath's
        /// normal Tick refresh live content (status bar clock etc.) via its change-detection partial
        /// flushes. Safe because a quick-settings overlay is modal - the screen beneath doesn't change
        /// while it's open.</summary>
        private void SoftPop()
        {
            if (_depth == 0) return;
            _animOpen = false;
            SafePause(_stack[_depth - 1]);
            _stack[--_depth] = null;
            Debug.WriteLine("[Nav] SoftPop -> depth " + _depth);
        }

        /// <summary>Animated open of a sub-page overlay: the new screen slides DOWN from the top over the
        /// screen beneath, which stays visible. The overlay is rendered into an OFF-DISPLAY buffer (no
        /// flush) so its raw frame never flashes on-screen before the slide. Requires the pushed screen to
        /// be a WidgetScreen (which can render without flushing); otherwise falls back to instant Push.</summary>
        public void PushAnimated(IScreen screen)
        {
            if (screen == null || _depth >= _stack.Length) return;
            var ws = screen as SpawnDev.UI.WidgetScreen;
            if (!_canTransition || _transitioning || ws == null) { Push(screen); return; }
            CaptureInto(_snapUnder);   // the screen beneath (currently displayed) - no flash
            Push(screen);              // WidgetScreen OnResume only marks dirty - no draw/flush yet
            ws.RenderNoFlush();        // draw the overlay into the framebuffer WITHOUT flushing to display
            CaptureInto(_snapOver);    // capture the overlay; the display still shows "beneath"
            _animOpen = true;          // keep _snapUnder for the eventual animated close
            _popOnDone = false;
            // Overlay slides DOWN (Y) over the static beneath: layer1=beneath (static), layer2=overlay.
            BeginTransition(_snapUnder, 0, false, _snapOver, 0, true, false, -_h, 0, SlideStepPx);
        }

        /// <summary>Animated back: the overlay slides UP off the top, revealing the screen beneath. The
        /// beneath image captured at open is reused as the static base (no re-render, so no flash), and
        /// the real Pop is DEFERRED until the slide finishes. Only animates when the overlay was opened
        /// via <see cref="PushAnimated"/>; otherwise instant <see cref="Pop"/>.</summary>
        public void RequestBack()
        {
            if (_depth == 0) return;
            if (!_canTransition || _transitioning || !_animOpen) { Pop(); return; }
            CaptureInto(_snapOver);    // the overlay (currently displayed) - fresh, no flash
            _popOnDone = true;         // pop (resume the beneath fresh) once the slide completes
            // Overlay slides UP (Y) off the static beneath.
            BeginTransition(_snapUnder, 0, false, _snapOver, 0, true, false, 0, -_h, -SlideStepPx);
        }

        /// <summary>Animated rotation to the NEXT top-level screen (swipe left): the current screen slides
        /// off to the left while the next slides in from the right (both move together). The incoming
        /// screen is rendered off-display (must be a WidgetScreen); otherwise instant <see cref="Next"/>.</summary>
        public void NextAnimated()
        {
            if (_depth > 0) { Next(); return; }
            int next = (_activeIndex + 1) % _screens.Length;
            if (next == _activeIndex) return;
            var ws = _screens[next] as SpawnDev.UI.WidgetScreen;
            if (!_canTransition || _transitioning || ws == null) { Next(); return; }
            CaptureInto(_snapUnder);   // current screen A (displayed) - free
            Next();                    // switch to B (WidgetScreen OnResume defers - no flush)
            ws.RenderNoFlush();        // draw B off-display
            CaptureInto(_snapOver);    // capture B; the display still shows A
            _transChromeMode = 1;      // full chrome (status bar + carousel dots) over the rotation slide
            // Both slide LEFT together (X): A from 0 -> -w, B from +w -> 0.
            BeginTransition(_snapUnder, 0, true, _snapOver, _w, true, true, 0, -_w, -SlideStepPx);
        }

        /// <summary>Animated rotation to the PREVIOUS screen (swipe right): current slides off right, the
        /// previous slides in from the left. Incoming must be a WidgetScreen; else instant <see cref="Prev"/>.</summary>
        public void PrevAnimated()
        {
            if (_depth > 0) { Prev(); return; }
            int prev = (_activeIndex - 1 + _screens.Length) % _screens.Length;
            if (prev == _activeIndex) return;
            var ws = _screens[prev] as SpawnDev.UI.WidgetScreen;
            if (!_canTransition || _transitioning || ws == null) { Prev(); return; }
            CaptureInto(_snapUnder);   // current screen A - free
            Prev();                    // switch to B
            ws.RenderNoFlush();        // draw B off-display
            CaptureInto(_snapOver);    // capture B
            _transChromeMode = 1;      // full chrome (status bar + carousel dots) over the rotation slide
            // Both slide RIGHT together (X): A from 0 -> +w, B from -w -> 0.
            BeginTransition(_snapUnder, 0, true, _snapOver, -_w, true, true, 0, _w, SlideStepPx);
        }

        /// <summary>Renders the incoming content into the framebuffer WITHOUT flushing - the caller draws
        /// its next page here so the navigator can capture and slide it.</summary>
        public delegate void RenderContent();

        // Caller-supplied fixed chrome for a content slide (status bar + the launcher's app-page dots),
        // redrawn on top of the sliding tiles every frame so it stays put while only the content moves.
        private RenderContent _transFixedChrome;

        /// <summary>Horizontal content slide within a single self-chromed screen (the launcher paging
        /// between app-grid pages). Mirrors the rotation slide's fixed-chrome model: only the sliding
        /// CONTENT (tiles) is captured and moved; the chrome (status bar + app-page dots) is redrawn
        /// FIXED on top each frame via <paramref name="drawFixedChrome"/>, so the dots stay in place and
        /// just change which one is lit. <paramref name="renderOutgoing"/> and
        /// <paramref name="renderIncoming"/> each draw one page's tiles-only content into the framebuffer
        /// (no chrome, no flush) to be captured. <paramref name="forward"/>=true means the new page
        /// enters from the right (swipe left). Returns false if it can't animate (caller repaints instantly).</summary>
        public bool SlideContentHorizontal(bool forward, RenderContent renderOutgoing, RenderContent renderIncoming, RenderContent drawFixedChrome)
        {
            if (!_canTransition || _transitioning || renderOutgoing == null || renderIncoming == null) return false;
            renderOutgoing();          // draw the outgoing page's tiles (no chrome) into fb
            CaptureInto(_snapUnder);   // capture outgoing tiles; display still shows the live page
            renderIncoming();          // draw the incoming page's tiles (no chrome) into fb
            CaptureInto(_snapOver);    // capture incoming tiles
            _transFixedChrome = drawFixedChrome;
            _transChromeMode = 2;      // caller-supplied fixed chrome (status bar + app-page dots)
            if (forward)
                BeginTransition(_snapUnder, 0, true, _snapOver, _w, true, true, 0, -_w, -SlideStepPx);
            else
                BeginTransition(_snapUnder, 0, true, _snapOver, -_w, true, true, 0, _w, SlideStepPx);
            return true;
        }

        private void BeginTransition(Bitmap layer1, int l1Base, bool l1Moves,
                                     Bitmap layer2, int l2Base, bool l2Moves,
                                     bool axisX, int fromOffset, int toOffset, int step)
        {
            _layer1 = layer1; _l1Base = l1Base; _l1Moves = l1Moves;
            _layer2 = layer2; _l2Base = l2Base; _l2Moves = l2Moves;
            _axisX = axisX;
            _transOffset = fromOffset; _transTarget = toOffset; _transStep = step;
            _transitioning = true;
        }

        /// <summary>Advance the slide one frame: blit the static base then the moving overlay at the
        /// current offset; free the snapshots when it reaches the target.</summary>
        public void TickTransition()
        {
            if (!_transitioning) return;
            _transOffset += _transStep;
            bool done = _transStep > 0 ? _transOffset >= _transTarget : _transOffset <= _transTarget;
            if (done) _transOffset = _transTarget;
            ClearFbClip(); // a scroll page may have left a viewport clip that would crop the composite
            int o1 = _l1Base + (_l1Moves ? _transOffset : 0);
            int o2 = _l2Base + (_l2Moves ? _transOffset : 0);
            if (_axisX)
            {
                _fb.DrawImage(new System.Drawing.Point(o1, 0), _layer1);
                _fb.DrawImage(new System.Drawing.Point(o2, 0), _layer2);
            }
            else
            {
                _fb.DrawImage(new System.Drawing.Point(0, o1), _layer1);
                _fb.DrawImage(new System.Drawing.Point(0, o2), _layer2);
            }
            // Rotation (horizontal) pages carry the fixed chrome on top; modal overlays (vertical) don't.
            if (_axisX)
            {
                if (_transChromeMode == 1) DrawChrome();                                   // status bar + carousel dots
                else if (_transChromeMode == 2 && _transFixedChrome != null) _transFixedChrome(); // caller-supplied fixed chrome (status bar + app-page dots)
            }
            _fb.Flush();
            if (done)
            {
                _transitioning = false;
                // Animated CLOSE: the overlay is still on the stack; finalize the pop WITHOUT forcing the
                // beneath to full-repaint (which flashes a black top strip for a frame). The slide already
                // ended on a complete image of the beneath.
                if (_popOnDone) { _popOnDone = false; SoftPop(); }
            } // buffers are persistent - reused next transition, not freed
        }

        /// <summary>
        /// Jumps the rotation layer directly to <paramref name="index"/>, collapsing
        /// any open sub-pages first. Used by the Launcher to "open" a tile.
        /// </summary>
        public void GoTo(int index)
        {
            if (index < 0 || index >= _screens.Length) return;
            SafePause(Current);
            ClearStack();
            _activeIndex = index;
            Debug.WriteLine("[Nav] GoTo rotation index " + _activeIndex);
            SafeResume(_screens[_activeIndex]);
        }

        /// <summary>
        /// Advances the rotation to the next top-level screen. No-op while a
        /// sub-page is open (the tap that would rotate pops the sub-page instead -
        /// see <see cref="HandleTap"/>).
        /// </summary>
        public void Next()
        {
            if (_depth > 0) return;
            int next = (_activeIndex + 1) % _screens.Length;
            if (next == _activeIndex) return;
            SafePause(_screens[_activeIndex]);
            _activeIndex = next;
            Debug.WriteLine("[Nav] Switched to screen index " + _activeIndex);
            SafeResume(_screens[_activeIndex]);
        }

        /// <summary>Rotates to the PREVIOUS top-level screen (wraps). No-op while a sub-page is open.
        /// Wired to swipe-right, the mirror of <see cref="Next"/> (swipe-left).</summary>
        public void Prev()
        {
            if (_depth > 0) return;
            int prev = (_activeIndex - 1 + _screens.Length) % _screens.Length;
            if (prev == _activeIndex) return;
            SafePause(_screens[_activeIndex]);
            _activeIndex = prev;
            Debug.WriteLine("[Nav] Switched (prev) to screen index " + _activeIndex);
            SafeResume(_screens[_activeIndex]);
        }

        /// <summary>
        /// Returns to the home screen (rotation index 0), collapsing any open
        /// sub-pages. Wired to the long-press gesture so a held finger always
        /// gets back to the watchface no matter how deep the user navigated.
        /// </summary>
        public void GoHome()
        {
            SafePause(Current);
            ClearStack();
            _activeIndex = 0;
            Debug.WriteLine("[Nav] GoHome");
            SafeResume(_screens[0]);
        }

        /// <summary>
        /// Routes a tap to the visible screen. If the screen consumes it we stay;
        /// otherwise we pop a sub-page (if any) or rotate to the next top-level
        /// screen at the base level.
        /// </summary>
        public void HandleTap(int x, int y)
        {
            bool consumed = false;
            try { consumed = Current.OnTap(x, y); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnTap EX " + ex.Message); }
            Debug.WriteLine("[Nav] tap=(" + x + "," + y + ") consumed=" + consumed + " depth=" + _depth);
            if (consumed) return;
            if (_depth > 0) RequestBack(); // animated back (slides out if the screen supports it)
            // Top-level paging is swipe-only now (removed: else Next()) - a stray tap no longer jumps
            // screens. Sub-pages still pop on an unconsumed tap; the BOOT button is the primary Back.
        }

        private void ClearStack()
        {
            for (int i = 0; i < _depth; i++) _stack[i] = null;
            _depth = 0;
        }

        private void SafePause(IScreen s)
        {
            try { s.OnPause(); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnPause EX " + ex.Message); }
        }

        private void SafeResume(IScreen s)
        {
            try { s.OnResume(); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnResume EX " + ex.Message); }
        }
    }
}
