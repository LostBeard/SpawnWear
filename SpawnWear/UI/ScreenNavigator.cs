using System.Diagnostics;

namespace SpawnWear.UI
{
    /// <summary>
    /// Tiny stack-less screen manager. Owns a fixed-size array of registered
    /// <see cref="IScreen"/> implementations and exposes a "next" cycle for
    /// tap-driven navigation. Phase 2 will replace this with a proper
    /// navigation stack (push / pop with back-button support); for V1 the
    /// navigator just rotates through the registered screens in order.
    /// </summary>
    public class ScreenNavigator
    {
        private readonly IScreen[] _screens;
        private int _activeIndex;

        public ScreenNavigator(IScreen[] screens)
        {
            _screens = screens;
            _activeIndex = 0;
            // First screen is implicitly active. Caller is responsible for
            // calling Invalidate / Tick on the first iteration.
        }

        public IScreen Current => _screens[_activeIndex];

        /// <summary>
        /// Switches to the next registered screen, wrapping around at the end.
        /// Pauses the outgoing screen and resumes the incoming one. Caller
        /// must trigger a Tick after this returns to paint the new screen.
        /// </summary>
        public void Next()
        {
            int next = (_activeIndex + 1) % _screens.Length;
            if (next == _activeIndex) return;
            try { _screens[_activeIndex].OnPause(); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnPause EX " + ex.Message); }
            _activeIndex = next;
            Debug.WriteLine("[Nav] Switched to screen index " + _activeIndex);
            try { _screens[_activeIndex].OnResume(); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnResume EX " + ex.Message); }
        }

        /// <summary>
        /// Routes a tap event to the active screen. If the screen consumes the
        /// tap (returns true) we stay; otherwise we cycle to the next screen.
        /// </summary>
        public void HandleTap(int x, int y)
        {
            bool consumed = false;
            try { consumed = _screens[_activeIndex].OnTap(x, y); }
            catch (System.Exception ex) { Debug.WriteLine("[Nav] OnTap EX " + ex.Message); }
            if (!consumed) Next();
        }
    }
}
