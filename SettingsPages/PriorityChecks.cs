using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>A "Priority  ☑ Urgent (P1)  ☑ High (P2)  ☐ Normal (P3)  ☐ Low (P4)" row. Used by ticket changes and Help colleagues.</summary>
internal sealed class PriorityChecks
{
    private readonly (string Priority, CheckBox Box)[] _boxes =
    [
        ("urgent", Kit.Check("Urgent (P1)")), ("high", Kit.Check("High (P2)")), ("normal", Kit.Check("Normal (P3)")), ("low", Kit.Check("Low (P4)"))
    ];

    public FlowLayoutPanel Row { get; }

    /// <summary>A box was ticked or unticked.</summary>
    public event Action? Changed;

    public PriorityChecks(Padding margin)
    {
        Row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = margin };
        var label = Theme.Label("Priority");
        label.Margin = new Padding(4, 6, 12, 0);
        Row.Controls.Add(label);
        foreach (var (_, box) in _boxes)
        {
            box.Margin = new Padding(0, 3, 14, 3);
            box.CheckedChanged += (_, _) => Changed?.Invoke();
            Row.Controls.Add(box);
        }
    }

    /// <summary>The ticked priorities ("urgent", "high", "normal", "low"), in that order.</summary>
    public List<string> Selected
    {
        get => _boxes.Where(p => p.Box.Checked).Select(p => p.Priority).ToList();
        set
        {
            foreach (var (priority, box) in _boxes)
                box.Checked = value.Contains(priority);
        }
    }

    public bool Enabled
    {
        set
        {
            foreach (var (_, box) in _boxes)
                box.Enabled = value;
        }
    }
}
