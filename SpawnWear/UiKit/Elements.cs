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
        public Color Background = Color.Gray;
        public Color Foreground = Color.White;
        public ClickHandler Clicked;

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            s.DrawRect(X, Y, Width, Height, Background);
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
}
