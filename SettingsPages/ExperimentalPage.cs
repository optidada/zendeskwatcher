using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// Experimental (last in the sidebar): the smart features, each off by default, in one compact card. A small English
/// language model that comes with the app (Tracking/Embeddings.cs) and rules; worked out on this PC, ticket text never leaves it.
/// Details per feature: How it works → Experimental features.
/// </summary>
internal sealed class ExperimentalPage : SettingsPage
{
    private readonly ToggleSwitch _guessPriority = new("Priority guess");
    private readonly ToggleSwitch _sameIssue = new("Same issue?");
    private readonly ToggleSwitch _similar = new("Similar solved ticket");
    private readonly ToggleSwitch _fitsYou = new("Fits you");
    private readonly ToggleSwitch _quietThanks = new("Quiet thank-yous");
    private readonly ToggleSwitch _topics = new("Topics");
    private readonly Label _topicsNow = Theme.Label("", Theme.Small, Theme.Text, Kit.CardWidth - 44 - 50);

    public ExperimentalPage(SettingsContext context)
        : base(context, "Experimental", "Smart extras, still being tried out. English only.")
    {
        var card = new Card("On this PC only", "A small language model that comes with the app (about 8 MB, loaded only while one of these is on) " +
            "and rules. Ticket text never leaves this PC; Zendesk is never changed. More under How it works.", Kit.CardWidth);
        Add(card, _guessPriority, "“Looks like P1: site down · frustrated tone” when it sounds more urgent than its priority.");
        Add(card, _sameIssue, "One notification when 3+ new tickets within an hour read alike: maybe an incident.");
        Add(card, _similar, "“Like #123456 … · solved by Jane” on new tickets (90 days of your groups).");
        Add(card, _fitsYou, "“Fits you: like 4 tickets you solved” on new tickets.");
        Add(card, _quietThanks, "A customer's plain “Thanks, that worked!” notifies without sound; Catch up skips it.");
        Add(card, _topics, "Words that come up twice as often as usual this week, shown here:");
        _topicsNow.Margin = new Padding(50, 0, 0, 4);
        card.Body.Controls.Add(_topicsNow);
        Panel.Controls.Add(card);

        TrackChanges(_guessPriority, _sameIssue, _similar, _fitsYou, _quietThanks, _topics);
        _topics.CheckedChanged += (_, _) => ShowTopics();
    }

    private static void Add(Card card, ToggleSwitch toggle, string explanation)
    {
        card.Body.Controls.Add(toggle);
        var note = Theme.Label(explanation, Theme.Small, Theme.TextMuted, Kit.CardWidth - 44 - 50);
        note.Margin = new Padding(50, 0, 0, 6);
        card.Body.Controls.Add(note);
    }

    public override void OnShowing() => ShowTopics();

    /// <summary>What the last look at the tickets found (read every 6 hours while Topics is on).</summary>
    private void ShowTopics()
    {
        _topicsNow.Visible = _topics.Checked;
        _topicsNow.Text = TopicsStatus.UpdatedAt is not { } at ? "Save, and the topics appear here after the next check."
            : TopicsStatus.Current.Count == 0 ? $"Nothing stands out this week ({TopicsStatus.TicketsLookedAt} tickets looked at, {at:HH:mm})."
            : $"{TopicsStatus.Text(TopicsStatus.Current)}  ({at:HH:mm})";
    }

    public override void Load(Settings settings)
    {
        _guessPriority.Checked = settings.GuessPriority;
        _sameIssue.Checked = settings.SameIssueAlert;
        _similar.Checked = settings.ShowSimilarTickets;
        _fitsYou.Checked = settings.MarkFitsYou;
        _quietThanks.Checked = settings.QuietThankYous;
        _topics.Checked = settings.ShowTopics;
        ShowTopics();
    }

    public override void Apply(Settings settings)
    {
        settings.GuessPriority = _guessPriority.Checked;
        settings.SameIssueAlert = _sameIssue.Checked;
        settings.ShowSimilarTickets = _similar.Checked;
        settings.MarkFitsYou = _fitsYou.Checked;
        settings.QuietThankYous = _quietThanks.Checked;
        settings.ShowTopics = _topics.Checked;
    }
}
