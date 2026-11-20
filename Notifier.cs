using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher;

/// <summary>
/// Shows notifications in Zendesk Watcher's own styles (popups in the chosen theme).
/// While presenting or in full screen, popups wait until it's over, and nothing makes a sound.
/// </summary>
public sealed class Notifier : INotificationSink
{
    public const string ActionOpenTicket = NotificationActions.OpenTicket;
    public const string ActionOpenZendesk = NotificationActions.OpenZendesk;
    public const string ActionSignIn = NotificationActions.SignIn;
    public const string ActionNone = NotificationActions.None;
    public const string ActionInfo = NotificationActions.Info;
    public const string ActionUpdate = NotificationActions.Update;
    public const string ActionTake = NotificationActions.Take;
    public const string ActionCatchUp = NotificationActions.CatchUp;
    public const string ActionSettings = NotificationActions.Settings;

    private readonly PopupManager _popups;

    public Notifier(Action<string, long?> onAction) =>
        _popups = new PopupManager(onAction, Snooze, OnPopupClosed, Copy)
        {
            Shown = OnShown,
            ClientChosen = (content, choice) => { if (content.IsTicketNotification) ChooseForClient(choice); }, // samples: nothing to change
            CopiedId = content => { if (content.IsTicketNotification) Count(content, StatOutcome.Copied); },
            OnScreenChanged = on => NotificationsOnScreenChanged?.Invoke(on)
        };

    /// <summary>Notifications on screen went from none to some (true) or back (false).</summary>
    public event Action<bool>? NotificationsOnScreenChanged;

    /// <summary>A keyboard shortcut for the oldest notification on screen (see <see cref="PopupManager.ActOnOldest"/>).</summary>
    public bool ActOnOldest(UI.Popups.PopupForm.Result result) => _popups.ActOnOldest(result);

    /// <summary>
    /// A choice from the "Options ▾" menu on a notification (mute, mute for a while, only urgent, important): the app
    /// saves it for the client (Clients page).
    /// </summary>
    public event Action<ClientChoice>? ClientChoiceRequested;

    private void ChooseForClient(ClientChoice choice)
    {
        Log.Info($"Client {choice.Name} ({choice.Id}) set to {choice.Mode}{(choice.IsMuted ? ", muted" : "")}{(choice.IsMuted && choice.MutedUntil is { } until ? $" until {until.ToLocalTime():yyyy-MM-dd HH:mm}" : "")} from a notification");
        ClientChoiceRequested?.Invoke(choice);
    }

    /// <summary>Counts what happened to a ticket notification, per kind and (for the Insights suggestion) per client.</summary>
    private void Count(NotificationContent content, StatOutcome outcome)
    {
        if (content.TicketId is { } ticketId && Tracking.TicketWatcher.IsTestTicket(ticketId))
            return; // --test-take: not a real notification, like samples
        Stats.Record(content.Kind, outcome);
        if (content.ClientId is { } id && content.ClientName is { } name)
            Stats.RecordClient(id, name, outcome);
        if (IsAction(outcome))
            MarkActed(content);
    }

    /// <summary>
    /// Any reaction is acting on it (Daniel: whatever takes the ticket further): opened, took or assigned, copied (all of
    /// it or the number), or snoozed. Closing, a choice for the client (muting doesn't take the ticket further) and only
    /// reading it aren't.
    /// </summary>
    private static bool IsAction(StatOutcome outcome) =>
        outcome is StatOutcome.OpenedTicket or StatOutcome.OpenedZendesk or StatOutcome.Took or StatOutcome.Copied or StatOutcome.Snoozed;

    /// <summary>Notifications already counted as acted on (by <see cref="NotificationContent.StatId"/>, so snoozed repeats too).</summary>
    private readonly HashSet<Guid> _acted = [];

    /// <summary>Counts a ticket notification as acted on, once.</summary>
    private void MarkActed(NotificationContent content)
    {
        if (!content.IsTicketNotification)
            return;
        if (_acted.Count > 5000)
            _acted.Clear(); // a long-running app: old notifications are long gone
        if (_acted.Add(content.StatId))
            Count(content, StatOutcome.Acted);
    }

    /// <summary>A notification appeared on screen: ticket notifications count in the statistics (snoozed repeats don't).</summary>
    private void OnShown(NotificationContent content)
    {
        if (content.IsTicketNotification && !content.IsSnoozedRepeat)
            Count(content, StatOutcome.Shown);
    }

    /// <summary>Copies a ticket notification for Teams and counts it. Runs on the UI thread.</summary>
    public void Copy(NotificationContent content)
    {
        ChipSounds.Stop();
        try
        {
            TicketShare.Copy(content, content.TicketId is { } id ? Settings.TicketUrl(id) : null);
            if (content.IsTicketNotification)
                Count(content, StatOutcome.Copied);
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy the notification", ex);
        }
    }

    /// <summary>Where ticket notifications and what was done with them are counted.</summary>
    public NotificationStats Stats { get; set; } = NotificationStats.Shared;

    private void OnPopupClosed(NotificationContent content, PopupForm.Result result)
    {
        if (result is not (PopupForm.Result.Timeout or PopupForm.Result.None))
            ChipSounds.Stop(); // acted on: the music can stop
        if (content.IsTicketNotification && StatForPopup(result, content.ResolvedAction) is { } outcome)
            Count(content, outcome);
    }

    /// <summary>What a closed popup means, or null if it isn't worth counting.</summary>
    public static StatOutcome? StatForPopup(PopupForm.Result result, string action) => result switch
    {
        PopupForm.Result.Open when action == ActionOpenTicket => StatOutcome.OpenedTicket,
        PopupForm.Result.Open when action == ActionOpenZendesk || action.StartsWith(NotificationActions.SearchPrefix, StringComparison.Ordinal) => StatOutcome.OpenedZendesk,
        PopupForm.Result.Take => StatOutcome.Took,
        PopupForm.Result.Snooze => StatOutcome.Snoozed,
        PopupForm.Result.Dismiss => StatOutcome.Closed,
        PopupForm.Result.Timeout => StatOutcome.TimedOut,
        _ => null
    };

    /// <summary>While paused, changes are still recorded but nothing is shown. Snoozed popups wait for the end of the pause.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value)
                ShowHeldSnoozes();
        }
    }

    private bool _paused;

    /// <summary>
    /// Of the given notifications, the ones still needed (deep: also read what happened on the ticket since, see
    /// <see cref="Settings.DropAnswered"/>). Set by the app; without it (or if the check fails) everything shows.
    /// Used for "only if still needed" snoozes and for notifications that waited long.
    /// </summary>
    public Func<IReadOnlyList<NotificationContent>, bool, Task<List<NotificationContent>>>? StillNeeded
    {
        get => _stillNeeded;
        set => _popups.StillNeeded = _stillNeeded = value;
    }

    private Func<IReadOnlyList<NotificationContent>, bool, Task<List<NotificationContent>>>? _stillNeeded;

    // ------------------------------------------------------------------ snooze

    private readonly List<NotificationContent> _heldSnoozes = [];

    /// <summary>The user snoozed a popup: show it again after the snooze time, if it still applies then.</summary>
    private void Snooze(NotificationContent content)
    {
        var minutes = Math.Clamp(Settings.SnoozeMinutes, 1, 24 * 60);
        Log.Info($"Snoozed for {minutes} min: {content.LogName}");
        var timer = new System.Windows.Forms.Timer { Interval = minutes * 60_000 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            if (Paused)
                _heldSnoozes.Add(content);
            else
                _ = ShowSnoozedAsync(content);
        };
        timer.Start();
    }

    private void ShowHeldSnoozes()
    {
        var held = _heldSnoozes.ToList();
        _heldSnoozes.Clear();
        foreach (var content in held)
            _ = ShowSnoozedAsync(content);
    }

    private async Task ShowSnoozedAsync(NotificationContent content)
    {
        if (content.TicketId is not null && StillNeeded is { } check) // only if the ticket still needs attention
        {
            try
            {
                if ((await check([content], true)).Count == 0)
                {
                    Log.Info($"Snoozed notification no longer needed, not shown again: {content.LogName}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not check a snoozed ticket; showing it again anyway", ex);
            }
        }

        if (Paused)
        {
            _heldSnoozes.Add(content); // paused while checking
            return;
        }
        var heading = content.Heading.StartsWith("Snoozed · ") ? content.Heading : $"Snoozed · {content.Heading}";
        Show(content with { Heading = heading, IsSnoozedRepeat = true }, Settings);
    }

    /// <summary>The settings that decide theme, sound, display time, logo and grouping.</summary>
    public Settings Settings { get; set; } = new();

    public void Show(NotificationContent content)
    {
        if (!Paused || content.RequestedByUser)
            Show(content, Settings);
    }

    public void CloseTaken(long ticketId, string? note = null) => _popups.CloseNewTicket(ticketId, note);

    public void NoteTaken(long ticketId, string note) => _popups.NoteNewTicket(ticketId, note);

    public void HeldBackOtherProduct(long ticketId) => Stats.RecordHeldBack();

    // Replies to something the user just clicked ("No updates", "already taken") stay quiet and short.
    private static readonly NotificationStyle QuietInfo = new() { Sound = ToastSound.Silent, Display = ToastDisplay.Standard };

    /// <summary>App messages (update ready, welcome, group not found) as chosen under Appearance.</summary>
    private NotificationStyle ActOnIt => Settings.AppMessageStyle;

    /// <summary>Sign-in required: the App messages sound, but always on screen until dismissed (nothing works until then).</summary>
    private NotificationStyle Blocking => new() { Sound = Settings.AppMessageStyle.Sound, Display = ToastDisplay.UntilDismissed };

    /// <summary>Shown even when paused: the app cannot work until the user signs in.</summary>
    public void ShowSignInRequired() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Sign-in required", "Your Zendesk session has expired", "Click to sign in again.")
    {
        Action = ActionSignIn,
        Style = Blocking
    }, Settings);

    /// <summary>The very first start, while Settings opens: what the app does and how to get going.</summary>
    private const string WelcomeBody =
        "Settings is open and leads you through the few things that matter: **signing in**, your **groups**, " +
        "your **working hours** and whose tickets you cover. The rest can wait.";

    /// <summary>The very first start: what to do next. Click opens Settings.</summary>
    public void ShowWelcome() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher", WelcomeBody)
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    /// <summary>settings.json was damaged at start (see <see cref="Settings.Load()"/>). Shown even when paused; click opens Settings.</summary>
    public void ShowSettingsProblem(string text) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Settings file damaged", text)
    {
        Action = ActionSettings,
        Style = Blocking
    }, Settings);

    /// <summary>Monitored groups that Zendesk no longer lists. Shown even when paused; click opens Settings.</summary>
    public void ShowMissingGroups(IReadOnlyCollection<string> names) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher",
        names.Count == 1 ? "A monitored group was not found" : $"{names.Count} monitored groups were not found",
        $"{string.Join(", ", names)}: deleted, or you no longer have access. Open Settings to untick {(names.Count == 1 ? "it" : "them")}.")
    {
        Action = ActionSettings,
        Style = ActOnIt
    }, Settings);

    /// <summary>
    /// "While you were away": one notification instead of many waiting ones, e.g. "12 new tickets" / "3 updates on your
    /// tickets" / "2 customers waiting on colleagues' tickets". Click opens Zendesk.
    /// </summary>
    /// <param name="title">"While you were away", or "While you were busy" after a very busy spell.</param>
    internal static NotificationContent AwaySummary(IReadOnlyList<NotificationContent> waiting, int dropped, string title = "While you were away")
    {
        int Count(NotificationKind kind) => waiting.Where(c => c.Kind == kind).Sum(c => c.TicketCount);
        static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

        var lines = new List<string>();
        if (Count(NotificationKind.NewTicket) is > 0 and var fresh)
            lines.Add(Plural(fresh, "new ticket", "new tickets") +
                Tracking.TicketRules.ScheduledNote(fresh, waiting.Where(c => c.Kind == NotificationKind.NewTicket).Sum(c => c.ScheduledCount)));
        if (Count(NotificationKind.MyTicket) is > 0 and var mine)
            lines.Add(Plural(mine, "update on your tickets", "updates on your tickets"));
        if (Count(NotificationKind.CcTicket) is > 0 and var cc)
            lines.Add(Plural(cc, "update on CC'd tickets", "updates on CC'd tickets"));
        if (Count(NotificationKind.HelpColleague) is > 0 and var help)
            lines.Add(Plural(help, "client waiting on a colleague's ticket", "clients waiting on colleagues' tickets"));
        if (dropped > 0)
            lines.Add($"{Plural(dropped, "other is", "others are")} no longer needed (taken, solved or answered)");

        return new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", title, string.Join("\n", lines))
        {
            Action = ActionOpenZendesk,
            MaxBodyLines = 7
        };
    }

    /// <summary>The app was started again while running: a short, quiet message with only Close. Shown even when paused.</summary>
    public void ShowAlreadyRunning() => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", "Zendesk Watcher is already running",
        "It's the icon in the system tray, next to the clock.")
    {
        Action = ActionInfo,
        Style = QuietInfo
    }, Settings);

    /// <summary>Feedback on something the user did (e.g. "Take it"). Shown even when paused.</summary>
    public void ShowInfo(string title, string body, long? ticketId = null) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Zendesk Watcher", title, body, ticketId)
    {
        Action = ticketId is null ? ActionInfo : null,
        Style = QuietInfo
    }, Settings);

    /// <summary>A new version has been downloaded; clicking restarts into it.</summary>
    public void ShowUpdateReady(string version, string? highlights = null) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Update ready", $"Zendesk Watcher {version} is ready",
        (highlights is null ? "" : highlights + "\n") + "Click to restart and update now, or it installs on the next start.")
    {
        Action = ActionUpdate,
        Style = ActOnIt
    });

    /// <summary>After an update: what's new since the version that ran before; a click opens the release notes.</summary>
    public void ShowWhatsNew(string title, string body, string releaseNotesUrl) => Show(new NotificationContent(
        NotificationKind.MyTicket, "Updated", title, body)
    {
        Action = NotificationActions.Url(releaseNotesUrl),
        Style = ActOnIt,
        MaxBodyLines = 7
    });

    /// <summary>Sample notifications with the given (possibly unsaved) settings. Shown even when paused.</summary>
    public void ShowTest(Settings settings, NotificationKind kind)
    {
        foreach (var sample in TestSamples(kind))
            Show(WithGuess(sample, settings) with { IsSample = true }, settings);
    }

    /// <summary>Several samples at once, to see how popups stack.</summary>
    public void ShowTestBurst(Settings settings)
    {
        foreach (var sample in TestSamples(null))
            Show(WithGuess(sample, settings) with { IsSample = true }, settings);
    }

    /// <summary>With the priority guess on (Experimental page), the colleague sample gets it, from the same rules as real tickets.</summary>
    private static NotificationContent WithGuess(NotificationContent sample, Settings settings) =>
        settings.GuessPriority && sample.Kind == NotificationKind.HelpColleague
            ? sample with { PriorityHint = Tracking.PriorityGuess.Guess(sample.Priority, sample.Title,
                [Markdown.Strip(sample.Body.Split('\n')[0])], "slapremium", TimeSpan.Zero) }
            : sample;

    /// <summary>
    /// What "Try it" shows: the samples of one kind, or several at once (<paramref name="kind"/> null). Never marked as
    /// ticket notifications, so they don't count in the statistics.
    /// </summary>
    internal static IEnumerable<NotificationContent> TestSamples(NotificationKind? kind)
    {
        // A different funny ticket each time (the screenshots always use the first ones, see AllSamples).
        int Pick(int count) => Random.Shared.Next(count);
        var news = Enumerable.Range(0, NewTickets.Length).OrderBy(_ => Random.Shared.Next()).Take(2).ToList(); // two different new tickets
        var samples = kind is { } single
            ? Samples(single, Pick(single switch { NotificationKind.NewTicket => NewTickets.Length, NotificationKind.HelpColleague => Waiting.Length, _ => Updates.Length }))
            : Samples(NotificationKind.NewTicket, news[0]).Concat(Samples(NotificationKind.MyTicket, Pick(Updates.Length)))
                .Concat(Samples(NotificationKind.HelpColleague, Pick(Waiting.Length))).Concat(Samples(NotificationKind.NewTicket, news[1])).Append(ListSample);
        return samples.Select(sample => sample with { Priority = sample.TicketCount > 1 ? null : RandomPriority() }).ToList();
    }

    /// <summary>A list of tickets ("Group added"): the ticket numbers look like links (in samples they don't open anything).</summary>
    private static NotificationContent ListSample => new(NotificationKind.NewTicket, "Group added", "7 unassigned tickets already waiting",
        "#123460 Desk moved to storage room B after the reorganization, stapler missing\n#123461 The internet (black box) is missing\n#123462 Printer says PC LOAD LETTER\n" +
        "#123463 **Scheduled** · The Bobs would like a word\n#123464 It's a UNIX system! I know this!\n\u2026 and 2 more: click to see them all",
        Context: "Support Tier 2") { Action = ActionNone, TicketCount = 7, MaxBodyLines = 6, OneRowPerLine = true };

    /// <summary>
    /// A random priority for each sample (sometimes none), so "Try it" shows the colors and how long each priority stays
    /// (Behavior → Display time) instead of always the same.
    /// </summary>
    private static string? RandomPriority() => Random.Shared.Next(5) switch
    {
        0 => "urgent",
        1 => "high",
        2 => "normal",
        3 => "low",
        _ => null
    };

    /// <summary>All samples (for the developer --render-popups helper).</summary>
    public static IEnumerable<NotificationContent> AllSamples() =>
        Samples(NotificationKind.NewTicket).Concat(Samples(NotificationKind.MyTicket)).Append(
            new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123458 Ah ah ah, you didn't say the magic word: editors locked out", "",
                TicketId: 123458, Priority: "urgent", Context: "Support Tier 1") { CanTake = true, Client = "InGen" }).Append(
            // The priority guess (Experimental): higher than set, and "probably not" for an urgent one with no sign of urgency.
            GuessSample(new NotificationContent(NotificationKind.HelpColleague, "Colleague's ticket", "#123459 Flair orders fail for all restaurants",
                $"**Stan**\nPeter is off work · {Clock.DayTime(DateTime.Today.AddHours(21).AddMinutes(40))} their time · last active 4 h ago\n“Any update? Orders are not going through for any of our restaurants, and our launch is tomorrow morning.”",
                TicketId: 123459, Priority: "normal", Context: "Support Tier 2") { EventTime = DateTimeOffset.UtcNow.AddMinutes(-40) })).Append(
            GuessSample(new NotificationContent(NotificationKind.NewTicket, "New ticket", "#123465 Homepage plays Surfin' Bird on a loop",
                "Brian Griffin", TicketId: 123465, Priority: "urgent", Context: "Support Tier 1") { EventTime = DateTimeOffset.UtcNow.AddMinutes(-3) },
                "Hi, our homepage keeps playing Surfin' Bird. Everyone here now knows the bird is the word. Thanks!")).Append(
            new NotificationContent(NotificationKind.MyTicket, $"Catch up · since yesterday {Clock.Hour(17)}", "Good morning, Roy",
                Tracking.TicketRules.CatchUpBody(new Tracking.TicketRules.CatchUpCounts
                {
                    Mine = 2, SlaPast = 1, SlaToday = 2, FirstDueToday = DateTime.Today.AddHours(10).AddMinutes(30),
                    Unassigned = 5, Scheduled = 1, QuietEscalated = 1, QuietPending = 2
                }, Tracking.TicketRules.YourTicketsText([("Open", "open", "urgent"), ("Open", "open", null), ("Open", "new", null), ("Open", "open", null),
                    ("In progress", "open", null), ("In progress", "open", null), ("Escalated", "hold", null), ("Scheduled", "hold", null),
                    ("Pending", "pending", null), ("Pending", "pending", null), ("Pending", "pending", null)]))) { MaxBodyLines = 16 }).Append(
            new NotificationContent(NotificationKind.MyTicket, "Zendesk Watcher", "Welcome to Zendesk Watcher", WelcomeBody)
                { Action = ActionSettings }).Append(ListSample);

    /// <summary>A sample with the priority guess worked out from its title and the customer's words (by default the body's first line).</summary>
    private static NotificationContent GuessSample(NotificationContent sample, string? customerText = null) => sample with
    {
        PriorityHint = Tracking.PriorityGuess.Guess(sample.Priority, sample.Title, [customerText ?? Markdown.Strip(sample.Body.Split('\n')[0])],
            Tracking.Sla.StandardKey, TimeSpan.Zero)
    };

    /// <summary>Shows "Mute" on samples; muting a sample does nothing (see <see cref="Mute"/>).</summary>
    private static ClientChoice SampleClient(string name) => new() { Id = -1, Name = name };

    /// <summary>New tickets for the samples: title, customer, what they wrote, client.</summary>
    private static readonly (string Title, string Customer, string Text, string Client)[] NewTickets =
    [
        ("TPS reports missing the new cover sheet", "Bill Lumbergh",
            "Yeah, hi. So if you could go ahead and add the new cover sheet to the TPS reports, that'd be great. Mmm-kay?", "Initech"),
        ("Ah ah ah, you didn't say the magic word: editors locked out", "Ray Arnold",
            "Every login gets a waving cartoon man saying \u201cAh ah ah\u201d. Hold on to your butts, we're restarting the server.", "InGen"),
        ("Printer says PC LOAD LETTER", "Tom Smykowski",
            "What does that even mean? I deal with the customers so the engineers don't have to. I have people skills!", "Initrode"),
        ("We spared no expense, but the site is still slow", "John Hammond",
            "Pages take ages to load. We spared no expense on the servers. Please have a look before the opening.", "InGen"),
        ("Emergency number 0118 999 881 999 119 725 3 doesn't fit in the form", "Douglas Reynholm",
            "The phone field stops after 15 digits. Rather important, it's the emergency number. Easy to remember, too.", "Reynholm Industries"),
        ("Contact form rejects my plan for world domination", "Stewie Griffin",
            "Blast! The form says my message is too long. It's 40 pages, and every page matters.", "Happy-Go-Lucky Toy Factory"),
        ("Flair counter on the staff page stuck at 14 pieces", "Joanna",
            "The minimum is 15. I'd rather not be told about it again. Can you fix the counter?", "Chotchkie's"),
        ("Homepage turned bright green overnight", "Stanley Ipkiss",
            "Somebody stop me! The whole homepage is green and the menu spins around. It was fine yesterday.", "Edge City Bank"),
        ("Our installer keeps calling to check the connection", "Steven Kovacs",
            "He set up the site, now he calls every day and says we're friends. How do we turn off his access?", "Kovacs Architects"),
        ("So you're telling me there's a chance the launch works?", "Lloyd Christmas",
            "Tests pass one time in a million. We're going for it. What could possibly go wrong?", "Mutts Cutts")
    ];

    /// <summary>Replies on my (or CC'd) tickets for the samples: title, who, what they wrote, client.</summary>
    private static readonly (string Title, string Who, string Text, string Client)[] Updates =
    [
        ("Search shows results from 1997", "Jen Barber", "I turned it off and on again. The reindex helped, but one site still shows old results…", "Reynholm Industries"),
        ("Red stapler not found in search", "Milton Waddams", "I was told I could keep my stapler… Excuse me, I believe search has my stapler.", "Initech"),
        ("Unpublished pages show up on the live site", "Ian Malcolm", "Life, uh, finds a way. So do unpublished pages, apparently…", "InGen"),
        ("Our slogan was changed to \u201cFreakin' sweet\u201d", "Peter Griffin", "Not sure who changed it. Probably me. Can you put the old one back?", "Pawtucket Brewery"),
        ("Fire alarm email went to every customer", "Maurice Moss", "I've sent a follow-up: \u201cDear Sir/Madam, the fire is under control.\u201d Is that enough?", "Reynholm Industries"),
        ("New theme is live, but the logo is still green", "Stanley Ipkiss", "Smokin'! Everything looks great now, except the logo…", "Edge City Bank"),
        ("Site works in the van, but not in the office", "Harry Dunne", "Just when I thought you couldn't fix it any better, you go and do something like this… and totally redeem yourself!", "Mutts Cutts")
    ];

    /// <summary>Customers waiting on colleagues' tickets for the samples: title, customer, reply, client, the colleague who's off.</summary>
    private static readonly (string Title, string Customer, string Text, string Client, string Colleague)[] Waiting =
    [
        ("Flair orders fail for all restaurants", "Stan", "Any update? This is blocking our launch tomorrow.", "Chotchkie's", "Peter"),
        ("Visitor center booking page is down", "Ellie Sattler", "Any update? Bookings are failing and the park opens tomorrow.", "InGen", "Moss"),
        ("Checkout fails for all customers", "Denholm Reynholm", "Any update? Nobody can buy anything and the board meets tomorrow.", "Reynholm Industries", "Samir"),
        ("Toy recall page shows the wrong toys", "Cleveland Brown", "Any update? Customers are returning the wrong toys, and that's no good.", "Happy-Go-Lucky Toy Factory", "Roy"),
        ("Connection drops every evening", "Chip Douglas", "Any update? I'll be checking in. Every day. We're friends now, right?", "Cable Guy Services", "Jen"),
        ("Ski lodge booking page is down", "Mary Swanson", "Any update? Bookings are failing and the season in Aspen starts tomorrow.", "Swanson Ski Lodge", "Michael")
    ];

    /// <summary>One sample of a kind: <paramref name="pick"/> chooses which (0: the one in the screenshots).</summary>
    private static IEnumerable<NotificationContent> Samples(NotificationKind kind, int pick = 0)
    {
        if (kind == NotificationKind.NewTicket)
        {
            var (title, customer, text, client) = NewTickets[pick % NewTickets.Length];
            var minutes = 2 + pick * 3 % 11;
            return [new NotificationContent(kind, "New ticket", $"#{123456 + pick * 11} {title}",
                $"{customer}\n“{text}”", TicketId: 123456 + pick * 11, Priority: "high", Context: "Support Tier 2")
                { Action = ActionNone, CanTake = true, EventTime = DateTimeOffset.UtcNow.AddMinutes(-minutes), Client = client, MuteTarget = SampleClient(client),
                  SlaDue = DateTimeOffset.UtcNow.AddMinutes(60 - minutes), SlaFirstReply = true }];
        }
        if (kind == NotificationKind.HelpColleague)
        {
            var (title, customer, text, client, colleague) = Waiting[pick % Waiting.Length];
            return [new NotificationContent(kind, "Colleague's ticket", $"#{123459 + pick * 11} {title}",
                $"**{customer}**\n{colleague} is off work · {Clock.DayTime(DateTime.Today.AddHours(21).AddMinutes(40))} their time · last active 4 h ago\n“{text}”",
                TicketId: 123459 + pick * 11, Priority: "urgent", Context: "Support Tier 2")
                { Action = ActionNone, Client = $"{client} · Premium Support", MuteTarget = SampleClient(client), SlaDue = DateTimeOffset.UtcNow.AddMinutes(-25),
                  EventTime = DateTimeOffset.UtcNow.AddMinutes(-85) }];
        }
        var (updateTitle, who, said, updateClient) = Updates[pick % Updates.Length];
        return [new NotificationContent(kind, "Ticket updated", $"#{123457 + pick * 11} {updateTitle}", $"**{who}**\n“{said}”",
            TicketId: 123457 + pick * 11, Priority: "normal") { Action = ActionNone, Client = updateClient, MuteTarget = SampleClient(updateClient), SlaDue = DateTimeOffset.UtcNow.AddMinutes(190),
            EventTime = DateTimeOffset.UtcNow.AddMinutes(-4) }];
    }

    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTime> _recent = [];

    /// <summary>True if the same notification was already shown within the last minute (then it's skipped).</summary>
    private bool IsDuplicate(NotificationContent content)
    {
        if (content.ResolvedAction is ActionNone or ActionInfo)
            return false; // test samples: you may want to see the same one again right away (e.g. in another theme)
        if (content.RequestedByUser)
            return false; // asked for (Catch up): always answered; the same one still on screen is replaced

        var now = DateTime.UtcNow;
        foreach (var old in _recent.Where(kv => now - kv.Value > DuplicateWindow).Select(kv => kv.Key).ToList())
            _recent.Remove(old);

        var key = string.Join("|", content.Kind, content.ResolvedAction, content.TicketId, content.Title, content.Body);
        if (_recent.ContainsKey(key))
            return true;
        _recent[key] = now;
        return false;
    }

    private void Show(NotificationContent content, Settings settings)
    {
        if (IsDuplicate(content))
        {
            Log.Info($"Skipped a duplicate notification: {content.LogName}");
            return;
        }
        if (content.RequestedByUser)
            _popups.CloseSame(content); // Catch up again: the new one takes the old one's place

        try
        {
            // Counted as shown once it's really on screen: a notification that waits in line and is dropped
            // (no longer needed) never counts. The popup manager calls OnShown when it appears. While you present or
            // use a full-screen app it's held, and shown when that's over.
            _popups.Show(content, settings);
        }
        catch (Exception ex)
        {
            Log.Error("Could not show notification", ex);
        }
    }
}
