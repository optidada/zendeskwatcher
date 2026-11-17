using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// "Priority  [All | P1–P3 | P1, P2 | P1 only]": which priorities notify, as one row of buttons. Used for new tickets
/// (per group), ticket changes and Help colleagues.
/// </summary>
internal sealed class PriorityChoice
{
    private readonly ChoicePills _pills = new([.. Settings.PriorityChoices.Select(c => c.Label)]) { Margin = new Padding(0, 2, 0, 2) };

    public FlowLayoutPanel Row { get; }

    /// <summary>Another choice was picked.</summary>
    public event Action? Changed;

    /// <param name="label">Show "Priority" in front (off where the row already says what it's for).</param>
    public PriorityChoice(Padding margin, bool label = true)
    {
        Row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, BackColor = Theme.Surface, Margin = margin };
        if (label)
        {
            var text = Theme.Label("Priority");
            text.Margin = new Padding(4, 10, 12, 0);
            Row.Controls.Add(text);
        }
        _pills.AccessibleName = "Priority";
        _pills.SelectedIndexChanged += (_, _) => Changed?.Invoke();
        Row.Controls.Add(_pills);
    }

    /// <summary>The priorities that notify ("urgent", "high", "normal", "low"; all four for All).</summary>
    public List<string> Selected
    {
        get => [.. Settings.PriorityChoices[_pills.SelectedIndex].Priorities ?? Settings.AllPriorities];
        set => _pills.SelectedIndex = Settings.PriorityChoiceIndex(value);
    }

    public bool Enabled
    {
        set => _pills.Enabled = value;
    }
}
