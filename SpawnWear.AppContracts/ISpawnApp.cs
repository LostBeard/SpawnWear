namespace SpawnWear.AppContracts
{
    /// <summary>
    /// Minimal contract every external SpawnWear app implements. The launcher
    /// activates exactly one app at a time; this interface is the lifecycle
    /// + render surface the firmware drives.
    ///
    /// Apps live on the launcher's framebuffer and react to touch + tick
    /// events on the same UI thread (no per-app threading). Background work
    /// goes through services owned by the firmware.
    ///
    /// Apps MUST be cheap to instantiate. The constructor runs at the moment
    /// the user activates the app; long-running setup goes in OnCreate.
    /// </summary>
    public interface ISpawnApp
    {
        /// <summary>Display label shown on the launcher tile / window title.</summary>
        string Name { get; }

        /// <summary>Called once after the app is loaded from disk and before
        /// any other method. Capture the IServiceHost reference for later use.
        /// Return false to refuse activation.</summary>
        bool OnCreate(IServiceHost services);

        /// <summary>Called when the app becomes the foreground screen. Repaint
        /// the framebuffer here; the firmware guarantees the panel is in
        /// Active state and the framebuffer is yours to scribble on (within
        /// the inset described in IDisplayBuffer).</summary>
        void OnResume(IDisplayBuffer fb);

        /// <summary>Called when the app stops being the foreground screen.
        /// Stop timers, flush pending I/O, free GC roots that don't need
        /// to survive into background.</summary>
        void OnPause();

        /// <summary>Called once when the app is being unloaded.</summary>
        void OnDestroy();

        /// <summary>Called by the firmware's event loop while the app is
        /// active. The firmware decides the tick budget. Apps should NOT
        /// spin in a loop here - return promptly.</summary>
        void Tick(IDisplayBuffer fb);

        /// <summary>Called on a single-finger tap inside the panel.
        /// Coordinates in raw panel pixels. Return true to consume the tap.</summary>
        bool OnTap(int x, int y);
    }
}
