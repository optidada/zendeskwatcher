using System.Media;
using System.Runtime.InteropServices;

namespace ZendeskWatcher.UI.Popups;

/// <summary>Shows custom popups stacked in a screen corner, queues the overflow and re-shows snoozed ones.</summary>
public sealed class PopupManager
{
    private const int MaxVisible = 4;

    private readonly List<PopupForm> _visible = [];
    private readonly Queue<(NotificationContent Content, Settings Settings)> _pending = new();
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
    }

    public void Show(NotificationContent content, Settings settings)
    {
        if (_visible.Count >= MaxVisible)
        {
            _pending.Enqueue((content, settings));
            return;
        }

        var style = content.Style ?? settings.StyleFor(content.Kind);
        var palette = PopupPalette.For(settings.ActiveNotificationTheme);
        var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var scale = DpiScale();

        var popup = new PopupForm(content, palette, style.Display, scale);
        popup.Finished += p => OnFinished(p, settings);
        popup.CopyClicked += p => _onCopy(p.Content);
        popup.PauseWhenAway = settings.PausePopupsWhenAway;
        _takeTurns = settings.PopupsTakeTurns;
        _visible.Add(popup);
        UpdateTurns();

        var restX = screen.WorkingArea.Right - popup.Width - (int)(16 * scale);
        Layout(settings.Position, scale);
        popup.Present(restX);

        PlaySound(style.Sound);
    }

    private void OnFinished(PopupForm popup, Settings settings)
    {
        _visible.Remove(popup);
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
            case PopupForm.Result.Snooze:
                _onSnooze(popup.Content);
                break;
        }

        Layout(settings.Position, DpiScale());

        if (_pending.Count > 0)
        {
            var (content, pendingSettings) = _pending.Dequeue();
            Show(content, pendingSettings);
        }
    }

    /// <summary>
    /// One countdown at a time: the oldest popup that times out counts down; the newer ones wait (with a full bar) and
    /// get their full time when it's their turn. So several at once don't all disappear together, unread.
    /// </summary>
    private void UpdateTurns()
    {
        var current = _visible.FirstOrDefault(p => p.TimesOut);
        foreach (var popup in _visible)
            popup.MyTurn = !_takeTurns || popup == current || !popup.TimesOut;
    }

    /// <summary>Newest popup nearest the corner; older ones are pushed away from it.</summary>
    private void Layout(PopupPosition position, float scale)
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        var margin = (int)(16 * scale);
        var gap = (int)(10 * scale);

        if (position == PopupPosition.TopRight)
        {
            var y = area.Top + margin;
            for (var i = _visible.Count - 1; i >= 0; i--)
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
