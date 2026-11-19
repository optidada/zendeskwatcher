using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A segmented control: the choices joined in one frame (the theme's button shape), the chosen one filled, so it reads
/// as "pick one view" rather than separate buttons. Click a choice, or use the arrow keys when it has focus.
/// </summary>
public sealed class ChoicePills : Control
{
    private const int PillHeight = 32, PadX = 16, Inset = 3; // at 96 dpi

    private readonly string[] _labels;
    private readonly int[] _widths; // at 96 dpi
    private int _selected;
    private int _hover = -1;

    public event EventHandler? SelectedIndexChanged;

    public ChoicePills(params string[] labels)
    {
        _labels = labels;
        Font = Theme.Body;
        // Measured at 96 dpi: the window scales the control with the display afterwards (measuring at the display's
        // scaling would scale it twice).
        using var bitmap = new Bitmap(1, 1);
        bitmap.SetResolution(96, 96);
        using var g = Graphics.FromImage(bitmap);
        _widths = labels.Select(l => TextRenderer.MeasureText(g, l, Theme.BodyBold).Width + PadX * 2).ToArray();
        Size = new Size(_widths.Sum() + 2 * Inset + 2, PillHeight + 2);
        Margin = new Padding(0, 3, 0, 3);
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.Selectable, true);
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var index = Math.Clamp(value, 0, _labels.Length - 1);
            if (index == _selected)
                return;
            _selected = index;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The pills' rectangles at the current size (the window scales the control with the display).</summary>
    private RectangleF[] PillRects()
    {
        var s = DeviceDpi / 96f;
        var frame = Frame();
        var inset = Inset * s;
        var scale = (frame.Width - 2 * inset) / _widths.Sum();
        var rects = new RectangleF[_labels.Length];
        var x = frame.X + inset;
        for (var i = 0; i < _labels.Length; i++)
        {
            rects[i] = new RectangleF(x, frame.Y + inset, _widths[i] * scale, frame.Height - 2 * inset);
            x += _widths[i] * scale;
        }
        return rects;
    }

    /// <summary>The shared frame around all choices.</summary>
    private RectangleF Frame() => new(1, 1, Width - 3, Height - 3);

    /// <summary>A pill in the theme's button shape (square in Terminal).</summary>
    private GraphicsPath Shape(RectangleF r)
    {
        var radius = Theme.ButtonRadius(r.Height, DeviceDpi / 96f);
        if (radius > 0)
            return Theme.RoundedRect(r, radius);
        var path = new GraphicsPath();
        path.AddRectangle(r);
        return path;
    }

    private int HitTest(Point p) => Array.FindIndex(PillRects(), r => r.Contains(p));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var s = DeviceDpi / 96f;
        using (var frame = Shape(Frame()))
        using (var track = new SolidBrush(Theme.Window))
        using (var border = new Pen(Theme.Border))
        {
            g.FillPath(track, frame);
            g.DrawPath(border, frame);
        }

        var rects = PillRects();
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            var selected = i == _selected;
            using var path = Shape(r);
            if (selected)
            {
                using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Border); // greyed while it can't be changed
                g.FillPath(fill, path);
            }
            else if (i == _hover)
            {
                using var hover = new SolidBrush(Theme.Border);
                g.FillPath(hover, path);
            }
            // A thin divider between two choices that aren't chosen.
            if (i > 0 && !selected && i - 1 != _selected)
            {
                using var divider = new Pen(Theme.Border, 1);
                g.DrawLine(divider, r.X, r.Y + 6 * s, r.X, r.Bottom - 6 * s);
            }
            if (selected && Focused && ShowFocusCues)
            {
                using var focus = new Pen(Theme.Text) { DashStyle = DashStyle.Dot };
                var inner = RectangleF.Inflate(r, -3, -3);
                using var innerPath = Shape(inner);
                g.DrawPath(focus, innerPath);
            }
            TextRenderer.DrawText(g, _labels[i], selected ? Theme.BodyBold : Theme.Body, Rectangle.Round(r),
                !Enabled ? Theme.TextMuted : selected ? Theme.OnAccent : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = HitTest(e.Location);
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        Focus();
        if (HitTest(e.Location) is var index and >= 0)
            SelectedIndex = index;
        base.OnMouseClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) SelectedIndex--;
        else if (e.KeyCode == Keys.Right) SelectedIndex++;
        base.OnKeyDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}
