namespace ZendeskWatcher.UI;

/// <summary>
/// A table drawn as one control (Insights). A table of labels is one window per cell: about 50 each, and scrolling the
/// page then redrew every one of them (slow, and smeared on the way). First column left, the others right-aligned.
/// </summary>
public sealed class GridTable : Control
{
    public enum Ink { Text, Muted, Link }

    /// <summary>
    /// A cell. <paramref name="Tags"/>: priority chips ("urgent", "high") drawn before the text. A cell with
    /// <paramref name="Click"/> is a link (hand cursor, underlined under the mouse). <paramref name="Bar"/>: a bar filled
    /// this much (0-1) instead of text (muted with <see cref="Ink.Muted"/>). <paramref name="Left"/>: left-aligned like the first column.
    /// </summary>
    public sealed record Cell(string Text, Font Font, Ink Ink = Ink.Text, string? Tip = null, Action? Click = null, string[]? Tags = null,
        float? Bar = null, bool Left = false);

    /// <summary>How long a <see cref="Cell.Bar"/> is (logical pixels).</summary>
    private const int BarWidth = 110;

    private IReadOnlyList<Cell[]> _rows = [];
    private int[] _widths = [];
    private int[] _heights = [];
    private (int Row, int Column)? _hover;
    private readonly ThemedToolTip _tip = new(); // in the app's colors

    /// <summary>The widest the table may get: <see cref="ShrinkColumn"/> gives way.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MaxWidth { get; set; } = int.MaxValue;

    /// <summary>The column that gives way when the table is wider than <see cref="MaxWidth"/> (its texts end in "…").</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ShrinkColumn { get; set; }

    public GridTable()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
    }

    private float Scale_ => DeviceDpi / 96f;
    private int Px(int logical) => (int)Math.Round(logical * Scale_);

    /// <summary>The rows, the first being the headings; sizes the control to fit.</summary>
    public void SetRows(IReadOnlyList<Cell[]> rows)
    {
        _rows = rows;
        _hover = null;
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        _widths = new int[columns];
        _heights = new int[rows.Count];
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Length; c++)
            {
                var size = Measure(rows[r][c]);
                _widths[c] = Math.Max(_widths[c], size.Width + Gap(c));
                _heights[r] = Math.Max(_heights[r], size.Height + Px(8));
            }
        var over = _widths.Sum() - MaxWidth;
        if (over > 0 && columns > ShrinkColumn)
            _widths[ShrinkColumn] = Math.Max(Px(48), _widths[ShrinkColumn] - over);
        Size = new Size(_widths.Sum(), _heights.Sum());
        Invalidate();
    }

    /// <summary>Space before a right-aligned column, after the first one.</summary>
    private int Gap(int column) => column == 0 ? Px(10) + Lead : Px(8);

    /// <summary>Before the first column: a label's own inner space, so the table lines up with the labels above it.</summary>
    private int Lead => Px(3);

    private Size Measure(Cell cell)
    {
        if (cell.Bar is not null)
            return new Size(Px(BarWidth), TextRenderer.MeasureText("X", cell.Font, Size.Empty, Flags).Height);
        var text = cell.Text.Length == 0 ? new Size(0, TextRenderer.MeasureText("X", cell.Font, Size.Empty, Flags).Height)
            : TextRenderer.MeasureText(cell.Text, cell.Font, Size.Empty, Flags);
        if (cell.Tags is not { Length: > 0 } tags)
            return text;
        var chips = tags.Select(t => PriorityTag.Measure(PriorityTag.Short(t) ?? t, Scale_)).ToList();
        return new Size(text.Width + chips.Sum(s => s.Width + Px(4)), Math.Max(text.Height, chips.Max(s => s.Height)));
    }

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

    private Rectangle CellBounds(int row, int column)
    {
        var x = _widths.Take(column).Sum();
        var y = _heights.Take(row).Sum();
        var gap = Gap(column);
        return column == 0 ? new Rectangle(x + Lead, y, _widths[0] - gap, _heights[row]) : new Rectangle(x + gap, y, _widths[column] - gap, _heights[row]);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        for (var r = 0; r < _rows.Count; r++)
            for (var c = 0; c < _rows[r].Length; c++)
            {
                var cell = _rows[r][c];
                var bounds = CellBounds(r, c);
                if (!bounds.IntersectsWith(e.ClipRectangle))
                    continue;
                var color = cell.Ink switch
                {
                    Ink.Muted => Theme.TextMuted,
                    Ink.Link => _hover == (r, c) ? Theme.Current.LinkHover : Theme.Current.Link,
                    _ => Theme.Text
                };
                var font = cell.Click is not null && _hover == (r, c) ? new Font(cell.Font, cell.Font.Style | FontStyle.Underline) : cell.Font;
                var size = Measure(cell);
                var left = c == 0 || cell.Left;
                if (cell.Bar is { } fill)
                {
                    DrawBar(g, new Rectangle(bounds.X, bounds.Y + (bounds.Height - Px(8)) / 2, size.Width, Px(8)), fill, cell.Ink == Ink.Muted);
                    continue;
                }
                var x = left ? bounds.X : bounds.Right - size.Width;
                foreach (var tag in cell.Tags ?? [])
                    x += PriorityTag.Draw(g, PriorityTag.Short(tag) ?? tag, PriorityTag.ColorOf(tag), x, bounds.Y + bounds.Height / 2, Scale_) + Px(4);
                TextRenderer.DrawText(g, cell.Text, font, new Rectangle(x, bounds.Y, bounds.Right - x, bounds.Height), color,
                    Flags | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (left ? TextFormatFlags.Left : TextFormatFlags.Right));
                if (!ReferenceEquals(font, cell.Font))
                    font.Dispose();
            }
    }

    /// <summary>A rounded track with the filled part in the accent color (or muted).</summary>
    private void DrawBar(Graphics g, Rectangle track, float fill, bool muted)
    {
        using (var back = new SolidBrush(Theme.Border))
            FillRounded(g, back, track);
        var width = (int)Math.Round(track.Width * Math.Clamp(fill, 0, 1));
        if (width <= 0)
            return;
        using var front = new SolidBrush(muted ? Theme.TextMuted : Theme.Accent);
        FillRounded(g, front, track with { Width = Math.Max(width, track.Height) });
    }

    private static void FillRounded(Graphics g, Brush brush, Rectangle r)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    private (int Row, int Column)? CellAt(Point p)
    {
        for (int r = 0, y = 0; r < _rows.Count; y += _heights[r], r++)
        {
            if (p.Y < y || p.Y >= y + _heights[r])
                continue;
            for (var c = 0; c < _rows[r].Length; c++)
            {
                var bounds = CellBounds(r, c);
                if (p.X < bounds.X || p.X >= bounds.Right)
                    continue;
                // Only the text itself, as with a label (right-aligned cells are mostly empty on the left).
                var width = Math.Min(Measure(_rows[r][c]).Width, bounds.Width);
                return (c == 0 || _rows[r][c].Left ? p.X < bounds.X + width : p.X >= bounds.Right - width) ? (r, c) : null;
            }
        }
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var at = CellAt(e.Location);
        if (at == _hover)
            return;
        var was = _hover;
        _hover = at;
        var cell = at is var (r, c) ? _rows[r][c] : null;
        Cursor = cell?.Click is not null ? Cursors.Hand : Cursors.Default;
        // The full text as the tip where it ends in "…".
        var tip = cell?.Tip ?? (cell is not null && at is var (r2, c2) && Measure(cell).Width > CellBounds(r2, c2).Width ? cell.Text : null);
        _tip.SetToolTip(this, tip);
        if (was is var (wr, wc))
            Invalidate(CellBounds(wr, wc));
        if (at is var (ar, ac))
            Invalidate(CellBounds(ar, ac));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is var (r, c))
            Invalidate(CellBounds(r, c));
        _hover = null;
        Cursor = Cursors.Default;
        _tip.SetToolTip(this, null);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && CellAt(e.Location) is var (r, c))
            _rows[r][c].Click?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _tip.Dispose();
        base.Dispose(disposing);
    }
}
