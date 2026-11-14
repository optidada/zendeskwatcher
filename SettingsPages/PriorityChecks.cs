using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>A "Priority  ☑ Urgent [P1]  ☑ High [P2]  ☐ Normal [P3]  ☐ Low [P4]" row (the P-levels as colored tags). Used by ticket changes and Help colleagues.</summary>
internal sealed class PriorityChecks
{
    private readonly (string Priority, CheckBox Box)[] _boxes =
    [
        ("urgent", Tagged("Urgent", "urgent")), ("high", Tagged("High", "high")), ("normal", Tagged("Normal", "normal")), ("low", Tagged("Low", "low"))
    ];

    public FlowLayoutPanel Row { get; }

    private static CheckBox Tagged(string text, string priority)
    {
        var box = Kit.Check(text);
        if (box is ThemedCheckBox themed)
            themed.Priority = priority;
        return box;
    }

    /// <summary>A box was ticked or unticked.</summary>
    public event Action? Changed;

    /// <param name="label">Show "Priority" in front (off under a heading that already says it).</param>
    public PriorityChecks(Padding margin, bool label = true)
    {
        Row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = margin };
        if (label)
        {
            var text = Theme.Label("Priority");
            text.Margin = new Padding(4, 6, 12, 0);
            Row.Controls.Add(text);
        }
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
