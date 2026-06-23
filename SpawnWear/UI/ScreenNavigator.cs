using System.Diagnostics;

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

        public ScreenNavigator(IScreen[] screens)
        {
            _screens = screens;
            _activeIndex = 0;
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
            SafePause(_stack[_depth - 1]);
            _stack[--_depth] = null;
            Debug.WriteLine("[Nav] Pop -> depth " + _depth);
            SafeResume(Current);
            return true;
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
            if (_depth > 0) Pop();
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
