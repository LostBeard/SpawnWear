using SpawnWear.AppContracts;
using System.Drawing;

namespace CounterApp
{
    /// <summary>
    /// SpawnWear demo app: tap-to-increment counter. Loads at runtime via
    /// HTTP POST /loadapp; tapping the watch screen increments a counter
    /// shown center-screen in big white text.
    ///
    /// Demonstrates the full ISpawnApp lifecycle (OnCreate / OnResume /
    /// OnPause / OnDestroy / Tick / OnTap) and IDisplayBuffer rendering.
    /// </summary>
    public class CounterApp : ISpawnApp
    {
        int _count;
        bool _dirty = true;
        IServiceHost _services;

        public string Name => "COUNTER";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _count = 0;
            _dirty = true;
            return true;
        }

        public void OnResume(IDisplayBuffer fb)
        {
            _dirty = true;
            Render(fb);
        }

        public void OnPause()
        {
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
            _count++;
            _dirty = true;
            return true;
        }

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int h = fb.PanelHeight;
            int top = fb.StatusBarHeight;
            int bottom = h - fb.PageIndicatorHeight;

            fb.Clear(Color.FromArgb(15, 15, 35));

            // Title
            string title = "TAP TO COUNT";
            int titleScale = 3;
            int titleW = fb.MeasureString(title, titleScale);
            fb.DrawString(title, (w - titleW) / 2, top + 30, titleScale, Color.FromArgb(160, 160, 200));

            // Big number
            string num = _count.ToString();
            int numScale = 8;
            int numW = fb.MeasureString(num, numScale);
            int numH = 7 * numScale;
            int numX = (w - numW) / 2;
            int numY = (top + bottom - numH) / 2;
            fb.DrawString(num, numX, numY, numScale, Color.White);

            // Footer hint
            string hint = "LONG PRESS = HOME";
            int hintScale = 2;
            int hintW = fb.MeasureString(hint, hintScale);
            fb.DrawString(hint, (w - hintW) / 2, bottom - 30, hintScale, Color.FromArgb(120, 120, 140));

            fb.Flush();
            _dirty = false;
        }
    }
}
