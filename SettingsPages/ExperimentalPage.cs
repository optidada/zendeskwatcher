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
    private readonly ToggleSwitch _quietThanks = new("Quiet thank-yous");

    public ExperimentalPage(SettingsContext context)
        : base(context, "Experimental", "Smart extras, still being tried out. English only.")
    {
        var card = new Card("On this PC only", "Uses a small built-in language model. Ticket text never leaves this PC. Details under How it works.", Kit.CardWidth);
        Add(card, _guessPriority, "“Set as P3 · looks like P1: site down” when it sounds more urgent than its priority; “probably not” when a P1 or P2 shows no sign of urgency.");
        Add(card, _quietThanks, "A customer's plain “Thanks, that worked!” notifies without sound; Catch up skips it.");
        Panel.Controls.Add(card);

        TrackChanges(_guessPriority, _quietThanks);
    }

    private static void Add(Card card, ToggleSwitch toggle, string explanation)
    {
        card.Body.Controls.Add(toggle);
        var note = Theme.Label(explanation, Theme.Small, Theme.TextMuted, Kit.CardWidth - 44 - 50);
        note.Margin = new Padding(50, 0, 0, 6);
        card.Body.Controls.Add(note);
    }

    public override void Load(Settings settings)
    {
        _guessPriority.Checked = settings.GuessPriority;
        _quietThanks.Checked = settings.QuietThankYous;
    }

    public override void Apply(Settings settings)
    {
        settings.GuessPriority = _guessPriority.Checked;
        settings.QuietThankYous = _quietThanks.Checked;
    }
}
