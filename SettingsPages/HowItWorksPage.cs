using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// "How it works": the rules behind the notifications in plain words, so you know why you see one (and why not).
/// Nothing to set here. Keep it in step with the checks (Tracking/TicketWatcher.cs, TicketRules.cs).
/// </summary>
internal sealed class HowItWorksPage : SettingsPage
{
    private static readonly (string Title, string[] Lines)[] Sections =
    [
        ("New tickets",
        [
            "A ticket in a group you monitor (Groups page) that nobody has picked up yet.",
            "Also the ones that came in while the app was closed, if they're still unassigned when it starts.",
            "Take it assigns the ticket to you and opens it. If a colleague was faster, you're told who has it.",
            "Muted clients stay quiet and aren't counted; \u201conly urgent\u201d clients only for Urgent (P1) and High (P2). Groups → Clients, or Mute \u25be on a notification."
        ]),
        ("Clients",
        [
            "Every client is normal to begin with, new ones too. Click a client on the Clients page (or Mute \u25be on a notification) to change it.",
            "Important \u2605: always notifies, also outside your hours and for priorities you unticked, and stays until you close it.",
            "Only urgent: new tickets and colleagues' tickets only for Urgent (P1) and High (P2). Mute: none of those, for good or for a day or a week.",
            "Your own and CC'd tickets always notify, whatever the client. A client can also have its own sound."
        ]),
        ("Your tickets and CC / following",
        [
            "When someone else changes a ticket assigned to you, or one you're CC'd on or follow: replies, internal notes, " +
            "status, priority or reassignment (Notifications → Which changes).",
            "Changes you make yourself never notify. Status changes leave out New → Open and Solved → Closed.",
            "Each for the priorities you choose; CC / following is Urgent (P1) and High (P2) by default."
        ]),
        ("Colleagues' tickets: Customer waiting",
        [
            "When a customer has written on a colleague's ticket and the colleague is off work (their working hours, in " +
            "their own time zone), so you can step in. Notifications → Colleagues.",
            "The customer: someone outside your company. Colleagues on the ticket, also as CC, count as you, " +
            "except the person who opened it.",
            "Waiting: the customer's message is the newest public comment. An internal note isn't an answer.",
            "Any status counts (Pending, On-hold, Escalated, …), except “In progress” and “Scheduled”: they're trusted for a while first (4 hours by default).",
            "Colleagues in your own region are left out to begin with: they usually work when you do.",
            "Not for muted clients, and only Urgent (P1) and High (P2) for \u201conly urgent\u201d ones (Groups → Clients).",
            "Only while you're working yourself, if you chose that on the Colleagues page; Catch up brings up what was held back."
        ]),
        ("Catch up",
        [
            "What still needs you since you stopped working last time (e.g. yesterday 17:00): tickets still unassigned in " +
            "your groups, and customers still waiting on your own, CC'd and colleagues' tickets.",
            "From the tray menu at any time, from the button on the morning summary, and by itself when your working day " +
            "starts (Notifications → Working hours)."
        ]),
        ("On screen",
        [
            "How long a ticket notification stays depends on its priority (Behavior → On screen). Hover over it to pause the countdown.",
            "Peek from the edge (Look & sound): it waits as a tab and counts down only after you've slid it out. " +
            "Urgent and important ones come out by themselves.",
            "Several take turns, and while you're away (screen locked, or no mouse or keyboard for a minute) they wait for you. " +
            "So they do while you present or use a full-screen app, silently, and nothing pops up over your screen. " +
            "When you're back, ones that waited are checked again and dropped if they're no longer needed."
        ]),
        ("Summaries",
        [
            "Good morning: after the app starts, what's waiting and what's watched.",
            "While you were away: instead of more than 5 notifications that waited.",
            "Many new tickets at once: one notification per group instead of one each."
        ]),
        ("Your data",
        [
            "The app reads Zendesk with your own sign-in and changes nothing, except when you click Take it.",
            "Insights are counted on this PC only; nothing is sent anywhere."
        ])
    ];

    public HowItWorksPage(SettingsContext context)
        : base(context, "How it works", "Why you get a notification, and why sometimes not. Nothing to set here.")
    {
        foreach (var (title, lines) in Sections)
        {
            var card = new Card(title, null, Kit.CardWidth);
            // Bullet and text in two columns, so wrapped lines start under the text, not under the bullet.
            var list = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty };
            for (var row = 0; row < lines.Length; row++)
            {
                var bullet = Theme.Label("•", Theme.Body, Theme.TextMuted);
                bullet.Margin = new Padding(0, 2, 8, 4);
                var text = Theme.Label(lines[row], Theme.Body, Theme.Text, Kit.CardWidth - 64);
                text.Margin = new Padding(0, 2, 0, 4);
                list.Controls.Add(bullet, 0, row);
                list.Controls.Add(text, 1, row);
            }
            card.Body.Controls.Add(list);
            Panel.Controls.Add(card);
        }
    }

    public override void Load(Settings settings) { }

    public override void Apply(Settings settings) { } // nothing to save
}
