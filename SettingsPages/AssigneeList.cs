using System.Drawing.Drawing2D;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// The "Help colleagues" assignees: headings with All / None, then the people in two columns with a check box each.
/// Drawn as one control instead of a check box window per person, so long lists scroll smoothly (no smears).
/// </summary>
internal sealed class AssigneeList : Control
{
    public sealed record Person(long Id, string Name, string? Tip);

    /// <param name="Title">A heading (a region), or null for no heading.</param>
    public sealed record Group(string? Title, IReadOnlyList<Person> People);

    private enum ItemKind { Heading, All, None, Person }

    private sealed record Item(ItemKind Kind, RectangleF Rect, string Text, Group Group, Person? Person = null);

    private readonly Func<long, bool> _isCovered;
    private readonly Action<Person, bool> _setCovered;
    private readonly ToolTip _tips = new();
    private List<Group> _groups = [];
    private List<Item> _items = [];
    private Item? _hover;

    /// <summary>Someone was ticked or unticked (after <c>setCovered</c>).</summary>
    public event Action? Changed;

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
    }

    public void SetGroups(List<Group> groups)
    {
        _groups = groups;
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
                    var link = !Enabled ? Theme.TextMuted : item == _hover ? Theme.AccentHover : Theme.Accent;
                    TextRenderer.DrawText(g, item.Text, Theme.Small, rect, link, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                    break;

                case ItemKind.Person:
                    var covered = _isCovered(item.Person!.Id);
                    var box = new RectangleF(item.Rect.X + 1, item.Rect.Y + (item.Rect.Height - 16 * s) / 2, 16 * s, 16 * s);
                    using (var path = Theme.RoundedRect(box, 3 * s))
                    {
                        if (covered)
                        {
                            using var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Border);
                            g.FillPath(fill, path);
                            using var tick = new Pen(Enabled ? Theme.OnAccent : Theme.Surface, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                            g.DrawLines(tick, [
                                new PointF(box.X + 4 * s, box.Y + 8.5f * s), new PointF(box.X + 7 * s, box.Y + 11.5f * s), new PointF(box.X + 12 * s, box.Y + 5 * s)
                            ]);
                        }
                        else
                        {
                            using var border = new Pen(Enabled && item == _hover ? Theme.Accent : Theme.Neutral, 1.2f * s);
                            g.DrawPath(border, path);
                        }
                    }
                    var text = new Rectangle((int)(box.Right + 8 * s), rect.Y, rect.Width - (int)(24 * s), rect.Height);
                    TextRenderer.DrawText(g, item.Text, Font, text, Enabled ? Theme.Text : Theme.TextMuted,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    break;
            }
        }
    }

    private Item? HitTest(Point p) => _items.FirstOrDefault(i => i.Kind != ItemKind.Heading && i.Rect.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit == _hover)
            return;
        _hover = hit;
        Cursor = hit is not null && Enabled ? Cursors.Hand : Cursors.Default;
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
        if (!Enabled || e.Button != MouseButtons.Left || HitTest(e.Location) is not { } hit)
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
            _tips.Dispose();
        base.Dispose(disposing);
    }
}
