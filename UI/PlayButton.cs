using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A round play button: a soft accent-tinted circle with the accent triangle (a square while <see cref="Playing"/>, as a
/// stop button), filled with the accent on hover and pressed in slightly on click. Always as tall as the control it plays for (<see cref="MatchHeight"/>), so a row lines up
/// at any display scaling.
/// </summary>
public sealed class PlayButton : Control
{
    private bool _hover, _pressed, _playing;

    /// <summary>Its sound is playing: shows ■ (stop) instead of ▶.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Playing
    {
        get => _playing;
        set
        {
            if (_playing == value)
                return;
            _playing = value;
            AccessibleName = value ? "Stop" : "Play";
            Invalidate();
            PlayingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary><see cref="Playing"/> changed (e.g. for the tooltip: "Stop" while playing).</summary>
    public event EventHandler? PlayingChanged;

    public PlayButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = "Play";
        Size = new Size(36, 36);
    }

    /// <summary>Keeps this button square and as tall as <paramref name="other"/>.</summary>
    public void MatchHeight(Control other)
    {
        void Fit() => Size = new Size(other.Height, other.Height);
        other.SizeChanged += (_, _) => Fit();
        Fit();
    }

    /// <summary>The size comes from the matched control, not from the page's scaling.</summary>
    protected override void ScaleControl(SizeF factor, BoundsSpecified specified) =>
        base.ScaleControl(factor, specified & ~BoundsSpecified.Size);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        var inset = (_pressed ? 2.5f : 1.5f) * s;
        var circle = new RectangleF(inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
        var active = _hover || Focused;

        using (var fill = new SolidBrush(!Enabled ? Theme.Border : active ? Theme.Accent : Theme.AccentSoft))
            g.FillEllipse(fill, circle);
        if (!active && Enabled)
        {
            using var ring = new Pen(Color.FromArgb(90, Theme.Accent), 1.2f * s);
            g.DrawEllipse(ring, circle);
        }

        var h = circle.Height * 0.2f;
        using var mark = new SolidBrush(!Enabled ? Theme.TextMuted : active ? Theme.OnAccent : Theme.Accent);
        if (_playing)
        {
            // A rounded square: stop.
            float sx = circle.X + circle.Width / 2f, sy = circle.Y + circle.Height / 2f, half = h * 0.8f;
            using var square = Theme.RoundedRect(new RectangleF(sx - half, sy - half, 2 * half, 2 * half), 1.5f * s);
            g.FillPath(mark, square);
            return;
        }
        // The triangle, nudged right so it looks centered (its weight sits left of its middle).
        float cx = circle.X + circle.Width / 2f + h * 0.2f, cy = circle.Y + circle.Height / 2f;
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(cx - h * 0.85f, cy - h), new PointF(cx - h * 0.85f, cy + h), new PointF(cx + h, cy) });
        g.FillPath(mark, path);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = e.SuppressKeyPress = true;
        }
    }
}
