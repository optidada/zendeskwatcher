using ZendeskWatcher.UI;

namespace ZendeskWatcher.SettingsPages;

/// <summary>
/// "How it works": the rules behind the notifications in plain words, so you know why you see one (and why not).
/// Nothing to set here. Keep it in step with the checks (Tracking/TicketWatcher.cs, TicketRules.cs).
/// </summary>
internal sealed class HowItWorksPage : SettingsPage
{
    /// <summary>A topic: what it's for (a short paragraph), where to change it, and the details as bullets.</summary>
    private sealed record Topic(string Title, string Purpose, string? Change, string[] Details);

    /// <summary>One topic per Settings page, in the sidebar's order and with its name; then what spans several pages.</summary>
    private static readonly Topic[] Topics =
    [
        new("Account",
            "Connects the app to Zendesk with your own agent account. You sign in as usual (Okta), and the app sees only what " +
            "you can see in Zendesk.",
            "Sign in or out on the Account page. To use another Zendesk account, sign out first.",
            [
                "The page shows how long you've been signed in. Zendesk asks you to sign in again after about 8 hours; the tray " +
                "icon and a notification tell you when.",
                "Catch up can be asked for here too: [[see Catch up|How it works/Catch up]]."
            ]),
        new("Groups",
            "This is where you choose which Zendesk groups you watch for new work. When a ticket lands in one of them and " +
            "nobody has taken it yet, a notification shows who it's from, what they wrote, and when the first reply is due. " +
            "Take it assigns it to you and opens it.",
            "Tick groups on the Groups page. Your own tickets notify you in any group.",
            [
                "Suggested groups are the ones you're a member of, or where your open tickets are. “(No group)” means tickets with " +
                "no group and no assignee.",
                "If a colleague took it first, you're told who has it. If someone takes it (or it's solved) while the notification " +
                "is still up, it says so (“Taken by Anna”, “Solved”) for a few seconds and closes, within a few minutes.",
                "The ▾ next to Take it suggests up to 3 colleagues who could take it instead: people who work the ticket's group " +
                "(they have unsolved tickets in it; members who work elsewhere, or managers, aren't suggested), are in their " +
                "working hours and did something in Zendesk in the last 3 hours, least busy first. Busy counts their tickets in " +
                "all groups; “waiting” is pending, on-hold, escalated and scheduled. “Handled 12 of their tickets”: the client's " +
                "tickets in that group in the last 90 days assigned to them now. It counts in the order: each one makes them " +
                "half an open ticket less busy, up to 3 open tickets, so someone who knows the client comes first unless " +
                "they're clearly busier. " +
                "You're asked before it's assigned.",
                "Tickets that came in while the app was closed come too, if they're still unassigned when it starts.",
                "Each monitored group can notify for all priorities or only the higher ones (Notifications → New tickets); " +
                "the others stay quiet and aren't counted. If one is raised while still unassigned, it comes then as “Priority " +
                "raised”. Important clients always come.",
                "The app checks every few minutes, so a ticket someone takes in between is never seen unassigned. With “Also when " +
                "someone took it before I saw it” (Notifications → New tickets), those with the priorities you pick come anyway, as " +
                "“Already taken”, saying who took it and how fast. Off by default; useful for leads who don't want to miss a P1.",
                "Scheduled ones (a time is booked, nobody owns it yet) say “Scheduled”, also in the summaries.",
                "No group ticked yet: nothing comes, and Catch up reminds you each time the app starts."
            ]),
        new("Clients",
            "This is where you control which clients you get notifications from. To begin with you hear about every client in " +
            "your monitored groups, new ones too. Mute the clients you don't handle, and make sure you never miss the important ones.",
            "On the [[Groups → Clients|Clients]] page, untick a client (or a whole group) to mute it, or use Options ▾ for more. " +
            "Options ▾ on a group's row sets it for all its clients at once, and Options ▾ on a notification's client line " +
            "does the same for one client. On your own and CC'd tickets it only offers Normal and Important: those always " +
            "notify, so muting and Only urgent don't apply there.",
            [
                "Important ★: always notifies, also outside your hours and for priorities you left out, and stays until you close it.",
                "Only urgent: notifies just for Urgent (P1) and High (P2).",
                "Mute for a while: until your shift ends, until your next shift ([[your working hours|Notifications/Working hours]]), or for a week.",
                "A sound of its own, so you know who it is before you look.",
                "Muting and “only urgent” are about new tickets and colleagues' tickets. Your own and CC'd tickets always notify, " +
                "whatever the client.",
                "The list comes from the last 90 days of tickets in your monitored groups.",
                "All · Worked with · Not worked with shows only the clients you've had a ticket from in that group in the " +
                "last 90 days (“you 6” after the name), or only the others. It only changes what's shown: a group's tick " +
                "box and Options ▾ then apply to those, e.g. Not worked with, untick the group, tick back the few to keep.",
                "Point at a client's name to see its card: its tickets in the last 90 days, who usually handles them (the people " +
                "they're assigned to now, most first, and you), and its products, as the client's " +
                "Products field in Zendesk lists them. The filter finds " +
                "products too, and says which one matched. That field is often out of date, and bundled products aren't listed, " +
                "so a client missing from the results may still have the product."
            ]),
        new("Notifications",
            "This is where you choose what you want to hear about: new tickets in your groups, changes on your own tickets and " +
            "the ones you're CC'd on or follow, and when you work.",
            "Everything is on the Notifications page: the kinds of tickets, which changes, and your working hours.",
            [
                "Changes on your tickets: when someone else replies, adds an internal note, or changes the status, priority or " +
                "assignee, you get a notification with what happened. Your own changes never notify.",
                "Which changes (Notifications → Which changes) and which priorities count: the ticket's current priority; none " +
                "counts as Normal. CC / following starts with Urgent (P1) and High (P2).",
                "Status changes leave out New → Open, Solved → Closed, and a ticket reopened by a reply (the reply says it). " +
                "Custom statuses count too, e.g. In progress → Escalated.",
                "Automatic updates stay quiet: the auto-reply and group move on a new ticket, and changes by Zendesk triggers or " +
                "service accounts. Their status changes still notify (Notifications → Which changes).",
                "[[Working hours|Notifications/Working hours]]: your hours and days (this PC's time). With “Only notify me during my working hours”, ticket " +
                "notifications outside them are skipped and Catch up shows what still needs you. With “Also outside them, while " +
                "I'm active in Zendesk”, they come as long as you changed or commented on a ticket in the last 30 minutes. " +
                "Important clients always come."
            ]),
        new("Colleagues",
            "Makes sure clients aren't left waiting when a colleague is off. If a client writes on a colleague's ticket while " +
            "that colleague is off work or away, you get a notification, so you can step in.",
            "Choose whose tickets you cover on the [[Notifications → Colleagues|Colleagues]] page.",
            [
                "Off work: outside their working hours, in their own time zone. Colleagues without a time zone in Zendesk are skipped.",
                "Activity in Zendesk counts too: off work but they changed or commented on a ticket in the last 30 minutes means " +
                "they're on it (no notification). In their working hours but nothing for 3 hours means probably away, so you're " +
                "told, with the reason on the notification. Calls, chat and just reading tickets don't show in Zendesk.",
                "Waiting means the client's message is the newest public comment; an internal note isn't an answer. Any status " +
                "counts (Pending, On-hold, Escalated, …).",
                "The client is someone outside your company. Colleagues on the ticket, also as CC, count as you, except the " +
                "person who opened it.",
                "Colleagues are chosen by region: newcomers follow their region's tick, and you can tick or untick people one by " +
                "one. Your own region starts unticked: they usually work when you do.",
                "Not for [[muted clients|How it works/Clients]], and only Urgent (P1) and High (P2) for “only urgent” ones.",
                "Only while you're working yourself, if you chose that; Catch up brings up what was held back."
            ]),
        new("Behavior",
            "Notifications are made to stay out of your way. This page sets how long they stay, what happens to the ones that " +
            "had to wait, and Snooze.",
            "On the Behavior page.",
            [
                "How long a ticket notification stays: Short, Normal or Long, then by priority; Urgent stays until you close it. " +
                "Pointing at one pauses its countdown.",
                "Away means the screen is locked, or no mouse or keyboard for a minute. While you present or use a full-screen " +
                "app, notifications wait silently, so nothing pops up over your screen.",
                "[[Already handled|Behavior/Already handled]]: when you're back, notifications that waited 15 minutes or more are checked again and dropped if " +
                "the ticket was taken, solved or closed, or (if you choose) already answered by someone.",
                "[[Snooze|Behavior/Snooze]] brings a notification back after the time you chose, but only if the ticket still needs you.",
                "Several at once take turns instead of piling up."
            ]),
        new("Look & sound",
            "How notifications look and sound, from a classic list in the corner to a small pill at the top of the screen.",
            "On the [[Look & sound|Look & sound]] page. [[Try it|Look & sound/Try it]] sends samples with your choices, even before saving.",
            [
                "Styles: a list, Peek (waits as a tab at the screen edge), a pill at the top center that sums them up, a ticker " +
                "along the screen edge, or Achievement (one at a time, with a short tune).",
                "With more than one monitor, notifications appear on the one you're working on (where the mouse is). The " +
                "ones that follow join them there until all are gone; then the next ones again go where the mouse is.",
                "Glow at the screen edge: urgent tickets, SLAs breached or due within 15 minutes and important clients make the " +
                "screen edges pulse briefly, so you notice them even when you look away.",
                "A sound per kind of notification, and a volume for all of them (this app only).",
                "Show the comment text: what the client wrote on new tickets, and replies and notes on changes. When off, " +
                "notifications only say who wrote."
            ]),
        new("SLA",
            "Shows how much time is left before a reply is due, counting down in red on the notification, so you know what to " +
            "answer first. Once it's late, it says by how much.",
            "Adjust the targets, or switch it off, on the [[Notifications → SLA|SLA]] page.",
            [
                "New tickets show when the first reply is due, counted from when they were created. A client waiting on an " +
                "assigned ticket shows when the next reply is due, counted from when they wrote.",
                "Each client's SLA type comes from Zendesk (its organization's SLA Type); clients without one count as Standard.",
                "When Zendesk runs an SLA timer on the ticket, its deadline is used. Otherwise the target for the type and " +
                "priority: yours if you changed it, else what Zendesk's timers on other tickets show, else the team's default.",
                "P1, 24x7 and Urgent (15 Min) count around the clock; the others only Monday to Friday.",
                "New SLA types in Zendesk appear on the SLA page by themselves; until enough tickets show their targets, " +
                "Standard's are used."
            ]),
        new("General",
            "The look of the app itself, and how it starts.",
            "On the General page.",
            [
                "[[App theme|General/App theme]]: the look of the Settings window, the tray menu, and notifications unless they have their own style " +
                "(Look & sound). Native follows your Windows colors.",
                "[[Startup|General/Startup]]: start Zendesk Watcher when you sign in to Windows, and Catch up when it starts."
            ]),
        new("Updates & feedback",
            "Keeps the app up to date, and lets you tell its makers what you think.",
            "On the [[Updates & feedback|Updates & feedback]] page.",
            [
                "New versions install by themselves when the app starts (a few seconds) and then say what's new. One that comes out while it runs downloads in the background; “Update ready” offers to restart now.",
                "[[Send feedback|Updates & feedback/Feedback]] emails what you write from your own Outlook, with the log if you tick it: ticket numbers and " +
                "subjects, never comments. Without classic Outlook, an email opens for you to send.",
                "[[Reset to defaults|Updates & feedback/Reset]] puts all settings back, except your account, groups and clients. Nothing changes until you save."
            ]),
        new("Insights",
            "Shows what's waiting right now, how you used your notifications, and tips to get fewer of the ones you never act on.",
            "On the Insights page. Clear statistics starts the counting over.",
            [
                "Counted on this PC only; nothing is sent anywhere.",
                "[[Team workload|Insights]]: everyone in your monitored groups (or the one group you pick) who is in working hours " +
                "(in their Zendesk time zone; people off work aren't shown), and their open tickets there. The bar counts open and " +
                "In progress tickets twice, Urgent and High once more, escalated, scheduled and waiting ones once, like the " +
                "suggestions next to Take it. Read while the page is open (at most every 10 minutes); each group's last result " +
                "shows right away."
            ]),
        new("Experimental",
            "Two optional helpers that work entirely on your PC: a warning when a ticket sounds more urgent than its priority, " +
            "and quiet thank-yous. Both are off until you switch them on.",
            "Switch them on on the Experimental page (English only).",
            [
                "Priority guess: “Set as P3 · looks like P1”, with the reasons, when what the client wrote sounds more urgent: " +
                "the site is down, security, data loss, lost sales, everyone affected, a deadline, a frustrated tone, or similar " +
                "earlier P1 or P2 tickets. A P1 or P2 ticket without a single sign says “Set as P1 · probably not”. Just a line " +
                "of text: the order, sound and time on screen stay.",
                "Quiet thank-yous: a client's plain “Thanks, that worked!” notifies without sound, and Catch up skips it.",
                "Worked out by rules and a small language model that comes with the app: ticket text never leaves your PC, and " +
                "Zendesk is never changed. It knows words, not your product, so some matches are missed."
            ]),
        new("Catch up",
            "Your starting point for the day: one notification with what still needs you since you last worked, and which of " +
            "your own tickets to start with.",
            "It comes by itself once a day: the first time the app starts that day (General → Startup), or when your working day " +
            "starts if it's already running (Notifications → Working hours). Later restarts that day don't bring it again. Ask " +
            "for it any time from the tray menu, the Account page, or with Ctrl+Alt+Z.",
            [
                "One line each, the number first: clients waiting for your reply, SLAs past or due today, unassigned tickets in " +
                "your groups, clients waiting on CC'd and colleagues' tickets, Escalated ones with no reply to the client for " +
                "2+ days, and Pending ones with no update for 7+ days. Nothing at all: “Nothing needs you right now”.",
                "Then your own open tickets by status, with how many are P1 or P2, and “To work on”: up to 5 of your New, Open " +
                "and In progress tickets, most urgent first, then the nearest SLA, then the longest untouched. Click a number to " +
                "open it; with more, “+N more” opens all of them in Zendesk.",
                "Up to 5 waiting clients and up to 3 unassigned tickets also come one by one: once a working day, or when you ask."
            ]),
        new("Summaries",
            "When a lot happens at once, you get one notification with a list instead of a flood.",
            null,
            [
                "While you were away: instead of more than 5 notifications that waited.",
                "More than 5 new tickets in a group at once: one notification instead of one each.",
                "Group added: when you start monitoring a group, the unassigned tickets already waiting there."
            ]),
        new("Priority levels",
            "What each priority means, as a reminder when the priority a client chose doesn't match the case.",
            null,
            [.. PriorityLevels.All.Select(l => $"{l.Tag}: {l.Definition}")]),
        new("Your data",
            "The app reads Zendesk with your own sign-in and only changes something when you click Take it or assign a ticket " +
            "to a colleague. Nothing about your tickets is sent anywhere else.",
            null,
            [
                "It checks every 3 minutes, every 10 while you're away, and right away when you're back, so it adds little to your " +
                "company's shared Zendesk limit. New unassigned tickets are also checked in between, so they show within about " +
                "1½ minutes. If Zendesk says it's busy, the app pauses until it may go on.",
                "Names are remembered for a week (colleagues with their time zone; people at clients only by name and email domain), so " +
                "starting the app doesn't look them all up again.",
                "Your settings, statistics and the lists Settings shows are kept in your Windows profile on this PC; with Priority " +
                "guess on, also 90 days of ticket numbers, subjects and priorities (no ticket text), read again once a week."
            ])
    ];

    public HowItWorksPage(SettingsContext context)
        : base(context, "How it works", "What each page is for, and how the app decides. Click a topic to read it.")
    {
        const int textWidth = Kit.CardWidth - 44;
        foreach (var topic in Topics)
        {
            var title = topic.Title;
            var card = new Card(title, null, Kit.CardWidth);
            // What it's for, where to change it, then the details as bullets (smaller, for those who want to know exactly).
            var content = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Surface,
                Margin = Padding.Empty
            };
            var purpose = Theme.Label(topic.Purpose, Theme.Body, Theme.Text, textWidth);
            purpose.Margin = new Padding(0, 4, 0, 8);
            content.Controls.Add(purpose);
            if (topic.Change is { } change)
            {
                content.Controls.Add(SectionLabel("Where to change it"));
                var where = Theme.Label(change, Theme.Body, Theme.Text, textWidth);
                where.Margin = new Padding(0, 0, 0, 8);
                content.Controls.Add(where);
            }
            content.Controls.Add(SectionLabel("Details"));
            // Bullet and text in two columns, so wrapped lines start under the text, not under the bullet.
            var list = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Theme.Surface, Margin = Padding.Empty };
            for (var row = 0; row < topic.Details.Length; row++)
            {
                var bullet = Theme.Label("•", Theme.Body, Theme.TextMuted);
                bullet.Margin = new Padding(0, 1, 6, 3);
                var text = Theme.Label(topic.Details[row], Theme.Body, Theme.TextMuted, textWidth - 20);
                text.Margin = new Padding(0, 1, 0, 3);
                list.Controls.Add(bullet, 0, row);
                list.Controls.Add(text, 1, row);
            }
            content.Controls.Add(list);
            card.Body.Controls.Add(content);

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
            // The title centered when the topic is folded: as much space below it as above.
            heading.Margin = Padding.Empty;
            card.Padding = card.Padding with { Bottom = card.Padding.Top };
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
                content.Visible = open;
                chevron.Open = open;
                if (open) Opened.Add(title); else Opened.Remove(title);
            }
            Show(Opened.Contains(title));
            _openers[title] = () => Show(true);
            foreach (var target in new Control[] { card, card.Body, header, heading, chevron })
            {
                target.Cursor = Cursors.Hand;
                target.Click += (_, _) => Show(!content.Visible);
            }
            Panel.Controls.Add(card);
        }
    }

    /// <summary>A small bold heading inside a topic ("Where to change it", "Details").</summary>
    private static Label SectionLabel(string text)
    {
        var label = Theme.Label(text, Theme.BodyBold, Theme.Text);
        label.Margin = new Padding(0, 2, 0, 2);
        return label;
    }

    /// <summary>Topics opened since the app started.</summary>
    private static readonly HashSet<string> Opened = [];

    /// <summary>Opens each topic, by title (a link to a topic opens it).</summary>
    private readonly Dictionary<string, Action> _openers = [];

    /// <summary>Opens the topic with this title (a link like "see Catch up"), so the page can scroll to it.</summary>
    public void OpenTopic(string title)
    {
        if (_openers.TryGetValue(title, out var open))
        {
            open();
            Panel.PerformLayout();
        }
    }

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
