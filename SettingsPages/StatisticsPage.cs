using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>Statistics: ticket notifications of the last 30 days, and what was done with them.</summary>
internal sealed class StatisticsPage : SettingsPage
{
    private static readonly (NotificationKind Kind, string Label)[] Kinds =
    [
        (NotificationKind.NewTicket, "New tickets"), (NotificationKind.MyTicket, "My tickets"),
        (NotificationKind.CcTicket, "CC / following"), (NotificationKind.HelpColleague, "Colleagues' tickets")
    ];

    private static readonly (StatOutcome Outcome, string Label)[] Columns =
    [
        (StatOutcome.Shown, "Shown"), (StatOutcome.OpenedTicket, "Opened"),
        (StatOutcome.Took, "Took it"), (StatOutcome.Snoozed, "Snoozed"), (StatOutcome.Closed, "Closed"), (StatOutcome.TimedOut, "Timed out"),
        (StatOutcome.Copied, "Copied")
    ];

    private readonly Label _summary = Theme.Label("", Theme.Body, Theme.Text, Kit.CardWidth - 44);
    private readonly TableLayoutPanel _table = new() { AutoSize = true, BackColor = Theme.Surface, Margin = new Padding(0, 8, 0, 8) };
    private readonly Button _clear = Theme.SecondaryButton("Clear statistics");

    public StatisticsPage(SettingsContext context)
        : base(context, "Statistics", "Ticket notifications in the last 30 days, and what you did with them. Counted on this PC only; nothing is sent anywhere.")
    {
        var card = new Card("Last 30 days", null, Kit.CardWidth);
        card.Body.Controls.Add(_summary);
        card.Body.Controls.Add(_table);
        card.Body.Controls.Add(Theme.Label(
            "Opened: the ticket, or Zendesk for a notification about several tickets. Timed out: disappeared on its own, " +
            "or went to the Windows notification center. Copied: copied for Teams (it can be opened as well). A snoozed notification " +
            "counts again for what you do when it comes back. " +
            "Windows notifications are only counted while Zendesk Watcher runs.", Theme.Small, Theme.TextMuted, Kit.CardWidth - 44));
        _clear.Margin = new Padding(0, 10, 0, 0);
        card.Body.Controls.Add(_clear);
        Panel.Controls.Add(card);

        _clear.Click += (_, _) =>
        {
            if (MessageBox.Show(Context.Window, "Clear the notification statistics?", Context.Window.Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            Context.Notifier.Stats.Clear();
            Refresh();
        };
    }

    public override void Load(Settings settings) => Refresh();

    public override void Apply(Settings settings) { } // nothing to save: the statistics are kept as they happen

    public override void OnShowing() => Refresh();

    private void Refresh()
    {
        var totals = Context.Notifier.Stats.Totals(DateOnly.FromDateTime(DateTime.Now));
        // "Opened" includes opening Zendesk from a notification about several tickets: the same reaction, no single ticket.
        int Count(NotificationKind kind, StatOutcome outcome) => outcome == StatOutcome.OpenedTicket
            ? totals.GetValueOrDefault((kind, StatOutcome.OpenedTicket)) + totals.GetValueOrDefault((kind, StatOutcome.OpenedZendesk))
            : totals.GetValueOrDefault((kind, outcome));
        int Total(StatOutcome outcome) => Kinds.Sum(k => Count(k.Kind, outcome));

        var shown = Total(StatOutcome.Shown);
        var acted = Total(StatOutcome.OpenedTicket) + Total(StatOutcome.Took);
        _summary.Text = shown == 0
            ? "No ticket notifications in the last 30 days yet."
            : $"{shown} ticket notification{(shown == 1 ? "" : "s")}. You opened or took {acted} ({Percent(acted, shown)}), " +
              $"snoozed {Total(StatOutcome.Snoozed)}, closed {Total(StatOutcome.Closed)}, and {Total(StatOutcome.TimedOut)} timed out.";

        _table.SuspendLayout();
        foreach (var old in _table.Controls.Cast<Control>().ToList())
            old.Dispose();
        _table.Controls.Clear();
        _table.ColumnStyles.Clear();
        _table.ColumnCount = Columns.Length + 1;
        // Columns size to their contents, so nothing is cut off at any display scaling.
        for (var c = 0; c <= Columns.Length; c++)
            _table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _table.Controls.Add(Cell("", Theme.Small, Theme.TextMuted, left: true), 0, 0);
        for (var c = 0; c < Columns.Length; c++)
            _table.Controls.Add(Cell(Columns[c].Label, Theme.Small, Theme.TextMuted), c + 1, 0);

        var row = 1;
        foreach (var (kind, label) in Kinds)
        {
            _table.Controls.Add(Cell(label, Theme.Body, Theme.Text, left: true), 0, row);
            for (var c = 0; c < Columns.Length; c++)
            {
                var count = Count(kind, Columns[c].Outcome);
                _table.Controls.Add(Cell(count == 0 ? "–" : count.ToString(), Theme.Body, count == 0 ? Theme.TextMuted : Theme.Text), c + 1, row);
            }
            row++;
        }

        _table.Controls.Add(Cell("Total", Theme.BodyBold, Theme.Text, left: true), 0, row);
        for (var c = 0; c < Columns.Length; c++)
            _table.Controls.Add(Cell(Total(Columns[c].Outcome).ToString(), Theme.BodyBold, Theme.Text), c + 1, row);
        _table.ResumeLayout();
    }

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{Math.Round(100.0 * part / whole)}%";

    private static Label Cell(string text, Font font, Color color, bool left = false)
    {
        var label = Theme.Label(text, font, color);
        label.Anchor = left ? AnchorStyles.Left : AnchorStyles.Right; // numbers line up on the right
        label.Margin = left ? new Padding(0, 4, 6, 4) : new Padding(4, 4, 0, 4);
        return label;
    }

    /// <summary>For the screenshot tool: a believable month, kept in memory only.</summary>
    internal static NotificationStats SampleStats()
    {
        var stats = new NotificationStats();
        void Add(NotificationKind kind, StatOutcome outcome, int count)
        {
            for (var i = 0; i < count; i++)
                stats.Record(kind, outcome);
        }
        Add(NotificationKind.NewTicket, StatOutcome.Shown, 48);
        Add(NotificationKind.NewTicket, StatOutcome.OpenedTicket, 14);
        Add(NotificationKind.NewTicket, StatOutcome.Took, 9);
        Add(NotificationKind.NewTicket, StatOutcome.OpenedZendesk, 2);
        Add(NotificationKind.NewTicket, StatOutcome.Snoozed, 4);
        Add(NotificationKind.NewTicket, StatOutcome.Closed, 6);
        Add(NotificationKind.NewTicket, StatOutcome.TimedOut, 17);
        Add(NotificationKind.MyTicket, StatOutcome.Shown, 63);
        Add(NotificationKind.MyTicket, StatOutcome.OpenedTicket, 31);
        Add(NotificationKind.MyTicket, StatOutcome.Closed, 12);
        Add(NotificationKind.MyTicket, StatOutcome.TimedOut, 20);
        Add(NotificationKind.CcTicket, StatOutcome.Shown, 7);
        Add(NotificationKind.CcTicket, StatOutcome.OpenedTicket, 2);
        Add(NotificationKind.CcTicket, StatOutcome.TimedOut, 5);
        Add(NotificationKind.HelpColleague, StatOutcome.Shown, 3);
        Add(NotificationKind.HelpColleague, StatOutcome.OpenedTicket, 2);
        Add(NotificationKind.HelpColleague, StatOutcome.Snoozed, 1);
        Add(NotificationKind.NewTicket, StatOutcome.Copied, 3);
        Add(NotificationKind.HelpColleague, StatOutcome.Copied, 1);
        return stats;
    }
}
