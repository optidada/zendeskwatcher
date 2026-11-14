using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ZendeskWatcher.UI;

/// <summary>
/// Working hours as one bar for the day (0–24 h): the hours in between filled in the accent, a handle at each end to
/// drag, whole hours only. Keys: Left/Right move the chosen handle, Space switches handles, Home/End go to the edges.
/// </summary>
public sealed class HourRange : Control
{
    private int _start = 8, _end = 17;
    private int _active;        // the handle the keyboard moves: 0 start, 1 end
    private int _dragging = -1; // the handle being dragged, -1 none
    private int _hover = -1;

    public event EventHandler? RangeChanged;

    public HourRange()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        Size = new Size(320, 62);
        AccessibleRole = AccessibleRole.Slider;
        UpdateAccessibleName();
    }

    /// <summary>The first working hour, 0–23.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Start
    {
        get => _start;
        set => SetRange(value, Math.Max(_end, value + 1));
    }

    /// <summary>The hour work ends, 1–24 (17 = until 17:00).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int End
    {
        get => _end;
        set => SetRange(Math.Min(_start, value - 1), value);
    }

    /// <summary>Sets both ends (at least one hour apart, within the day); raises <see cref="RangeChanged"/> when it changes.</summary>
    public void SetRange(int start, int end)
    {
        start = Math.Clamp(start, 0, 23);
        end = Math.Clamp(end, start + 1, 24);
        if (start == _start && end == _end)
            return;
        (_start, _end) = (start, end);
        UpdateAccessibleName();
        Invalidate();
        RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateAccessibleName() => AccessibleName = $"{Clock.Hour(_start)} to {Clock.Hour(_end)}";

    // ------------------------------------------------------------------ geometry

    private float S => DeviceDpi / 96f;
    /// <summary>Where a label's text starts inside it (at 96 dpi): the hours and the bar start there too.</summary>
    private const float TextInset = 3;

    private float HandleRadius => 8 * S;
    private float TrackLeft => Math.Max(HandleRadius + 2 * S, TextInset * S);
    private float TrackWidth => Width - 2 * TrackLeft;
    private float TrackY => 30 * S;

    private float X(int hour) => TrackLeft + TrackWidth * hour / 24f;

    /// <summary>The whole hour nearest to a point on the bar.</summary>
    internal static int HourAt(float x, float left, float width) => (int)Math.Round(Math.Clamp((x - left) / width, 0, 1) * 24);

    /// <summary>The handle under the point, or -1.</summary>
    private int HandleAt(Point p)
    {
        float Distance(int hour) => MathF.Sqrt(MathF.Pow(p.X - X(hour), 2) + MathF.Pow(p.Y - TrackY, 2));
        var start = Distance(_start);
        var end = Distance(_end);
        var reach = HandleRadius + 4 * S;
        if (start > reach && end > reach)
            return -1;
        return end < start || (end == start && p.X > X(_end)) ? 1 : 0;
    }

    // ------------------------------------------------------------------ painting

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = S;
        var on = Enabled;

        // The hours, above the bar.
        var text = $"{Clock.Hour(_start)} – {Clock.Hour(_end)}";
        var hours = _end - _start;
        // Inset like a label's text, so it lines up with the heading above.
        var inset = (int)(TextInset * s);
        TextRenderer.DrawText(g, text, Theme.BodyBold, new Point(inset, 0), on ? Theme.Text : Theme.TextMuted, TextFormatFlags.NoPadding);
        var textWidth = TextRenderer.MeasureText(g, text, Theme.BodyBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(g, $"  {hours} h", Theme.Small, new Point(inset + textWidth, (int)(2 * s)), Theme.TextMuted, TextFormatFlags.NoPadding);

        // The day, and the working part in the accent.
        var trackHeight = 6 * s;
        var track = new RectangleF(TrackLeft, TrackY - trackHeight / 2, TrackWidth, trackHeight);
        using (var path = Theme.RoundedRect(track, trackHeight / 2))
        using (var day = new SolidBrush(Theme.Border))
            g.FillPath(day, path);
        var work = new RectangleF(X(_start), track.Y, X(_end) - X(_start), trackHeight);
        using (var path = Theme.RoundedRect(work, trackHeight / 2))
        using (var fill = new SolidBrush(on ? Theme.Accent : Theme.Neutral))
            g.FillPath(fill, path);

        // Every 6 hours, under the bar.
        foreach (var hour in new[] { 0, 6, 12, 18, 24 })
        {
            var label = Clock.HourMark(hour);
            var w = TextRenderer.MeasureText(g, label, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
            var x = Math.Clamp(X(hour) - w / 2f, 0, Width - w);
            TextRenderer.DrawText(g, label, Theme.Small, new Point((int)x, (int)(TrackY + 12 * s)), Theme.TextMuted, TextFormatFlags.NoPadding);
        }

        // The handles: the keyboard's one gets a ring while focused.
        for (var i = 0; i < 2; i++)
        {
            var cx = X(i == 0 ? _start : _end);
            var r = HandleRadius * (_dragging == i || _hover == i ? 1.12f : 1f);
            var handle = new RectangleF(cx - r, TrackY - r, 2 * r, 2 * r);
            using (var face = new SolidBrush(Theme.Surface))
                g.FillEllipse(face, handle);
            using (var edge = new Pen(on ? Theme.Accent : Theme.Neutral, 2.2f * s))
                g.DrawEllipse(edge, handle);
            if (Focused && i == _active)
            {
                using var ring = new Pen(Color.FromArgb(110, Theme.Accent), 1.5f * s);
                g.DrawEllipse(ring, RectangleF.Inflate(handle, 3.5f * s, 3.5f * s));
            }
        }
    }

    // ------------------------------------------------------------------ mouse and keys

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Enabled || e.Button != MouseButtons.Left)
            return;
        Focus();
        var hit = HandleAt(e.Location);
        if (hit < 0)
        {
            // A click on the bar moves the nearer end there.
            var hour = HourAt(e.X, TrackLeft, TrackWidth);
            hit = Math.Abs(hour - _start) <= Math.Abs(hour - _end) ? 0 : 1;
            MoveHandle(hit, hour);
        }
        _dragging = _active = hit;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging >= 0)
        {
            MoveHandle(_dragging, HourAt(e.X, TrackLeft, TrackWidth));
            return;
        }
        var hover = Enabled ? HandleAt(e.Location) : -1;
        Cursor = hover >= 0 ? Cursors.SizeWE : Cursors.Hand;
        if (hover != _hover)
        {
            _hover = hover;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = -1;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    /// <summary>Moves one end, never past the other (at least an hour apart).</summary>
    private void MoveHandle(int handle, int hour)
    {
        if (handle == 0)
            SetRange(Math.Min(hour, _end - 1), _end);
        else
            SetRange(_start, Math.Max(hour, _start + 1));
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled)
            return;
        var current = _active == 0 ? _start : _end;
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.Down: MoveHandle(_active, current - 1); break;
            case Keys.Right or Keys.Up: MoveHandle(_active, current + 1); break;
            case Keys.Home: MoveHandle(_active, 0); break;
            case Keys.End: MoveHandle(_active, 24); break;
            case Keys.Space or Keys.Enter: _active = 1 - _active; Invalidate(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
