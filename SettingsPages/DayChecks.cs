using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>A row "Days  ☑ Mon ☑ Tue … ☐ Sun": which days are working days.</summary>
internal sealed class DayChecks
{
    private static readonly DayOfWeek[] Order =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly (DayOfWeek Day, CheckBox Box)[] _boxes = Order.Select(d => (d, Kit.Check(d.ToString()[..3]))).ToArray();
    private readonly Label _label;

    public FlowLayoutPanel Row { get; }

    /// <summary>A box was ticked or unticked.</summary>
    public event Action? Changed;

    public DayChecks(string label, Padding margin)
    {
        Row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = margin };
        _label = Theme.Label(label);
        _label.Margin = new Padding(4, 6, 12, 0);
        if (label.Length > 0) // empty: the caller puts its own label next to the row
            Row.Controls.Add(_label);
        foreach (var (_, box) in _boxes)
        {
            box.Margin = new Padding(0, 3, 6, 3);
            box.CheckedChanged += (_, _) => Changed?.Invoke();
            Row.Controls.Add(box);
        }
    }

    /// <summary>The ticked days, Monday first.</summary>
    public List<DayOfWeek> Selected
    {
        get => _boxes.Where(d => d.Box.Checked).Select(d => d.Day).ToList();
        set
        {
            foreach (var (day, box) in _boxes)
                box.Checked = value.Contains(day);
        }
    }

    public bool Enabled
    {
        set
        {
            _label.Enabled = value;
            foreach (var (_, box) in _boxes)
                box.Enabled = value;
        }
    }
}
