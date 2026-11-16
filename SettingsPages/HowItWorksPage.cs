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
            "No group monitored yet: none of these come, and Catch up reminds you each time the app starts.",
            "Also the ones that came in while the app was closed, if they're still unassigned when it starts.",
            "It shows who it's from and the start of what they wrote (off with Look & sound → Show the comment text).",
            "Scheduled ones (a time is booked, nobody owns it yet) say “Scheduled”, also in the summaries.",
            "Take it assigns the ticket to you and opens it. If a colleague was faster, you're told who has it.",
            "Someone took it (or it was solved) while the notification is still up: it closes by itself within a minute.",
            "The ▾ next to Take it: up to 3 colleagues in the ticket's group who could take it, least busy first. Only those in their working hours who did something in Zendesk in the last 3 hours. Pick one to assign it to them (you're asked first).",
            "Muted clients stay quiet and aren't counted; \u201conly urgent\u201d clients only for Urgent (P1) and High (P2). [[Groups → Clients|Clients]], or Mute \u25be on a notification."
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
            "Each for the priorities you choose (the ticket's current priority; none counts as Normal). CC / following is Urgent (P1) " +
            "and High (P2) by default. Custom statuses count too, e.g. In progress → Escalated."
        ]),
        ("Colleagues' tickets: Customer waiting",
        [
            "When a customer has written on a colleague's ticket and the colleague is off work (their working hours, in " +
            "their own time zone), so you can step in. [[Notifications → Colleagues|Colleagues]]. Colleagues without a " +
            "time zone in Zendesk are always skipped.",
            "Activity in Zendesk counts too: off work but they changed or commented on a ticket in the last 30 minutes, " +
            "they're on it (no notification). In their working hours but nothing for 3 hours: probably away, so you're " +
            "told, with the reason on the notification. Only checked when a customer has written (a few requests, " +
            "remembered for 15 minutes). Calls, chat and just reading tickets don't show in Zendesk.",
            "The customer: someone outside your company. Colleagues on the ticket, also as CC, count as you, " +
            "except the person who opened it.",
            "Waiting: the customer's message is the newest public comment. An internal note isn't an answer.",
            "Any status counts (Pending, On-hold, Escalated, In progress, Scheduled, …): a customer who wrote needs an answer.",
            "Colleagues are chosen by region: newcomers follow their region's tick, and you can tick or untick people one by " +
            "one (› opens a region). Your own region starts unticked: they usually work when you do.",
            "Not for muted clients, and only Urgent (P1) and High (P2) for \u201conly urgent\u201d ones (Groups → Clients).",
            "Only while you're working yourself, if you chose that on the Colleagues page; Catch up brings up what was held back."
        ]),
        ("SLA deadlines",
        [
            "Each client's SLA type comes from Zendesk (its organization's SLA Type); clients without one count as Standard.",
            "New tickets show when the first reply is due, counted from when they were created. A customer waiting on an " +
            "assigned ticket shows when the next reply is due, counted from when they wrote.",
            "When Zendesk runs an SLA timer on the ticket, its deadline is used. Otherwise the target for the type and priority " +
            "(Notifications → SLA): yours if you changed it, else what Zendesk's timers on other tickets show, else the team's default.",
            "New SLA types in Zendesk appear there by themselves; until enough tickets show their targets, Standard's are used.",
            "P1, 24x7 and Urgent (15 Min) count around the clock; the others only Monday to Friday. First reply counts from when the " +
            "ticket was created, a follow-up from when the customer wrote.",
            "In red bold on the notification, counting down; once late it says by how much. Switch it off under [[Notifications → SLA|SLA/On notifications]]."
        ]),
        ("Experimental features",
        [
            "Each is off until you switch it on (Experimental page). English only. Worked out on this PC by rules and a small " +
            "language model that comes with the app: ticket text never leaves it, and Zendesk is never changed.",
            "The model compares meanings: “site down” and “website down” come out close, an invoice question far away. " +
            "It knows words, not your product, so some matches are missed.",
            "Priority guess: reads what the customer wrote (the first message, or the latest replies) for signs of urgency: the " +
            "site is down, security, data loss, lost sales, everyone affected, a deadline, a frustrated tone, and similar earlier " +
            "P1 or P2 tickets. When that's higher than the priority: “Set as P3 · looks like P1” with the reasons. A long wait or a " +
            "demanding SLA type adds a little, never on its own; “that fixed it” keeps it quiet. A P1 or P2 ticket without a single " +
            "sign (“no rush” counts as none): “Set as P1 · probably not”. Just a line of text: the order, sound and time on screen stay.",
            "Quiet thank-yous: a short reply that thanks or says it's solved, with no question and no “but”."
        ]),
        ("Catch up",
        [
            "What still needs you since you stopped working last time (the end of your last working day), one line each, " +
            "the number first: customers waiting for your reply, SLAs past or due today, tickets unassigned in your groups " +
            "(and how many are Scheduled), customers waiting on CC'd and colleagues' tickets, Escalated ones where the " +
            "customer hasn't had a reply for 2+ days, and Pending ones with no update for 7+ days (follow up, or solve them). " +
            "Nothing at all: “Nothing needs you right now”.",
            "Below that, your own open tickets by status (Open, In progress, Escalated, Scheduled, Pending, …), with how many " +
            "are P1 or P2. Then “To work on”: up to 5 of your New, Open and In progress tickets, most urgent first, then " +
            "the nearest SLA, then the longest untouched (click a number to open it).",
            "It greets you when the app starts (General → Startup) and right after the guide on your first start, comes by " +
            "itself when your working day starts (Notifications → Working hours), and from the tray menu at any time.",
            "Up to 5 waiting customers and up to 3 unassigned tickets also come one by one: once a working day, or when you ask.",
            "Catch up, What's new and app messages show all their text; ticket notifications show a short preview."
        ]),
        ("On screen",
        [
            "How long a ticket notification stays: Short, Normal or Long (Behavior → On screen), then by priority; Urgent stays until you close it. Catch up and app messages follow the same choice. Hover over it to pause the countdown.",
            "Peek from the edge (Look & sound): it waits as a tab and counts down only after you've slid it out. " +
            "Urgent and important ones, app messages and Catch up come out by themselves.",
            "A pill at the top: a small pill at the top center says what's there (\"2 new · 1 P1\"); point at it to see them. " +
            "Like Peek, they count down only after you've seen them, and urgent ones open it by themselves. The countdown stops " +
            "while you point at them and runs on when they close, oldest first: the bar is shorter the next time you look.",
            "A ticker at the screen edge: they scroll along the bottom (or top) of the screen; click one to open it, right-click for " +
            "Take it, Copy or Snooze. The mouse over it pauses it; each leaves after its normal time, in turn.",
            "Achievement unlocked: game style at the bottom center, one at a time, with a short tune instead of its sound (none when that's Silent).",
            "Several take turns, and while you're away (screen locked, or no mouse or keyboard for a minute) they wait for you. " +
            "So they do while you present or use a full-screen app, silently, and nothing pops up over your screen. " +
            "When you're back, ones that waited 15 minutes or more are checked again and dropped if they're no longer needed: " +
            "taken, solved or closed, or (with Behavior → Skip old news) already answered by someone.",
            "Snooze brings a notification back after the time you chose, but only if the ticket still needs you.",
            "Glow at the screen edge (Look & sound): Urgent tickets, SLAs breached or due within 15 minutes and important clients " +
            "also make the screen edges pulse briefly (in the priority's color, or the notification's color for a client); while you're away it waits until you're back. " +
            "Several at once glow only once."
        ]),
        ("Summaries",
        [
            "Catch up: the greeting when the app starts (see Catch up above).",
            "While you were away: instead of more than 5 notifications that waited.",
            "More than 5 new tickets in a group at once: one notification with the list instead of one each.",
            "Group added: when you start monitoring a group, one notification lists the unassigned tickets already waiting there."
        ]),
        ("Your data",
        [
            "The app reads Zendesk with your own sign-in and changes nothing, except when you click Take it or assign a ticket to a colleague.",
            "It checks every minute (Zendesk's own search can lag 1–3 minutes behind).",
            "Insights are counted on this PC only; nothing is sent anywhere. Its Your team table reads your region's colleagues' " +
            "tickets in your groups while the page is open (at most every 10 minutes); Solved counts the last 7 days.",
            "Send feedback (Updates & feedback) emails what you write from your own Outlook, with the log if you tick it: " +
            "ticket numbers and subjects, never comments. Without classic Outlook, an email opens for you to send."
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
