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
            "Scheduled ones (a time is booked, nobody owns it yet) say “Scheduled”, also in the summaries.",
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
            "Changes you make yourself never notify. Status changes leave out New → Open, Solved → Closed, " +
            "and a ticket reopened by a reply (the reply says it).",
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
        ("SLA deadlines",
        [
            "Each client's SLA type comes from Zendesk (its organization's SLA Type); clients without one count as Standard.",
            "New tickets show when the first reply is due, counted from when they were created. A customer waiting on an " +
            "assigned ticket shows when the next reply is due, counted from when they wrote.",
            "When Zendesk runs an SLA timer on the ticket, its deadline is used. Otherwise the target for the type and priority " +
            "(Notifications → SLA): yours if you changed it, else the team's default, else one estimated from Zendesk's timers on other tickets.",
            "New SLA types in Zendesk appear there by themselves; until enough tickets show their targets, Standard's are used.",
            "In red bold on the notification, counting down; once late it says by how much. Switch it off under Notifications → SLA."
        ]),
        ("Experimental features",
        [
            "Each is off until you switch it on (Experimental page). English only. Worked out on this PC by rules and a small " +
            "language model that comes with the app: ticket text never leaves it, and Zendesk is never changed.",
            "The model compares meanings: “site down” and “website down” come out close, an invoice question far away. " +
            "It knows words, not your product, so some matches are missed.",
            "Priority guess: reads what the customer wrote (the first message, or the latest replies) for signs of urgency: the " +
            "site is down, security, data loss, lost sales, everyone affected, a deadline, a frustrated tone, and similar earlier " +
            "P1 or P2 tickets. Shown when that's higher than the priority. A long wait or a demanding SLA type adds a little, never on its own; " +
            "“no rush” and “that fixed it” keep it quiet.",
            "Same issue?: 3 or more new tickets within an hour whose subjects read alike, once per group of tickets.",
            "Similar solved ticket and Fits you: compared with the subjects of the last 90 days of your monitored groups (and " +
            "tickets you solved), read again every 6 hours and kept in memory only.",
            "Quiet thank-yous: a short reply that thanks or says it's solved, with no question and no “but”.",
            "Topics: words in this week's subjects that come up at least 3 times and twice as often as in the weeks before."
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
        : base(context, "How it works", "Why you get a notification, and why sometimes not. Click a topic to read it.")
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

            // Collapsed to begin with, so the topics fit at a glance; a click on the card's title area opens it.
            // Opened ones stay open while the app runs (the page is rebuilt with the Settings window).
            var heading = card.Body.Controls[0];
            // Title and chevron in one full-width row (the chevron at the right edge).
            var header = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(Kit.CardWidth - 40, 0)
            };
            heading.Anchor = AnchorStyles.Left;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var chevron = new Chevron { Anchor = AnchorStyles.Right, Margin = Padding.Empty };
            card.Body.Controls.Remove(heading);
            header.Controls.Add(heading, 0, 0);
            header.Controls.Add(chevron, 1, 0);
            card.Body.Controls.Add(header);
            card.Body.Controls.SetChildIndex(header, 0);
            void Show(bool open)
            {
                list.Visible = open;
                chevron.Open = open;
                if (open) Opened.Add(title); else Opened.Remove(title);
            }
            Show(Opened.Contains(title));
            foreach (var target in new Control[] { card, card.Body, header, heading, chevron })
            {
                target.Cursor = Cursors.Hand;
                target.Click += (_, _) => Show(!list.Visible);
            }
            Panel.Controls.Add(card);
        }
    }

    /// <summary>Topics opened since the app started.</summary>
    private static readonly HashSet<string> Opened = [];

    public override void Load(Settings settings) { }

    public override void Apply(Settings settings) { } // nothing to save
}

/// <summary>A small chevron: › when closed, pointing down when open.</summary>
internal sealed class Chevron : Control
{
    private bool _open;

    public Chevron()
    {
        Size = new Size(20, 20);
        BackColor = Theme.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    [System.ComponentModel.Browsable(false), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Open
    {
        get => _open;
        set { _open = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var s = DeviceDpi / 96f;
        float cx = Width / 2f, cy = Height / 2f, a = 4f * s;
        using var pen = new Pen(Theme.TextMuted, 1.8f * s)
            { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round,
              LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
        PointF[] points = _open
            ? [new(cx - a, cy - a / 2), new(cx, cy + a / 2), new(cx + a, cy - a / 2)]
            : [new(cx - a / 2, cy - a), new(cx + a / 2, cy), new(cx - a / 2, cy + a)];
        g.DrawLines(pen, points);
    }
}
