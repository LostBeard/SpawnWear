using SpawnWear.AppContracts;
using System;
using System.Drawing;

namespace DiceApp
{
    /// <summary>
    /// SpawnWear demo app: tap-to-roll a six-sided die. Result drawn as
    /// classic dice pips (filled circles in the canonical 3x3 grid).
    ///
    /// Random source: System.Random seeded from DateTime.UtcNow.Ticks at
    /// OnCreate time. Adequate for a watch toy; not cryptographic.
    /// </summary>
    public class DiceApp : ISpawnApp
    {
        IServiceHost _services;
        Random _rng;
        int _roll = 1;
        bool _dirty = true;
        long _rollAtTicks;

        public string Name => "DICE";

        public bool OnCreate(IServiceHost services)
        {
            _services = services;
            _rng = new Random((int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF));
            _roll = 1;
            _dirty = true;
            return true;
        }

        public void OnResume(IDisplayBuffer fb) { _dirty = true; Render(fb); }
        public void OnPause() { }
        public void OnDestroy() { _services = null; _rng = null; }

        public void Tick(IDisplayBuffer fb)
        {
            if (_dirty) Render(fb);
        }

        public bool OnTap(int x, int y)
        {
            _roll = _rng.Next(6) + 1;
            _rollAtTicks = DateTime.UtcNow.Ticks;
            _dirty = true;
            return true;
        }

        void Render(IDisplayBuffer fb)
        {
            int w = fb.PanelWidth;
            int h = fb.PanelHeight;
            int top = fb.StatusBarHeight;
            int bottom = h - fb.PageIndicatorHeight;

            fb.Clear(Color.FromArgb(40, 20, 20));

            // Title.
            string title = "DICE - TAP TO ROLL";
            int titleScale = 2;
            int titleW = fb.MeasureString(title, titleScale);
            fb.DrawString(title, (w - titleW) / 2, top + 30, titleScale, Color.FromArgb(220, 180, 180));

            // Big die face: a white rounded-ish square with pips for the rolled value.
            int dieSize = 220;
            int dieX = (w - dieSize) / 2;
            int dieY = (top + bottom - dieSize) / 2;
            DrawDieFace(fb, dieX, dieY, dieSize, _roll);

            // Numeric label below the die.
            string num = "= " + _roll.ToString();
            int numScale = 4;
            int numW = fb.MeasureString(num, numScale);
            fb.DrawString(num, (w - numW) / 2, dieY + dieSize + 16, numScale, Color.White);

            fb.Flush();
            _dirty = false;
        }

        // Draw a white die face with classical pip layout for value 1..6.
        // pipSize = die_size / 8; corners + center pattern matches a real die.
        static void DrawDieFace(IDisplayBuffer fb, int x, int y, int size, int value)
        {
            // Background.
            fb.FillRectangle(x, y, size, size, Color.WhiteSmoke);
            // Knock corners for a softer look.
            int notch = 8;
            fb.FillRectangle(x, y, notch, notch, Color.FromArgb(40, 20, 20));
            fb.FillRectangle(x + size - notch, y, notch, notch, Color.FromArgb(40, 20, 20));
            fb.FillRectangle(x, y + size - notch, notch, notch, Color.FromArgb(40, 20, 20));
            fb.FillRectangle(x + size - notch, y + size - notch, notch, notch, Color.FromArgb(40, 20, 20));

            int pipSize = size / 7;
            int margin = size / 5;
            // 3x3 grid positions.
            int cx = x + size / 2;
            int cy = y + size / 2;
            int lx = x + margin, rx = x + size - margin - pipSize;
            int ty = y + margin, by = y + size - margin - pipSize;
            int mx = cx - pipSize / 2, my = cy - pipSize / 2;

            Color pip = Color.FromArgb(20, 20, 20);
            // Pip layout per face value.
            // 1: center
            // 2: top-left, bottom-right
            // 3: top-left, center, bottom-right
            // 4: four corners
            // 5: four corners + center
            // 6: 3 left column + 3 right column
            if (value == 1) { Pip(fb, mx, my, pipSize, pip); }
            else if (value == 2) { Pip(fb, lx, ty, pipSize, pip); Pip(fb, rx, by, pipSize, pip); }
            else if (value == 3) { Pip(fb, lx, ty, pipSize, pip); Pip(fb, mx, my, pipSize, pip); Pip(fb, rx, by, pipSize, pip); }
            else if (value == 4) { Pip(fb, lx, ty, pipSize, pip); Pip(fb, rx, ty, pipSize, pip); Pip(fb, lx, by, pipSize, pip); Pip(fb, rx, by, pipSize, pip); }
            else if (value == 5) { Pip(fb, lx, ty, pipSize, pip); Pip(fb, rx, ty, pipSize, pip); Pip(fb, mx, my, pipSize, pip); Pip(fb, lx, by, pipSize, pip); Pip(fb, rx, by, pipSize, pip); }
            else if (value == 6)
            {
                Pip(fb, lx, ty, pipSize, pip); Pip(fb, rx, ty, pipSize, pip);
                Pip(fb, lx, cy - pipSize / 2, pipSize, pip); Pip(fb, rx, cy - pipSize / 2, pipSize, pip);
                Pip(fb, lx, by, pipSize, pip); Pip(fb, rx, by, pipSize, pip);
            }
        }

        static void Pip(IDisplayBuffer fb, int x, int y, int size, Color color)
        {
            // Square pip is fine at the panel's density - true circles would
            // need polygon primitives the framework doesn't ship.
            fb.FillRectangle(x, y, size, size, color);
            // Knock corners for a softer dot.
            int n = size / 4;
            fb.FillRectangle(x, y, n, n, Color.WhiteSmoke);
            fb.FillRectangle(x + size - n, y, n, n, Color.WhiteSmoke);
            fb.FillRectangle(x, y + size - n, n, n, Color.WhiteSmoke);
            fb.FillRectangle(x + size - n, y + size - n, n, n, Color.WhiteSmoke);
        }
    }
}
