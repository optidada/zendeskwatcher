using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A row of round buttons where exactly one is selected (a segmented control), in the current theme's colors.
/// Click a pill, or use the arrow keys when it has focus.
/// </summary>
public sealed class ChoicePills : Control
{
    private const int PillHeight = 30, PadX = 12, Gap = 6; // at 96 dpi

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
        Size = new Size(_widths.Sum() + Gap * (labels.Length - 1) + 2, PillHeight + 2);
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
        var scale = (Width - 2) / (float)(_widths.Sum() + Gap * (_labels.Length - 1));
        var rects = new RectangleF[_labels.Length];
        var x = 1f;
        for (var i = 0; i < _labels.Length; i++)
        {
            rects[i] = new RectangleF(x, 1, _widths[i] * scale, Height - 3);
            x += (_widths[i] + Gap) * scale;
        }
        return rects;
    }

    private int HitTest(Point p) => Array.FindIndex(PillRects(), r => r.Contains(p));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rects = PillRects();
        for (var i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            var selected = i == _selected;
            using var path = Theme.RoundedRect(r, r.Height / 2);
            if (selected)
            {
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(i == _hover ? Theme.Neutral : Theme.Border);
                g.DrawPath(pen, path);
            }
            if (selected && Focused && ShowFocusCues)
            {
                using var focus = new Pen(Theme.Text) { DashStyle = DashStyle.Dot };
                var inner = RectangleF.Inflate(r, -3, -3);
                using var innerPath = Theme.RoundedRect(inner, inner.Height / 2);
                g.DrawPath(focus, innerPath);
            }
            TextRenderer.DrawText(g, _labels[i], selected ? Theme.BodyBold : Theme.Body, Rectangle.Round(r),
                selected ? Theme.OnAccent : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}
