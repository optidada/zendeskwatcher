using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// A thin scrollbar in the theme's colors, for a list whose own (Windows-gray) scrollbar is hidden. It doesn't scroll
/// anything itself: it reads the position through <see cref="State"/> and asks for a new one through <see cref="ScrollTo"/>.
/// </summary>
internal sealed class ThinScrollBar : Control
{
    private bool _hover, _dragging;
    private int _dragOffset;

    /// <summary>Scroll range, visible page and position, in the list's units; null when it can't scroll.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<(int Min, int Max, int Page, int Pos)?>? State { get; set; }

    /// <summary>Scroll the list to this position.</summary>
    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<int>? ScrollTo { get; set; }

    public ThinScrollBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Default;
    }

    private (int Min, int Max, int Page, int Pos)? Current()
    {
        var state = State?.Invoke();
        return state is { } s && s.Page > 0 && s.Max - s.Min + 1 > s.Page ? s : null;
    }

    private RectangleF Thumb((int Min, int Max, int Page, int Pos) s)
    {
        var range = s.Max - s.Min + 1;
        var pad = 2 * DeviceDpi / 96f;
        var track = Height - 2 * pad;
        var height = Math.Max(24 * DeviceDpi / 96f, track * s.Page / range);
        var top = pad + (track - height) * (s.Pos - s.Min) / Math.Max(1, range - s.Page);
        return new RectangleF(pad, top, Width - 2 * pad, height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        if (Current() is not { } s)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var thumb = Thumb(s);
        using var path = Theme.RoundedRect(thumb, thumb.Width / 2);
        using var brush = new SolidBrush(_hover || _dragging ? Theme.TextMuted : Theme.Neutral);
        g.FillPath(brush, path);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Current() is not { } s)
            return;
        var thumb = Thumb(s);
        if (e.Y >= thumb.Top && e.Y <= thumb.Bottom)
        {
            _dragging = true;
            _dragOffset = (int)(e.Y - thumb.Top);
            Capture = true;
        }
        else
        {
            // A click beside the thumb moves a page towards it.
            ScrollTo?.Invoke(s.Pos + (e.Y < thumb.Top ? -s.Page : s.Page));
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || Current() is not { } s)
            return;
        var thumb = Thumb(s);
        var pad = 2 * DeviceDpi / 96f;
        var free = Height - 2 * pad - thumb.Height;
        var fraction = free <= 0 ? 0 : Math.Clamp((e.Y - _dragOffset - pad) / free, 0, 1);
        ScrollTo?.Invoke(s.Min + (int)Math.Round(fraction * (s.Max - s.Min + 1 - s.Page)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }
}
