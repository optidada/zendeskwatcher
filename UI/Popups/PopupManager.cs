using System.Media;
using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>Shows custom popups stacked in a screen corner, queues the overflow and re-shows snoozed ones.</summary>
public sealed class PopupManager
{
    private const int MaxVisible = 4;

    private readonly List<PopupForm> _visible = [];
    private readonly List<Waiting> _pending = [];
    private readonly Dictionary<PopupForm, DateTime> _shownAt = [];
    private readonly System.Windows.Forms.Timer _awayWatch = new() { Interval = 1000 };
    private bool _wasAway;
    private bool _wasBusy;
    private bool _draining;
    private Settings _lastSettings = new();

    // The pill, the ticker and achievements (Look & sound -> How they appear): see PillHost, TickerBar, AchievementStyle.
    private PillHost? _pill;
    private TickerBar? _ticker;
    private PopupStacking _mode;

    private sealed record Waiting(NotificationContent Content, Settings Settings, DateTime Since);

    /// <summary>A notification really appeared (not when it only joins the line; dropped ones never do).</summary>
    public Action<NotificationContent>? Shown { get; set; }

    /// <summary>A choice from "Mute ▾" on a notification's client line.</summary>
    public Action<NotificationContent, ClientChoice>? ClientChosen { get; set; }

    /// <summary>Which of these notifications are still needed (bool: also read the ticket's changes). Set by the notifier.</summary>
    public Func<IReadOnlyList<NotificationContent>, bool, Task<List<NotificationContent>>>? StillNeeded { get; set; }
    private readonly Action<string, long?> _onAction;
    private readonly Action<NotificationContent> _onSnooze;
    private readonly Action<NotificationContent, PopupForm.Result> _onClosed;
    private readonly Action<NotificationContent> _onCopy;
    private static int _watchingLock;

    /// <param name="onSnooze">The user snoozed a popup; the caller decides when (and whether) it shows again.</param>
    /// <param name="onClosed">A popup closed, and how (for the statistics).</param>
    /// <param name="onCopy">Copy was clicked on a ticket notification.</param>
    public PopupManager(Action<string, long?> onAction, Action<NotificationContent> onSnooze,
        Action<NotificationContent, PopupForm.Result> onClosed, Action<NotificationContent> onCopy)
    {
        if (Interlocked.Exchange(ref _watchingLock, 1) == 0)
            PopupForm.WatchSessionLock();
        _onAction = onAction;
        _onSnooze = onSnooze;
        _onClosed = onClosed;
        _onCopy = onCopy;
        _awayWatch.Tick += (_, _) => WatchAway();
    }

    public void Show(NotificationContent content, Settings settings) => Show(content, settings, overLimit: false);

    /// <summary>Closes (uncounted) notifications that say the same as <paramref name="content"/>, on screen or waiting.</summary>
    public void CloseSame(NotificationContent content)
    {
        bool Same(NotificationContent other) =>
            other.Kind == content.Kind && other.TicketId == content.TicketId && other.Title == content.Title && other.Body == content.Body;
        _pending.RemoveAll(w => Same(w.Content));
        foreach (var popup in _visible.Where(p => Same(p.Content)).ToList())
            popup.Dismiss(PopupForm.Result.None);
        _ticker?.RemoveWhere(Same);
        SyncPill();
    }

    /// <param name="overLimit">Show it even when the stack is full (the "while you were away" summary).</param>
    private void Show(NotificationContent content, Settings settings, bool overLimit)
    {
        _lastSettings = settings;
        SwitchMode(settings.Stacking);
        // Presenting or full screen: wait in line, quietly, until it's over (WatchAway notices and shows them).
        // Achievements come one at a time, even the "while you were away" summary (it's next in line then).
        var full = !HasRoom(settings) && !(overLimit && settings.Stacking != PopupStacking.Achievement);
        if (full || !CanShowPopups())
        {
            _pending.Insert(overLimit ? 0 : _pending.Count, new Waiting(content, settings, DateTime.UtcNow));
            _awayWatch.Start();
            SyncPill();
            return;
        }
        if (settings.Stacking == PopupStacking.Ticker)
        {
            ShowInTicker(content, settings);
            return;
        }

        var palette = PopupPalette.For(settings.ActiveNotificationTheme);
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var size = Math.Clamp(settings.NotificationSize, 100, 200) / 100f;
        var scale = DpiScale() * size; // margins and gaps grow with the notifications

        var popup = new PopupForm(content, palette, settings.DisplaySecondsFor(content), DpiScale(), size,
            settings.Stacking == PopupStacking.Achievement ? AchievementStyle.Decoration : null);
        popup.StartHidden = settings.Stacking == PopupStacking.Pill && _pill is not { Expanded: true }; // the pill shows it
        popup.Finished += p => OnFinished(p, settings);
        popup.CopyClicked += p => _onCopy(p.Content);
        popup.ClientChosen += (p, choice) => ClientChosen?.Invoke(p.Content, choice);
        popup.PauseWhenAway = settings.PausePopupsWhenAway;
        _visible.Add(popup);
        _shownAt[popup] = DateTime.UtcNow;
        _awayWatch.Start();
        UpdateTurns();

        var area = screen.WorkingArea;
        var margin = (int)(16 * scale);
        var position = EffectivePosition(settings);
        _peek = PeekEdgeFor(settings);
        popup.Peek = _peek;
        if (_peek != PopupForm.PeekEdge.None)
            margin = 0; // the tab sits right at the screen edge
        var (restX, entry) = position switch
        {
            PopupPosition.LeftCenter => (area.Left + margin, PopupForm.Entry.FromLeft),
            PopupPosition.TopCenter => (area.Left + (area.Width - popup.Width) / 2, PopupForm.Entry.FromTop),
            PopupPosition.BottomCenter => (area.Left + (area.Width - popup.Width) / 2, PopupForm.Entry.FromBottom),
            PopupPosition.Center => (area.Left + (area.Width - popup.Width) / 2, PopupForm.Entry.Fade),
            _ => (area.Right - popup.Width - margin, PopupForm.Entry.FromRight)
        };
        if (settings.Stacking == PopupStacking.Achievement)
            entry = PopupForm.Entry.Bounce; // rises from the bottom edge, game style
        else if (settings.Stacking == PopupStacking.Pill)
            entry = PopupForm.Entry.Fade;
        _deck = settings.Stacking == PopupStacking.Deck;
        Layout(position, scale);
        popup.Present(_peek is PopupForm.PeekEdge.Top or PopupForm.PeekEdge.Bottom ? popup.RestX : restX, entry); // top/bottom tabs sit side by side
        if (_deck)
            Layout(position, scale); // now that it has a place: the fan, newest card on top
        if (_mode == PopupStacking.Pill)
            OnPillCard(popup);
        Announce(content, settings);
    }

    /// <summary>It's there (a card, or an item in the ticker): counted as shown, its sound, and the edge glow.</summary>
    private void Announce(NotificationContent content, Settings settings)
    {
        Shown?.Invoke(content);

        var sound = settings.SoundFor(content);
        if (settings.Stacking == PopupStacking.Achievement && sound != ToastSound.Silent)
            ChipSounds.PlayAchievement(); // the game-style tune instead of its own sound, not both
        else
            PlaySound(sound);
        EdgeGlow.Consider(content, settings); // Urgent, SLA breach or important client, when on (Look & sound)
    }

    private void OnFinished(PopupForm popup, Settings settings)
    {
        _visible.Remove(popup);
        _shownAt.Remove(popup);
        UpdateTurns();
        _onClosed(popup.Content, popup.Outcome);
        Act(popup.Content, popup.Outcome, popup.ClickedLink);

        Layout(EffectivePosition(settings), DpiScale() * Math.Clamp(settings.NotificationSize, 100, 200) / 100f);
        SyncPill();
        _ = DrainAsync();
    }

    /// <summary>What a closed notification asks for: open (or a link in it), take it, or snooze.</summary>
    private void Act(NotificationContent content, PopupForm.Result outcome, RichText.Link? clickedLink)
    {
        switch (outcome)
        {
            case PopupForm.Result.Open when clickedLink is { } link:
                _onAction(link.Action, link.TicketId); // a ticket number or a Settings page in the text
                break;
            case PopupForm.Result.Open:
                _onAction(content.ResolvedAction, content.TicketId);
                break;
            case PopupForm.Result.Take:
                _onAction(Notifier.ActionTake, content.TicketId);
                break;
            case PopupForm.Result.Snooze:
                _onSnooze(content);
                break;
        }
    }

    // ------------------------------------------------------------------ pill, ticker, achievement

    /// <summary>Whether another one fits on screen now in this way of appearing (achievements: one at a time).</summary>
    private bool HasRoom(Settings settings) => settings.Stacking switch
    {
        PopupStacking.Ticker => (_ticker?.Count ?? 0) < TickerBar.MaxItems,
        PopupStacking.Achievement => _visible.Count == 0,
        _ => _visible.Count < MaxVisible
    };

    /// <summary>Another way of appearing was chosen: cards in the pill show as they are (the next layout places them).</summary>
    private void SwitchMode(PopupStacking mode)
    {
        if (mode == _mode)
            return;
        if (_mode == PopupStacking.Pill)
            _pill?.Release();
        _mode = mode;
    }

    /// <summary>A new card in the pill: the pill's text updates; urgent ones and app messages open it for a while.</summary>
    private void OnPillCard(PopupForm popup)
    {
        if (_pill is null)
        {
            _pill = new PillHost(() => _visible);
            _pill.ExpandedChanged += UpdateTurns; // seen ones count down
        }
        SyncPill();
        if (PopupForm.ComesOutByItself(popup.Content))
            _pill.OpenFor(TimeSpan.FromSeconds(6));
    }

    /// <summary>The pill's summary of what's on screen and waiting; it hides when nothing is left.</summary>
    private void SyncPill()
    {
        if (_pill is null || _mode != PopupStacking.Pill)
            return;
        var all = _visible.Select(p => p.Content).Concat(_pending.Select(w => w.Content)).ToList();
        _pill.Sync(all, PopupPalette.For(_lastSettings.ActiveNotificationTheme), DpiScale() * Math.Clamp(_lastSettings.NotificationSize, 100, 200) / 100f);
    }

    /// <summary>The ticker: one more item scrolling along the screen edge.</summary>
    private void ShowInTicker(NotificationContent content, Settings settings)
    {
        if (_ticker is null)
        {
            _ticker = new TickerBar();
            _ticker.Finished += (item, outcome) =>
            {
                _onClosed(item, outcome);
                Act(item, outcome, null);
                _ = DrainAsync();
            };
            _ticker.CopyClicked += item => _onCopy(item);
        }
        _ticker.PauseWhenAway = settings.PausePopupsWhenAway;
        _ticker.Add(content, settings.DisplaySecondsFor(content), PopupPalette.For(settings.ActiveNotificationTheme), settings.ActiveNotificationTheme,
            DpiScale(), Math.Clamp(settings.NotificationSize, 100, 200) / 100f, TickerBar.AtTop(settings));
        _awayWatch.Start();
        Announce(content, settings);
    }

    // ------------------------------------------------------------------ waiting notifications

    /// <summary>Shows waiting notifications while there's room; ones that waited long are checked first (see <see cref="Settings.RecheckWaitingAfter"/>).</summary>
    private async Task DrainAsync()
    {
        if (_draining)
            return;
        _draining = true;
        try
        {
            while (_pending.Count > 0 && HasRoom(_pending[0].Settings) && CanShowPopups())
            {
                var next = _pending[0];
                _pending.RemoveAt(0);
                if (WaitedLong(next.Content, next.Settings, next.Since) && (await CheckAsync([next.Content], deep: true)).Count == 0)
                    continue;
                Show(next.Content, next.Settings);
            }
        }
        finally
        {
            _draining = false;
            SyncPill(); // some were dropped as no longer needed
        }
    }

    private static bool WaitedLong(NotificationContent content, Settings settings, DateTime since) =>
        content.IsTicketNotification && content.TicketId is not null && DateTime.UtcNow - since >= Settings.RecheckWaitingAfter;

    /// <summary>The still needed ones; everything if there's no check or it fails.</summary>
    private async Task<List<NotificationContent>> CheckAsync(IReadOnlyList<NotificationContent> contents, bool deep)
    {
        if (StillNeeded is not { } check || contents.Count == 0)
            return contents.ToList();
        try
        {
            return await check(contents, deep);
        }
        catch (Exception ex)
        {
            Log.Error("Could not check waiting notifications; showing them anyway", ex);
            return contents.ToList();
        }
    }

    /// <summary>Once a second while notifications are on screen or waiting: notices when the user comes back.</summary>
    private void WatchAway()
    {
        // Presenting or full screen counts as away: the ones on screen hide (and wait), new ones wait in line, and
        // afterwards it's like coming back (a summary instead of many, outdated ones dropped).
        var busy = !CanShowPopups();
        if (busy != _wasBusy)
        {
            _wasBusy = busy;
            foreach (var popup in _visible.Where(_ => _mode != PopupStacking.Pill)) // the pill hides its own cards
                popup.Held = busy;
            if (_pill is not null)
                _pill.Held = busy;
            if (_ticker is not null)
                _ticker.Held = busy;
            UpdateTurns();
        }
        var away = PopupForm.UserAway() || busy;
        if (_wasAway && !away)
        {
            EdgeGlow.UserBack(c => _visible.Any(p => ReferenceEquals(p.Content, c))); // the glow that waited
            _ = CameBackThenDrainAsync(_lastSettings);
        }
        _wasAway = away;
        if (_visible.Count == 0 && _pending.Count == 0 && (_ticker?.Count ?? 0) == 0)
        {
            _awayWatch.Stop();
            _wasAway = false;
        }
    }

    private async Task CameBackThenDrainAsync(Settings settings)
    {
        await CameBackAsync(settings);
        await DrainAsync(); // held while presenting: their turn now
    }

    /// <summary>
    /// The user is back: outdated notifications on screen close quietly, and more than
    /// <see cref="Settings.SummarizeWaitingAbove"/> waiting ones become one "while you were away" summary.
    /// </summary>
    private async Task CameBackAsync(Settings settings)
    {
        var tickets = _pending.Where(w => w.Content.IsTicketNotification).ToList();
        if (tickets.Count > Settings.SummarizeWaitingAbove)
        {
            foreach (var w in tickets)
                _pending.Remove(w);
            var contents = tickets.Select(w => w.Content).ToList();
            var kept = await CheckAsync(contents, deep: false);
            if (kept.Count > Settings.SummarizeWaitingAbove)
            {
                Log.Info($"Back after being away: {contents.Count} waiting notifications, {kept.Count} still needed; showing a summary");
                Show(Notifier.AwaySummary(kept, contents.Count - kept.Count) with { Style = settings.SummaryStyle }, settings, overLimit: true);
            }
            else
            {
                var keep = kept.ToHashSet(ReferenceEqualityComparer.Instance);
                _pending.InsertRange(0, tickets.Where(w => keep.Contains(w.Content)));
                _ = DrainAsync();
            }
        }

        if (_ticker is { Count: > 0 } ticker)
        {
            var oldItems = ticker.Items.Where(i => WaitedLong(i.Content, settings, i.ShownAt)).Select(i => i.Content).ToList();
            var keepItems = (await CheckAsync(oldItems, deep: true)).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var item in oldItems.Where(c => !keepItems.Contains(c)))
            {
                Log.Info($"No longer needed, closed: {item.Title}");
                ticker.Remove(item, PopupForm.Result.None);
            }
        }

        var old = _visible.Where(p => _shownAt.TryGetValue(p, out var at) && WaitedLong(p.Content, settings, at)).ToList();
        if (old.Count == 0)
            return;
        var stillNeeded = (await CheckAsync(old.Select(p => p.Content).ToList(), deep: true)).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var popup in old.Where(p => !stillNeeded.Contains(p.Content) && _visible.Contains(p)))
        {
            Log.Info($"No longer needed, closed: {popup.Content.Title}");
            popup.Dismiss(PopupForm.Result.None);
        }
    }

    /// <summary>
    /// One countdown at a time: the oldest popup that times out counts down; the newer ones wait (with a full bar) and
    /// get their full time when it's their turn. So several at once don't all disappear together, unread.
    /// </summary>
    private void UpdateTurns()
    {
        // In a deck only the front card (the newest) can be read: only it counts down. One behind it never does, so
        // nothing disappears unseen; a front card that stays until closed holds the others until then.
        var current = _deck ? (_visible.LastOrDefault() is { TimesOut: true } front ? front : null)
            : _visible.FirstOrDefault(p => p.TimesOut && (_mode != PopupStacking.Pill || _pill?.Seen(p) == true)); // pill: once opened
        // Peek: each one counts down only after it was slid out (and read), so none waits for a tab nobody opened.
        var pillHidden = _mode == PopupStacking.Pill && _wasBusy; // presenting: the pill and its cards wait
        foreach (var popup in _visible)
            popup.MyTurn = (_peek != PopupForm.PeekEdge.None || popup == current || !popup.TimesOut) && !pillHidden;
    }

    /// <summary>
    /// Oldest at the top, in both corners: people read top to bottom, and the oldest counts down first (take turns).
    /// Bottom right: the newest is nearest the corner and pushes the older ones up. Top right: the newest is added below.
    /// </summary>
    private void Layout(PopupPosition position, float scale)
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        var margin = (int)(16 * scale);
        var gap = (int)(10 * scale);

        // A deck: every card as tall as the tallest, so their edges line up. Otherwise each its own height.
        var tallest = _deck && _visible.Count > 0 ? _visible.Max(p => p.NaturalHeight) : 0;
        foreach (var popup in _visible)
            popup.StretchTo(tallest);

        if (_deck)
        {
            LayoutDeck(position, area, margin, scale);
            return;
        }

        if (_peek is PopupForm.PeekEdge.Top or PopupForm.PeekEdge.Bottom)
        {
            // Tabs along the top or bottom edge, side by side and centered, oldest on the left. When they don't fit (large
            // notifications on a small screen) they overlap evenly; the one under the mouse comes to the front.
            var width = _visible.Sum(p => p.Width) + gap * Math.Max(0, _visible.Count - 1);
            var x = area.Left + Math.Max(0, (area.Width - width) / 2);
            var squeeze = width > area.Width && _visible.Count > 1
                ? (float)(width - area.Width) / (_visible.Count - 1)
                : 0f;
            foreach (var popup in _visible)
            {
                popup.RestX = x;
                popup.TargetY = _peek == PopupForm.PeekEdge.Top ? area.Top : area.Bottom - popup.Height;
                x += popup.Width + gap - (int)Math.Ceiling(squeeze);
            }
            return;
        }
        if (_peek != PopupForm.PeekEdge.None)
            margin = gap; // tabs at the side edge: only a small gap from the top or bottom

        if (position is PopupPosition.LeftCenter or PopupPosition.RightCenter or PopupPosition.Center)
        {
            // Centered vertically as a group, oldest at the top.
            var total = _visible.Sum(p => p.Height) + gap * Math.Max(0, _visible.Count - 1);
            var y = area.Top + Math.Max(margin, (area.Height - total) / 2);
            foreach (var popup in _visible)
            {
                popup.TargetY = y;
                y += popup.Height + gap;
            }
        }
        else if (position is PopupPosition.TopRight or PopupPosition.TopCenter)
        {
            var y = _mode == PopupStacking.Pill ? PillHost.CardsTop(area, scale) : area.Top + margin; // the pill opens below itself
            for (var i = 0; i < _visible.Count; i++)
            {
                _visible[i].TargetY = y;
                y += _visible[i].Height + gap;
            }
        }
        else
        {
            var y = area.Bottom - margin;
            for (var i = _visible.Count - 1; i >= 0; i--)
            {
                y -= _visible[i].Height;
                _visible[i].TargetY = y;
                y -= gap;
            }
        }
    }

    /// <summary>Stacked like cards (Look & sound): set from the settings when a notification is shown.</summary>
    private bool _deck;

    /// <summary>Peek (Look & sound): the screen edge the notifications wait at, or None.</summary>
    private PopupForm.PeekEdge _peek;

    /// <summary>
    /// Peek needs a screen edge: with Center it uses Top center. The pill is always at the top center and achievements
    /// at the bottom center (Settings says so too).
    /// </summary>
    public static PopupPosition EffectivePosition(Settings settings) => settings.Stacking switch
    {
        PopupStacking.Pill => PopupPosition.TopCenter,
        PopupStacking.Achievement => PopupPosition.BottomCenter,
        PopupStacking.Peek when settings.Position == PopupPosition.Center => PopupPosition.TopCenter,
        _ => settings.Position
    };

    /// <summary>The edge the notifications peek out from: right for the right corners, left, or top for Top center.</summary>
    public static PopupForm.PeekEdge PeekEdgeFor(Settings settings) =>
        settings.Stacking != PopupStacking.Peek ? PopupForm.PeekEdge.None
        : EffectivePosition(settings) switch
        {
            PopupPosition.LeftCenter => PopupForm.PeekEdge.Left,
            PopupPosition.TopCenter => PopupForm.PeekEdge.Top,
            PopupPosition.BottomCenter => PopupForm.PeekEdge.Bottom,
            _ => PopupForm.PeekEdge.Right
        };

    /// <summary>
    /// A deck: the newest card in front, each older one behind it shifted up and to the right by the same amount, so
    /// every card behind shows an equal strip along its top and right edge. The fan is kept on screen: at the right
    /// edge the front card moves left to make room, at the top it moves down.
    /// </summary>
    private void LayoutDeck(PopupPosition position, Rectangle area, int margin, float scale)
    {
        if (_visible.Count == 0)
            return;
        var peek = (int)(12 * scale);
        var front = _visible[^1];
        var spread = (_visible.Count - 1) * peek; // how far the last card behind sticks out
        var frontX = position switch
        {
            PopupPosition.LeftCenter => area.Left + margin,
            PopupPosition.TopCenter or PopupPosition.BottomCenter or PopupPosition.Center => area.Left + (area.Width - front.Width - spread) / 2,
            _ => area.Right - margin - front.Width - spread
        };
        var frontTop = position switch
        {
            PopupPosition.TopRight or PopupPosition.TopCenter => area.Top + margin + spread,
            PopupPosition.LeftCenter or PopupPosition.RightCenter or PopupPosition.Center => area.Top + (area.Height - front.Height + spread) / 2,
            _ => area.Bottom - margin - front.Height
        };
        for (var i = 0; i < _visible.Count; i++)
        {
            var depth = _visible.Count - 1 - i; // 0 = front
            _visible[i].RestX = frontX + depth * peek;
            _visible[i].TargetY = frontTop - depth * peek; // tops line up step by step, whatever the card's height
        }
        // Back to front, so the newest ends up on top.
        foreach (var popup in _visible)
            popup.RaiseWithoutActivating();
    }

    /// <summary>
    /// False while the user is presenting or something runs full screen: popups wait (and make no sound) until it's
    /// over, so nothing appears over a screen share.
    /// </summary>
    public static bool CanShowPopups()
    {
        const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4, QUNS_APP = 7;
        try
        {
            return SHQueryUserNotificationState(out var state) != 0 ||
                   state is not (QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE or QUNS_APP);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>Plays a notification sound (also used by the ▶ preview buttons in Settings).</summary>
    public static void PlaySound(ToastSound sound)
    {
        if (sound == ToastSound.Silent)
            return;
        if (ChipSounds.IsChip(sound))
        {
            ChipSounds.Play(sound);
            return;
        }

        var file = sound switch
        {
            ToastSound.Mail => "Windows Notify Email.wav",
            ToastSound.InstantMessage => "Windows Notify Messaging.wav",
            ToastSound.Reminder => "Windows Notify Calendar.wav",
            ToastSound.Sms => "Windows Message Nudge.wav",
            _ => "Windows Notify System Generic.wav"
        };

        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", file);
            if (File.Exists(path))
                new SoundPlayer(path).Play();
            else
                SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            Log.Error("Could not play notification sound", ex);
        }
    }

    private static float DpiScale()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
