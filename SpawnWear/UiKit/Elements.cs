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

        /// <summary>Finger pressed at (px,py): route top-most-first to the deepest interactive child
        /// containing the point so it can show a pressed state. Returns true if a child took it. (Raw
        /// down; the classified tap still comes via <see cref="OnTap"/> on release.)</summary>
        public virtual bool OnPress(int px, int py)
        {
            if (!Visible) return false;
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var c = (UIElement)Children[i];
                if (c.Visible && c.Contains(px, py) && c.OnPress(px, py)) return true;
            }
            return false;
        }

        /// <summary>Finger released anywhere: clear any pressed state in the subtree.</summary>
        public virtual void OnRelease()
        {
            for (int i = 0; i < Children.Count; i++) ((UIElement)Children[i]).OnRelease();
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

        public override bool OnPress(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            Pressed = true;
            return true;
        }

        public override void OnRelease() { Pressed = false; }
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

    /// <summary>A vertical stack that SCROLLS: children are laid out top-to-bottom but shifted up by
    /// <see cref="ScrollOffset"/> and clipped to the column's own bounds (its viewport). Drag-scroll feeds
    /// <see cref="Scroll"/>. Use for lists taller than the screen (e.g. Settings) - the fixed chrome
    /// (status bar / page dots) stays put because it's outside this container.</summary>
    public class UIScrollColumn : UIElement
    {
        public int Spacing = 10;
        public int ScrollOffset;
        private int _contentHeight;

        private int MaxScroll { get { int m = _contentHeight - Height; return m > 0 ? m : 0; } }

        public override void Layout()
        {
            int y = Y - ScrollOffset;
            int total = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                var c = (UIElement)Children[i];
                if (!c.Visible) continue;
                c.X = X;
                c.Y = y;
                c.Width = Width;
                c.Layout();
                y += c.Height + Spacing;
                total += c.Height + Spacing;
            }
            if (total > 0) total -= Spacing;
            _contentHeight = total;
            if (ScrollOffset > MaxScroll) ScrollOffset = MaxScroll;
            if (ScrollOffset < 0) ScrollOffset = 0;
        }

        /// <summary>Scroll by a finger delta (drag down = fingerDy&gt;0 = content moves down / earlier rows).</summary>
        public void Scroll(int fingerDy)
        {
            ScrollOffset -= fingerDy;
            if (ScrollOffset < 0) ScrollOffset = 0;
            if (ScrollOffset > MaxScroll) ScrollOffset = MaxScroll;
        }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            s.SetClip(X, Y, Width, Height);
            base.Draw(s);      // children at their scrolled positions, clipped to the viewport
            s.ClearClip();
        }

        public override bool OnTap(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false; // only taps inside the viewport reach rows
            return base.OnTap(px, py);
        }

        public override bool OnPress(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            return base.OnPress(px, py);
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

    /// <summary>A labelled value slider (themed): a full-width rounded track that fills with the accent
    /// color up to <see cref="Value"/> (percent of the [<see cref="Min"/>,<see cref="Max"/>] range), with
    /// the label on the left and the value on the right, both drawn over the bar. Tapping (or, once the
    /// gesture layer reports moves, dragging) anywhere on the track sets the value to that position and
    /// fires <see cref="Changed"/>. Used for brightness in the quick-settings panel.</summary>
    public class UISlider : UIElement
    {
        public delegate void ChangeHandler(int value);

        public string Text = "";
        public int Scale = 4;
        public int Min = 0;
        public int Max = 100;
        public int Value = 50;
        public ChangeHandler Changed;

        public UISlider() { Height = Theme.Current.RowHeight; }

        // Map an absolute x within the track to a clamped value in [Min,Max].
        private int ValueForX(int px)
        {
            int rel = px - X;
            if (rel < 0) rel = 0;
            if (rel > Width) rel = Width;
            if (Width <= 0) return Min;
            return Min + (rel * (Max - Min)) / Width;
        }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            var t = Theme.Current;
            int range = Max - Min; if (range <= 0) range = 1;
            int v = Value; if (v < Min) v = Min; if (v > Max) v = Max;

            // Track then accent fill up to the value. The fill is at least a rounded-cap wide so its left
            // end matches the track's rounded corner; a pill-shaped fill reads as the filled portion.
            Shapes.RoundedRect(s, X, Y, Width, Height, t.Radius, t.Surface);
            int fillW = ((v - Min) * Width) / range;
            if (fillW < Height) fillW = fillW > 0 ? Height : 0; // avoid a sliver; 0 stays empty
            if (fillW > 0) Shapes.RoundedRect(s, X, Y, fillW, Height, t.Radius, t.Accent);

            int th = s.TextHeight(Scale);
            int ty = Y + (Height - th) / 2;
            s.DrawText(Text, X + t.CornerInset + 14, ty, Scale, t.OnSurface);
            string val = v.ToString() + "%";
            int vw = s.MeasureText(val, Scale);
            s.DrawText(val, X + Width - vw - t.CornerInset - 14, ty, Scale, t.OnSurface);
        }

        public override bool OnTap(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            Value = ValueForX(px);
            if (Changed != null) Changed(Value);
            return true;
        }

        // Pressing on the track sets the value immediately (so a press-drag, once the gesture layer
        // reports moves, reads naturally); returns true to claim the press.
        public override bool OnPress(int px, int py)
        {
            if (!Visible || !Contains(px, py)) return false;
            Value = ValueForX(px);
            if (Changed != null) Changed(Value);
            return true;
        }
    }

    /// <summary>A tappable settings/list row: rounded surface with a label on the left and a value/state
    /// on the right; darkens while pressed and fires <see cref="Tapped"/> on tap. Rows with no
    /// <see cref="Tapped"/> handler are informational (don't consume the tap).</summary>
    public class UIListRow : UIElement
    {
        public delegate void TapHandler();

        public string Label = "";
        public string Value = "";
        public int Scale = Theme.Current.BodyScale;
        public TapHandler Tapped;
        public bool Pressed;

        public UIListRow() { Height = 40; }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            var t = Theme.Current;
            Color bg = Pressed ? t.SurfacePressed : t.Surface;
            Shapes.RoundedRect(s, X, Y, Width, Height, t.Radius, bg);
            int th = s.TextHeight(Scale);
            int ty = Y + (Height - th) / 2;
            s.DrawText(Label, X + t.CornerInset + 14, ty, Scale, t.OnSurface);
            if (Value != null && Value.Length > 0)
            {
                int vw = s.MeasureText(Value, Scale);
                s.DrawText(Value, X + Width - vw - t.CornerInset - 14, ty, Scale, t.Muted);
            }
        }

        public override bool OnTap(int px, int py)
        {
            if (!Visible || !Contains(px, py) || Tapped == null) return false;
            Tapped();
            return true;
        }

        public override bool OnPress(int px, int py)
        {
            if (!Visible || !Contains(px, py) || Tapped == null) return false;
            Pressed = true;
            return true;
        }

        public override void OnRelease() { Pressed = false; }
    }

    /// <summary>Draws a single <see cref="UiIcon"/> centered + scaled to fit its bounds. For screen-scale
    /// glyphs (e.g. the WiFi signal on the WiFi page) rather than tile-scale.</summary>
    public class UIIcon : UIElement
    {
        public UiIcon Icon = UiIcon.None;
        public Color Color = Theme.Current.OnSurface;

        public override void Draw(IUiSurface s)
        {
            if (!Visible || Icon == UiIcon.None) return;
            int size = Width < Height ? Width : Height;
            Icons.Draw(s, Icon, X + (Width - size) / 2, Y + (Height - size) / 2, size, Color, Theme.Current.Background);
        }
    }

    /// <summary>Bottom page-position indicator: one dot per rotation screen, the active one a wide pill.
    /// Position it full-width where you want the dots' baseline; it centers the row horizontally.</summary>
    public class UIPageDots : UIElement
    {
        public int ActiveIndex;
        public int Total;

        const int InactiveDotSize = 10;
        const int ActiveDotWidth = 28;
        const int ActiveDotHeight = 10;
        const int DotGap = 12;

        public override void Draw(IUiSurface s)
        {
            if (!Visible || Total <= 1) return;
            var t = Theme.Current;
            int totalWidth = (Total - 1) * InactiveDotSize + ActiveDotWidth + (Total - 1) * DotGap;
            int cursor = X + (Width - totalWidth) / 2;
            for (int i = 0; i < Total; i++)
            {
                if (i == ActiveIndex)
                {
                    Shapes.RoundedRect(s, cursor, Y, ActiveDotWidth, ActiveDotHeight, ActiveDotHeight / 2, t.OnSurface);
                    cursor += ActiveDotWidth + DotGap;
                }
                else
                {
                    int dy = Y + (ActiveDotHeight - InactiveDotSize) / 2;
                    Shapes.RoundedRect(s, cursor, dy, InactiveDotSize, InactiveDotSize, InactiveDotSize / 2, t.Divider);
                    cursor += InactiveDotSize + DotGap;
                }
            }
        }
    }

    /// <summary>A read-only info row: a label on the left and a value right-aligned, both on one line.
    /// The staple of the About/WiFi/Stats-style screens. Mutate <see cref="Value"/> between ticks and
    /// repaint to update live readouts.</summary>
    public class UIKeyValue : UIElement
    {
        public string Label = "";
        public string Value = "";
        public int Scale = Theme.Current.SmallScale;
        public Color LabelColor = Theme.Current.Muted;
        public Color ValueColor = Theme.Current.OnSurface;

        public UIKeyValue() { Height = 36; }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            int th = s.TextHeight(Scale);
            int ty = Y + (Height - th) / 2;
            if (Label != null && Label.Length > 0) s.DrawText(Label, X, ty, Scale, LabelColor);
            if (Value != null && Value.Length > 0)
            {
                int vw = s.MeasureText(Value, Scale);
                s.DrawText(Value, X + Width - vw, ty, Scale, ValueColor);
            }
        }
    }

    /// <summary>Horizontal stack layout: places its visible children left-to-right within its own bounds,
    /// each stretched to an equal share of the width (minus <see cref="Spacing"/>) and to the row's Height.
    /// Pair with <see cref="UIColumn"/> to build a grid (a column of rows).</summary>
    public class UIRow : UIElement
    {
        public int Spacing = 10;

        public override void Layout()
        {
            int n = 0;
            for (int i = 0; i < Children.Count; i++) if (((UIElement)Children[i]).Visible) n++;
            if (n == 0) return;
            int cw = (Width - (n - 1) * Spacing) / n;
            int x = X;
            for (int i = 0; i < Children.Count; i++)
            {
                var c = (UIElement)Children[i];
                if (!c.Visible) continue;
                c.X = x;
                c.Y = Y;
                c.Width = cw;
                c.Height = Height;
                c.Layout();
                x += cw + Spacing;
            }
        }
    }

    /// <summary>Android-quick-settings-style toggle tile: a rounded tile that lights up (accent fill) when
    /// <see cref="On"/> and sits dark (surface fill) when off, with a centered label. Tapping flips
    /// <see cref="On"/> and fires <see cref="Toggled"/>. Grid these with <see cref="UIRow"/> +
    /// <see cref="UIColumn"/> for the drop-down panel.</summary>
    public class UITile : UIElement
    {
        public delegate void ToggleHandler(bool on);

        public string Text = "";
        public int Scale = Theme.Current.BodyScale;
        public bool On;
        public UiIcon Icon = UiIcon.None;
        public ToggleHandler Toggled;

        public UITile() { Height = 96; }

        public override void Draw(IUiSurface s)
        {
            if (!Visible) return;
            var t = Theme.Current;
            Color bg = On ? t.Accent : t.Surface;
            Color fg = On ? t.OnAccent : t.Muted;
            Shapes.RoundedRect(s, X, Y, Width, Height, t.Radius, bg);

            int th = s.TextHeight(Scale);
            if (Icon != UiIcon.None)
            {
                // Icon in the upper area, label in the lower - Android quick-tile layout. The accent fill
                // already signals on/off, so no separate pip.
                int iconSize = (Height * 44) / 100;
                Icons.Draw(s, Icon, X + (Width - iconSize) / 2, Y + Height / 8, iconSize, fg, bg);
                int tw = s.MeasureText(Text, Scale);
                s.DrawText(Text, X + (Width - tw) / 2, Y + Height - th - Height / 10, Scale, fg);
            }
            else
            {
                int tw = s.MeasureText(Text, Scale);
                s.DrawText(Text, X + (Width - tw) / 2, Y + (Height - th) / 2, Scale, fg);
            }
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
