namespace SpawnWear.UI
{
    /// <summary>
    /// Minimal screen contract for a SpawnWear UI. Each "screen" owns its own
    /// rendering against a shared framebuffer and reacts to lifecycle events
    /// (becoming visible, going away, the user tapping).
    ///
    /// Mirrors the role of an Android Activity at this level - the
    /// <see cref="ScreenNavigator"/> calls <see cref="OnResume"/> when the
    /// screen becomes the active screen and <see cref="OnPause"/> when it
    /// stops being active. Screens are responsible for clearing their own
    /// pixels on first paint via <see cref="Invalidate"/>; the navigator
    /// guarantees Invalidate is called before the first Tick after a screen
    /// switch.
    /// </summary>
    public interface IScreen
    {
        /// <summary>
        /// Called once per main-loop wake while this screen is active. Should
        /// repaint whatever needs repainting and return - the loop will sleep
        /// again until the next tick or external event.
        /// </summary>
        void Tick();

        /// <summary>
        /// Forces the next <see cref="Tick"/> to do a full repaint (clearing
        /// any leftover pixels from a previous screen or wake-from-sleep
        /// state).
        /// </summary>
        void Invalidate();

        /// <summary>
        /// Called by the navigator when the screen becomes active. Default
        /// implementation should at least invalidate so the first tick paints.
        /// </summary>
        void OnResume();

        /// <summary>
        /// Called by the navigator when the screen is being switched away
        /// from. Screens that hold expensive resources (timers, sensor
        /// streams) should release them here.
        /// </summary>
        void OnPause();

        /// <summary>
        /// Called by the navigator on a single-finger tap inside the panel.
        /// Default behavior is to do nothing; screens that want per-tap
        /// behavior can override. Returns true if the screen consumed the
        /// tap (no navigator-level cycling); false to let the navigator
        /// switch to the next screen.
        /// </summary>
        bool OnTap(int x, int y);
    }
}
