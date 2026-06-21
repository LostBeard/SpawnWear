using SpawnWear.AppContracts;
using System.Drawing;

namespace AppTemplate
{
    /// <summary>
    /// SpawnWear app starter template. Fork this project, rename the class +
    /// AssemblyName, edit the Render method, build to produce a tiny .pe
    /// that you can drop onto http://&lt;watch-ip&gt;:8080/ to run on the watch.
    ///
    /// Lifecycle order:
    ///   1. Constructor (parameterless, must exist)
    ///   2. OnCreate - capture the IServiceHost reference, set up state
    ///   3. OnResume - first time the screen becomes visible, draw
    ///   4. Tick - 1 Hz idle, ~60 Hz while finger held; redraw if dirty
    ///   5. OnTap - finger lifted after a short tap; mutate state + flag dirty
    ///   6. OnPause - user navigated away; stop timers, free transient state
    ///   7. OnDestroy - app is being unloaded; drop service references
    /// </summary>
    public class MyApp : ISpawnApp
    {
        IServiceHost _services;
        bool _dirty = true;

        // The label shown on the launcher tile / app browser.
        public string Name => "MY APP";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            // Return false to refuse activation (e.g. required service missing).
            // var rtc = services.GetRtc(); if (!rtc.IsValid) return false;
            return true;
        }

        public void OnResume(IDisplayBuffer fb)
        {
            _dirty = true;
            Render(fb);
        }

        public void OnPause()
        {
            // Stop anything that shouldn't keep running while the user is
            // looking at a different app. Don't drop service references yet -
            // the app may be resumed shortly.
        }

        public void OnDestroy()
        {
            _services = null;
        }

        public void Tick(IDisplayBuffer fb)
        {
            if (_dirty) Render(fb);
        }

        public bool OnTap(int x, int y)
        {
            // Return true to consume the tap (the launcher's tap-to-cycle
            // navigation won't fire). Return false to let the navigator
            // handle it.
            _dirty = true;
            return true;
        }

        // -- Rendering helpers -------------------------------------------

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int h = fb.PanelHeight;
            int top = fb.StatusBarHeight;
            int bottom = h - fb.PageIndicatorHeight;

            // Fill with a background color (AMOLED black is free in power
            // terms, but a tinted background helps the app feel distinct).
            fb.Clear(Color.FromArgb(20, 20, 30));

            // Title at the top of the content area.
            string title = Name;
            int titleScale = 3;
            int titleW = fb.MeasureString(title, titleScale);
            fb.DrawString(title, (w - titleW) / 2, top + 30, titleScale, Color.White);

            // Body text. Replace this with whatever your app actually draws.
            string body = "FORK ME!";
            int bodyScale = 5;
            int bodyW = fb.MeasureString(body, bodyScale);
            int bodyH = 7 * bodyScale;
            fb.DrawString(body, (w - bodyW) / 2, (top + bottom - bodyH) / 2, bodyScale, Color.LimeGreen);

            // Footer hint.
            string hint = "LONG PRESS = HOME";
            int hintScale = 2;
            int hintW = fb.MeasureString(hint, hintScale);
            fb.DrawString(hint, (w - hintW) / 2, bottom - 30, hintScale, Color.FromArgb(120, 120, 140));

            fb.Flush();
            _dirty = false;
        }
    }
}
