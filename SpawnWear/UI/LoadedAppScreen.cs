using System;
using System.Diagnostics;
using nanoFramework.UI;
using SpawnWear.AppContracts;

namespace SpawnWear.UI
{
    /// <summary>
    /// IScreen wrapper around a dynamically-loaded ISpawnApp. The launcher's
    /// app loader sets the wrapped instance via SetApp(), then the navigator
    /// switches to the slot this wrapper occupies. Lifecycle calls forward
    /// to the wrapped app.
    ///
    /// Slot is reserved at boot so the navigator's screens array doesn't
    /// have to grow at runtime (which would invalidate page-dot indices on
    /// every other screen).
    ///
    /// Crash isolation: every callback into the app is wrapped in try/catch.
    /// An exception from the app surfaces as a Debug.WriteLine entry; the
    /// firmware itself never dies for an app's bug.
    /// </summary>
    public class LoadedAppScreen : IScreen
    {
        readonly IServiceHost _services;
        readonly Bitmap _fb;
        readonly int _panelWidth;
        readonly int _panelHeight;
        ISpawnApp _app;
        bool _needsRepaint = true;

        int _pageDotIndex = -1;
        int _pageDotCount = 0;
        public void SetPageDots(int activeIndex, int total) { _pageDotIndex = activeIndex; _pageDotCount = total; }
        StatusBar _statusBar;
        public void SetStatusBar(StatusBar bar) { _statusBar = bar; }

        public LoadedAppScreen(IServiceHost services, Bitmap fb, int panelWidth, int panelHeight)
        {
            _services = services;
            _fb = fb;
            _panelWidth = panelWidth;
            _panelHeight = panelHeight;
        }

        public bool HasApp => _app != null;
        public string AppName => _app != null ? _app.Name : "<empty>";

        /// <summary>Replace the wrapped app. Calls OnDestroy on the previous
        /// app (if any) and OnCreate on the new one. Returns false if
        /// OnCreate refused.</summary>
        public bool SetApp(ISpawnApp app)
        {
            if (_app != null)
            {
                try { _app.OnPause(); } catch (Exception ex) { Debug.WriteLine("[LoadedApp] prev OnPause EX " + ex.Message); }
                try { _app.OnDestroy(); } catch (Exception ex) { Debug.WriteLine("[LoadedApp] prev OnDestroy EX " + ex.Message); }
            }
            _app = app;
            if (_app == null) return true;
            try
            {
                if (!_app.OnCreate(_services))
                {
                    Debug.WriteLine("[LoadedApp] OnCreate returned false; refusing activation");
                    _app = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LoadedApp] OnCreate EX " + ex.GetType().Name + ": " + ex.Message);
                _app = null;
                return false;
            }
            _needsRepaint = true;
            return true;
        }

        public void Invalidate() { _needsRepaint = true; }

        public void OnResume()
        {
            _needsRepaint = true;
            if (_app == null) { Tick(); return; }
            var fb = _services.GetDisplay();
            try { _app.OnResume(fb); }
            catch (Exception ex) { Debug.WriteLine("[LoadedApp] OnResume EX " + ex.Message); }
            // Render system chrome over whatever the app drew.
            if (_pageDotCount > 1) PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
            _statusBar?.Render(force: true);
        }

        public void OnPause()
        {
            if (_app == null) return;
            try { _app.OnPause(); }
            catch (Exception ex) { Debug.WriteLine("[LoadedApp] OnPause EX " + ex.Message); }
        }

        public bool OnTap(int x, int y)
        {
            if (_app == null) return false;
            try { return _app.OnTap(x, y); }
            catch (Exception ex) { Debug.WriteLine("[LoadedApp] OnTap EX " + ex.Message); return false; }
        }

        public void Tick()
        {
            var fb = _services.GetDisplay();
            if (_app == null)
            {
                if (_needsRepaint)
                {
                    fb.Clear(System.Drawing.Color.Black);
                    fb.DrawString("NO APP LOADED", 60, 200, 3, System.Drawing.Color.FromArgb(150, 150, 150));
                    fb.DrawString("POST .pe TO /LOADAPP", 50, 240, 2, System.Drawing.Color.FromArgb(120, 120, 120));
                    if (_pageDotCount > 1) PageDots.Render(_fb, _panelWidth, _panelHeight, _pageDotIndex, _pageDotCount);
                    fb.Flush();
                    _statusBar?.Render(force: true);
                    _needsRepaint = false;
                }
                else
                {
                    _statusBar?.Render(force: false);
                }
                return;
            }

            try { _app.Tick(fb); }
            catch (Exception ex) { Debug.WriteLine("[LoadedApp] Tick EX " + ex.Message); }
            _statusBar?.Render(force: false);
        }
    }
}
