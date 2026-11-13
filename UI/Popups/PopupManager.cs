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
    private bool _draining;
    private Settings _lastSettings = new();

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
    private bool _takeTurns = true;

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

    /// <param name="overLimit">Show it even when the stack is full (the "while you were away" summary).</param>
    private void Show(NotificationContent content, Settings settings, bool overLimit)
    {
        _lastSettings = settings;
        if (_visible.Count >= MaxVisible && !overLimit)
        {
            _pending.Add(new Waiting(content, settings, DateTime.UtcNow));
            _awayWatch.Start();
            return;
        }

        var palette = PopupPalette.For(settings.ActiveNotificationTheme);
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var size = Math.Clamp(settings.NotificationSize, 100, 200) / 100f;
        var scale = DpiScale() * size; // margins and gaps grow with the notifications

        var popup = new PopupForm(content, palette, settings.DisplaySecondsFor(content), DpiScale(), size);
        popup.Finished += p => OnFinished(p, settings);
        popup.CopyClicked += p => _onCopy(p.Content);
        popup.ClientChosen += (p, choice) => ClientChosen?.Invoke(p.Content, choice);
        popup.PauseWhenAway = settings.PausePopupsWhenAway;
        _takeTurns = settings.PopupsTakeTurns;
        _visible.Add(popup);
        _shownAt[popup] = DateTime.UtcNow;
        _awayWatch.Start();
        UpdateTurns();

        var area = screen.WorkingArea;
        var margin = (int)(16 * scale);
        var (restX, entry) = settings.Position switch
        {
            PopupPosition.LeftCenter => (area.Left + margin, PopupForm.Entry.FromLeft),
            PopupPosition.TopCenter => (area.Left + (area.Width - popup.Width) / 2, PopupForm.Entry.FromTop),
            PopupPosition.Center => (area.Left + (area.Width - popup.Width) / 2, PopupForm.Entry.Fade),
            _ => (area.Right - popup.Width - margin, PopupForm.Entry.FromRight)
        };
        _deck = settings.Stacking == PopupStacking.Deck;
        Layout(settings.Position, scale);
        popup.Present(restX, entry);
        if (_deck)
            Layout(settings.Position, scale); // now that it has a place: the fan, newest card on top
        Shown?.Invoke(content);

        PlaySound(settings.SoundFor(content));
    }

    private void OnFinished(PopupForm popup, Settings settings)
    {
        _visible.Remove(popup);
        _shownAt.Remove(popup);
        UpdateTurns();
        _onClosed(popup.Content, popup.Outcome);

        switch (popup.Outcome)
        {
            case PopupForm.Result.Open:
                _onAction(popup.Content.ResolvedAction, popup.Content.TicketId);
                break;
            case PopupForm.Result.Take:
                _onAction(Notifier.ActionTake, popup.Content.TicketId);
                break;
            case PopupForm.Result.CatchUp:
                _onAction(Notifier.ActionCatchUp, null);
                break;
            case PopupForm.Result.Snooze:
                _onSnooze(popup.Content);
                break;
        }

        Layout(settings.Position, DpiScale() * Math.Clamp(settings.NotificationSize, 100, 200) / 100f);
        _ = DrainAsync();
    }

    // ------------------------------------------------------------------ waiting notifications

    /// <summary>Shows waiting notifications while there's room; ones that waited long are checked first (see <see cref="Settings.RecheckWaitingAfterMinutes"/>).</summary>
    private async Task DrainAsync()
    {
        if (_draining)
            return;
        _draining = true;
        try
        {
            while (_visible.Count < MaxVisible && _pending.Count > 0)
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
        }
    }

    private static bool WaitedLong(NotificationContent content, Settings settings, DateTime since) =>
        settings.RecheckWaitingAfterMinutes > 0 && content.IsTicketNotification && content.TicketId is not null &&
        DateTime.UtcNow - since >= TimeSpan.FromMinutes(settings.RecheckWaitingAfterMinutes);

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
        var away = PopupForm.UserAway();
        if (_wasAway && !away)
            _ = CameBackAsync(_lastSettings);
        _wasAway = away;
        if (_visible.Count == 0 && _pending.Count == 0)
        {
            _awayWatch.Stop();
            _wasAway = false;
        }
    }

    /// <summary>
    /// The user is back: outdated notifications on screen close quietly, and more than
    /// <see cref="Settings.SummarizeWaitingAbove"/> waiting ones become one "while you were away" summary.
    /// </summary>
    private async Task CameBackAsync(Settings settings)
    {
        var tickets = _pending.Where(w => w.Content.IsTicketNotification).ToList();
        if (settings.SummarizeWaitingAbove > 0 && tickets.Count > settings.SummarizeWaitingAbove)
        {
            foreach (var w in tickets)
                _pending.Remove(w);
            var contents = tickets.Select(w => w.Content).ToList();
            var kept = settings.RecheckWaitingAfterMinutes > 0 ? await CheckAsync(contents, deep: false) : contents;
            if (kept.Count > settings.SummarizeWaitingAbove)
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
        // In a deck only the front card (the newest) can be read, so it counts down first.
        var current = _deck ? _visible.LastOrDefault(p => p.TimesOut) : _visible.FirstOrDefault(p => p.TimesOut);
        foreach (var popup in _visible)
            popup.MyTurn = !_takeTurns || popup == current || !popup.TimesOut;
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

        if (_deck)
        {
            LayoutDeck(position, area, margin, scale);
            return;
        }

        if (position is PopupPosition.LeftCenter or PopupPosition.Center)
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
            var y = area.Top + margin;
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
            PopupPosition.TopCenter or PopupPosition.Center => area.Left + (area.Width - front.Width - spread) / 2,
            _ => area.Right - margin - front.Width - spread
        };
        var frontTop = position switch
        {
            PopupPosition.TopRight or PopupPosition.TopCenter => area.Top + margin + spread,
            PopupPosition.LeftCenter or PopupPosition.Center => area.Top + (area.Height - front.Height + spread) / 2,
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
    /// False while the user is presenting or something runs full screen; Windows toasts are used then,
    /// so popups never appear over a screen share.
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
