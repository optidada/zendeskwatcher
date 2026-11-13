using System.Drawing.Drawing2D;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The "Help colleagues" assignees: headings with All / None, then the people in two columns with a check box each.
/// Drawn as one control instead of a check box window per person, so long lists scroll smoothly (no smears).
/// People with a time zone show their local time and a dot: filled when they're working now, empty when they're off.
/// </summary>
internal sealed class AssigneeList : Control
{
    /// <param name="Zone">Their time zone (from Zendesk), for "working now"; null when unknown.</param>
    public sealed record Person(long Id, string Name, string? Tip, TimeZoneInfo? Zone = null);

    /// <param name="Title">A heading (a region), or null for no heading.</param>
    public sealed record Group(string? Title, IReadOnlyList<Person> People);

    private enum ItemKind { Heading, All, None, Person, Legend }

    private sealed record Item(ItemKind Kind, RectangleF Rect, string Text, Group Group, Person? Person = null);

    private readonly Func<long, bool> _isCovered;
    private readonly Action<Person, bool> _setCovered;
    private readonly ToolTip _tips = new();
    private List<Group> _groups = [];
    private List<Item> _items = [];
    private Item? _hover;
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 }; // the local times move on

    /// <summary>Whether someone is working at this local time of theirs (the working hours on the page).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<DateTime, bool> IsWorkingAt { get; set; } = _ => true;

    /// <summary>Someone was ticked or unticked (after <c>setCovered</c>).</summary>
    public event Action? Changed;

    /// <summary>
    /// Optional: a short label at the right end of a row ("★ Important"), in the accent color when Accent. Used by the
    /// Clients page; the Colleagues list shows local times there instead.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<long, (string Text, bool Accent)?>? Badge { get; set; }

    /// <summary>
    /// Optional: a click on the name (or a right-click) asks for a menu at that point (screen coordinates) instead of
    /// toggling; the check box still toggles. Used by the Clients page.
    /// </summary>
    public event Action<Person, Point>? MenuRequested;

    /// <summary>The people's rows in display order (a person in two groups has two), for "find".</summary>
    public List<(Person Person, Rectangle Rect)> PersonRows() =>
        _items.Where(i => i.Kind == ItemKind.Person).Select(i => (i.Person!, Rectangle.Round(i.Rect))).ToList();

    /// <summary>A row drawn highlighted (the one "find" went to); empty for none.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Rectangle Highlighted
    {
        get => _highlighted;
        set
        {
            if (value == _highlighted)
                return;
            Invalidate(_highlighted);
            _highlighted = value;
            Invalidate(_highlighted);
        }
    }

    private Rectangle _highlighted;

    /// <param name="isCovered">Whether a person is ticked.</param>
    /// <param name="setCovered">Ticks or unticks a person.</param>
    public AssigneeList(int width, Func<long, bool> isCovered, Action<Person, bool> setCovered)
    {
        _isCovered = isCovered;
        _setCovered = setCovered;
        Width = width;
        Height = 1;
        BackColor = Theme.Surface;
        Font = Theme.Body;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _clock.Tick += (_, _) => { if (Visible) Invalidate(); };
    }

    public void SetGroups(List<Group> groups)
    {
        _groups = groups;
        _highlighted = Rectangle.Empty;
        _clock.Enabled = groups.Any(g => g.People.Any(p => p.Zone is not null));
        _hover = null;
        _tips.SetToolTip(this, null);
        LayoutItems();
        Invalidate();
    }

    /// <summary>Ticks or unticks everyone in the list.</summary>
    public void SetAll(bool covered) => SetCovered(_groups.SelectMany(g => g.People), covered);

    private float DpiScale => DeviceDpi / 96f;

    private void LayoutItems()
    {
        var s = DpiScale;
        var items = new List<Item>();
        var y = 0f;
        var column = (Width - 8 * s) / 2;
        var first = true;
        foreach (var group in _groups)
        {
            if (group.Title is { } title)
            {
                y += first ? 2 * s : 8 * s;
                var height = 24 * s;
                var titleWidth = TextRenderer.MeasureText(title, Theme.Small, Size.Empty, TextFormatFlags.NoPrefix).Width + 12 * s;
                items.Add(new Item(ItemKind.Heading, new RectangleF(0, y, titleWidth, height), title, group));
                var allWidth = TextRenderer.MeasureText("All", Theme.Small).Width + 10 * s;
                items.Add(new Item(ItemKind.All, new RectangleF(titleWidth, y, allWidth, height), "All", group));
                var noneWidth = TextRenderer.MeasureText("None", Theme.Small).Width + 10 * s;
                items.Add(new Item(ItemKind.None, new RectangleF(titleWidth + allWidth, y, noneWidth, height), "None", group));
                y += height;
            }
            for (var i = 0; i < group.People.Count; i++)
            {
                var rowHeight = 26 * s;
                var x = i % 2 == 0 ? 0 : column + 8 * s;
                items.Add(new Item(ItemKind.Person, new RectangleF(x, y, column, rowHeight), group.People[i].Name, group, group.People[i]));
                if (i % 2 == 1 || i == group.People.Count - 1)
                    y += rowHeight;
            }
            first = false;
        }
        if (_groups.Any(g => g.People.Any(p => p.Zone is not null)))
        {
            y += 6 * s;
            items.Add(new Item(ItemKind.Legend, new RectangleF(0, y, Width, 22 * s), "", _groups[0]));
            y += 22 * s;
        }
        _items = items;
        Height = Math.Max(1, (int)Math.Ceiling(y + 2 * s));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutItems();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_items.Count > 0 && _items.Max(i => i.Rect.Right) > Width + 1)
            LayoutItems();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = DpiScale;

        foreach (var item in _items)
        {
            if (!item.Rect.IntersectsWith(e.ClipRectangle))
                continue;
            var rect = Rectangle.Round(item.Rect);
            switch (item.Kind)
            {
                case ItemKind.Heading:
                    TextRenderer.DrawText(g, item.Text, Theme.Small, rect, Theme.TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    break;

                case ItemKind.All or ItemKind.None:
                    var link = !Active ? Theme.TextMuted : item == _hover ? Theme.AccentHover : Theme.Accent;
                    TextRenderer.DrawText(g, item.Text, Theme.Small, rect, link, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    break;

                case ItemKind.Person:
                    if (rect == _highlighted)
                    {
                        using var mark = new SolidBrush(Theme.AccentSoft);
                        using var markPath = Theme.RoundedRect(new RectangleF(rect.X - 4 * s, rect.Y + 1, rect.Width + 4 * s, rect.Height - 2), 4 * s);
                        g.FillPath(mark, markPath);
                    }
                    var covered = _isCovered(item.Person!.Id);
                    var box = new RectangleF(item.Rect.X + 1, item.Rect.Y + (item.Rect.Height - 16 * s) / 2, 16 * s, 16 * s);
                    using (var path = Theme.RoundedRect(box, 3 * s))
                    {
                        if (covered)
                        {
                            using var fill = new SolidBrush(Active ? Theme.Accent : Theme.Border);
                            g.FillPath(fill, path);
                            using var tick = new Pen(Active ? Theme.OnAccent : Theme.Surface, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLines(tick, [
                                new PointF(box.X + 4 * s, box.Y + 8.5f * s), new PointF(box.X + 7 * s, box.Y + 11.5f * s), new PointF(box.X + 12 * s, box.Y + 5 * s)
                            ]);
                        }
                        else
                        {
                            using var border = new Pen(Active && item == _hover ? Theme.Accent : Theme.Neutral, 1.2f * s);
                            g.DrawPath(border, path);
                        }
                    }
                    var text = new Rectangle((int)(box.Right + 8 * s), rect.Y, rect.Width - (int)(24 * s), rect.Height);
                    if (Badge?.Invoke(item.Person.Id) is { } badge)
                    {
                        var badgeWidth = TextRenderer.MeasureText(badge.Text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                        var badgeRight = rect.Right - (int)(10 * s);
                        TextRenderer.DrawText(g, badge.Text, Theme.Small, new Rectangle(badgeRight - badgeWidth, rect.Y, badgeWidth, rect.Height),
                            !Active ? Theme.TextMuted : badge.Accent ? Theme.Accent : Theme.TextMuted,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                        text.Width = badgeRight - badgeWidth - (int)(8 * s) - text.X;
                    }
                    if (item.Person.Zone is { } zone)
                    {
                        // Right-aligned: their local time, with a dot that says whether they're working now.
                        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
                        var time = local.ToString("HH:mm");
                        var timeWidth = TextRenderer.MeasureText(time, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                        var right = rect.Right - (int)(10 * s);
                        TextRenderer.DrawText(g, time, Theme.Small, new Rectangle(right - timeWidth, rect.Y, timeWidth, rect.Height), Theme.TextMuted,
                            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        var dotX = right - timeWidth - 12 * s;
                        DrawDot(g, dotX, item.Rect.Y + item.Rect.Height / 2, IsWorkingAt(local), s);
                        text.Width = (int)(dotX - 8 * s) - text.X;
                    }
                    TextRenderer.DrawText(g, item.Text, Font, text, Active ? Theme.Text : Theme.TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    break;

                case ItemKind.Legend:
                    // "● working now   ○ off work: you hear about their customers' replies", with the same dots.
                    var x = item.Rect.X + 6 * s;
                    var middle = item.Rect.Y + item.Rect.Height / 2;
                    foreach (var (working, label) in new[] { (true, "working now"), (false, "off work: you hear about their customers' replies") })
                    {
                        DrawDot(g, x, middle, working, s);
                        var width = TextRenderer.MeasureText(label, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width;
                        TextRenderer.DrawText(g, label, Theme.Small, new Rectangle((int)(x + 10 * s), rect.Y, width + 2, rect.Height), Theme.TextMuted,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        x += 10 * s + width + 18 * s;
                    }
                    break;
            }
        }
    }

    /// <summary>A small dot centered at (x, y): filled in the success color when working, an empty ring when off.</summary>
    private void DrawDot(Graphics g, float x, float y, bool working, float s)
    {
        var r = 3.5f * s;
        var dot = new RectangleF(x - r, y - r, 2 * r, 2 * r);
        if (working)
        {
            using var fill = new SolidBrush(Active ? Theme.Success : Theme.Neutral);
            g.FillEllipse(fill, dot);
        }
        else
        {
            using var ring = new Pen(Theme.Neutral, 1.2f * s);
            g.DrawEllipse(ring, dot);
        }
    }

    private Item? HitTest(Point p) => _items.FirstOrDefault(i => i.Kind is not (ItemKind.Heading or ItemKind.Legend) && i.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit == _hover)
            return;
        _hover = hit;
        Cursor = hit is not null && Active ? Cursors.Hand : Cursors.Default;
        _tips.SetToolTip(this, hit?.Person?.Tip);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _tips.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (!Active || HitTest(e.Location) is not { } hit)
            return;
        // With a menu: the name (or a right-click) opens it, the check box toggles.
        if (hit.Kind == ItemKind.Person && MenuRequested is not null &&
            (e.Button == MouseButtons.Right || e.X > hit.Rect.X + 24 * DpiScale))
        {
            MenuRequested(hit.Person!, PointToScreen(e.Location));
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        switch (hit.Kind)
        {
            case ItemKind.Person:
                SetCovered([hit.Person!], !_isCovered(hit.Person!.Id));
                break;
            case ItemKind.All:
                SetCovered(hit.Group.People, true);
                break;
            case ItemKind.None:
                SetCovered(hit.Group.People, false);
                break;
        }
    }

    /// <summary>Shown greyed and ignores clicks, but still scrolls (e.g. while signed out).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value)
                return;
            _readOnly = value;
            Invalidate();
        }
    }

    private bool _readOnly;

    private bool Active => Enabled && !_readOnly;

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    private void SetCovered(IEnumerable<Person> people, bool covered)
    {
        foreach (var person in people)
            _setCovered(person, covered);
        Invalidate();
        Changed?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            _clock.Dispose();
        }
        base.Dispose(disposing);
    }
}
