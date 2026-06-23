using System.Collections;
using System.Drawing;

namespace SpawnDev.UI
{
    /// <summary>
    /// Retained-mode UI element - the base of the tree, mirroring GameUI's
    /// UIElement (position/size, children, draw, hit-test) but lean and
    /// nanoFramework-compatible (ArrayList, no generics). Widgets override Draw
    /// and, if interactive, OnTap.
    /// </summary>
    public class UIElement
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public bool Visible = true;

        protected readonly ArrayList Children = new ArrayList();

        public UIElement Add(UIElement child)
        {
            Children.Add(child);
            return child;
        }

        /// <summary>Draws this element then its children. Override to paint, then
        /// call base.Draw to paint children on top.</summary>
        public virtual void Draw(IUiSurface s)
        {
            if (!Visible) return;
            for (int i = 0; i < Children.Count; i++) ((UIElement)Children[i]).Draw(s);
        }

        public bool Contains(int px, int py)
        {
            return px >= X && px < X + Width && py >= Y && py < Y + Height;
        }

        /// <summary>Routes a tap. Default offers it to children top-most first; the
        /// first child that consumes it wins. Returns true if consumed.</summary>
        public virtual bool OnTap(int px, int py)
        {
            if (!Visible) return false;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var c = (UIElement)Children[i];
                if (c.Visible && c.Contains(px, py) && c.OnTap(px, py)) return true;
            }
            return false;
        }

        /// <summary>Position this element's children within its own bounds. Default: each child lays
        /// out its own subtree (children are absolutely positioned). Containers like
        /// <see cref="UIColumn"/> override this to place their children. The host calls this on the
        /// root once after sizing it, before <see cref="Draw"/>.</summary>
        public virtual void Layout()
        {
            for (int i = 0; i < Children.Count; i++) ((UIElement)Children[i]).Layout();
        }
    }

    /// <summary>Container with an optional solid background (mirrors GameUI UIPanel).</summary>
    public class UIPanel : UIElement
    {
        public Color Background = Color.Black;
        public bool Filled = true;

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            if (Filled) s.DrawRect(X, Y, Width, Height, Background);
            base.Draw(s);
        }
    }

    /// <summary>Text element, optionally centered in its bounds (mirrors GameUI UILabel).</summary>
    public class UILabel : UIElement
    {
        public string Text = "";
        public int Scale = 4;
        public Color Color = Color.White;
        public bool Center;

        public override void Draw(IUiSurface s)
        {
            if (!Visible || Text == null || Text.Length == 0) return;
            int th = s.TextHeight(Scale);
            int tx = Center ? X + (Width - s.MeasureText(Text, Scale)) / 2 : X;
            int ty = Y + (Height - th) / 2;
            s.DrawText(Text, tx, ty, Scale, Color);
        }
    }

    /// <summary>Tappable button: filled background + centered label, fires Clicked
    /// when tapped (mirrors GameUI UIButton, minus hover/press states for now).</summary>
    public class UIButton : UIElement
    {
        public delegate void ClickHandler();

        public string Text = "";
        public int Scale = 4;
        public Color Background = Theme.Current.Surface;
        public Color Foreground = Theme.Current.OnSurface;
        public int CornerRadius = Theme.Current.Radius;
        public bool Pressed;   // visual press-state, driven by the gesture layer (animation lands there)
        public ClickHandler Clicked;

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            Color bg = Pressed ? Theme.Current.SurfacePressed : Background;
            Shapes.RoundedRect(s, X, Y, Width, Height, CornerRadius, bg);
            int tw = s.MeasureText(Text, Scale);
            int th = s.TextHeight(Scale);
            s.DrawText(Text, X + (Width - tw) / 2, Y + (Height - th) / 2, Scale, Foreground);
        }

        public override bool OnTap(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            if (Clicked != null) Clicked();
            return true;
        }
    }

    /// <summary>Vertical stack layout: places its children top-to-bottom within its own bounds, each
    /// stretched to the column's content width (kept at its own Height), separated by Spacing. Set the
    /// column's X/Y/Width/Height (e.g. a safe-area band) and add children; the host calls Layout.</summary>
    public class UIColumn : UIElement
    {
        public int Spacing = 10;
        public int PadTop, PadBottom, PadLeft, PadRight;

        public override void Layout()
        {
            int y = Y + PadTop;
            int cw = Width - PadLeft - PadRight;
            for (int i = 0; i < Children.Count; i++)
            {
                var c = (UIElement)Children[i];
                if (!c.Visible) continue;
                c.X = X + PadLeft;
                c.Y = y;
                c.Width = cw;
                c.Layout();
                y += c.Height + Spacing;
            }
        }
    }

    /// <summary>A labelled on/off toggle row (themed): label on the left, a pill track + knob on the
    /// right. Tapping flips <see cref="On"/> and fires <see cref="Toggled"/>.</summary>
    public class UISwitch : UIElement
    {
        public delegate void ToggleHandler(bool on);

        public string Text = "";
        public int Scale = 4;
        public bool On;
        public ToggleHandler Toggled;

        public UISwitch() { Height = Theme.Current.RowHeight; }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            var t = Theme.Current;
            Shapes.RoundedRect(s, X, Y, Width, Height, t.Radius, t.Surface);   // rounded row/card
            int th = s.TextHeight(Scale);
            s.DrawText(Text, X + t.CornerInset + 14, Y + (Height - th) / 2, Scale, t.OnSurface);
            // capsule track + circular knob on the right
            int trackW = 70, trackH = 36;
            int tx = X + Width - trackW - t.CornerInset - 14;
            int ty = Y + (Height - trackH) / 2;
            Shapes.RoundedRect(s, tx, ty, trackW, trackH, trackH / 2, On ? t.Accent : t.Divider);
            int knob = trackH - 8;
            int kx = On ? (tx + trackW - knob - 4) : (tx + 4);
            Shapes.RoundedRect(s, kx, ty + 4, knob, knob, knob / 2, On ? t.OnAccent : t.Muted);
        }

        public override bool OnTap(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            On = !On;
            if (Toggled != null) Toggled(On);
            return true;
        }
    }
}
