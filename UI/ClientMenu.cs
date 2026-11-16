using System.Globalization;

namespace ZendeskWatcher.UI;

/// <summary>
/// The choices for a client, as a menu: on a notification ("Options ▾") and on the Clients page (click a client).
/// Normal, Important, Only urgent, Mute until my shift ends / until my next shift / for 1 week / for good, and on the
/// Clients page its own sound.
/// </summary>
internal static class ClientMenu
{
    /// <summary>Your working hours and days as saved (Notifications → Working hours), for "until my shift ends / next shift".</summary>
    public static Func<(int Start, int End, IReadOnlyCollection<DayOfWeek> Days)>? MyHours { get; set; }

    private static DateTimeOffset LocalToUtc(DateTime local) => new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();

    /// <param name="current">The client with its current choice; the chosen one comes back as a copy.</param>
    /// <param name="sounds">Sound names to offer (Clients page); null for none (notifications).</param>
    public static ContextMenuStrip Build(ClientChoice current, Action<ClientChoice> chosen, IReadOnlyList<(ToastSound Sound, string Name)>? sounds = null)
    {
        var now = DateTimeOffset.UtcNow;
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };

        ToolStripMenuItem Item(string text, bool isChecked, Func<ClientChoice, ClientChoice> change)
        {
            var item = new ToolStripMenuItem(text) { Checked = isChecked };
            item.Click += (_, _) => chosen(change(current.Copy()));
            menu.Items.Add(item);
            return item;
        }

        // Whose notifications these choices are about, so "Mute" clearly means this client.
        var headerFont = new Font(menu.Font, FontStyle.Bold);
        menu.Disposed += (_, _) => headerFont.Dispose(); // an item's font isn't disposed with it
        menu.Items.Add(new ToolStripMenuItem($"Notifications from {current.Name.Replace("&", "&&")}") { Enabled = false, Font = headerFont });
        menu.Items.Add(new ToolStripSeparator());

        // The client's own choice (kept while muted); picking one also unmutes.
        var own = current.Mode == ClientMode.Muted ? ClientMode.Normal : current.Mode;
        Item("Normal", own == ClientMode.Normal, c => WithMode(c, ClientMode.Normal));
        Item("Important ★  – always, stays until closed", own == ClientMode.Important, c => WithMode(c, ClientMode.Important));
        Item("Only urgent  – Urgent (P1) and High (P2)", own == ClientMode.OnlyUrgent, c => WithMode(c, ClientMode.OnlyUrgent));
        menu.Items.Add(new ToolStripSeparator());
        var muted = current.IsMutedAt(now);
        var mutedFor = muted && current.MutedUntil is { } until ? until : (DateTimeOffset?)null;
        if (muted)
            Item("Unmute", false, c => WithMute(c, false, null));
        if (mutedFor is { } u)
            Item($"Muted until {Clock.DayTime(u.ToLocalTime().DateTime)}", true, c => c);
        // Until your shift ends (only while you're in it) or your next one starts (Notifications → Working hours).
        var (start, end, days) = MyHours?.Invoke() ?? (8, 17, Tracking.TicketRules.Weekdays);
        var local = DateTime.Now;
        if (Tracking.TicketRules.ShiftEnd(local, start, end, days) is { } shiftEnd)
            Item($"Mute until my shift ends · {Clock.Time(shiftEnd)}", false, c => WithMute(c, true, LocalToUtc(shiftEnd)));
        else // outside your hours there's no shift to end: shown greyed, so it's known to exist
            Item("Mute until my shift ends · during your working hours", false, c => c).Enabled = false;
        var nextShift = Tracking.TicketRules.NextShiftStart(local, start, days);
        Item($"Mute until my next shift · {Clock.DayTime(nextShift)}", false, c => WithMute(c, true, LocalToUtc(nextShift)));
        Item("Mute for 1 week", false, c => WithMute(c, true, now.AddDays(7)));
        Item("Mute", muted && mutedFor is null, c => WithMute(c, true, null));

        if (sounds is not null)
        {
            menu.Items.Add(new ToolStripSeparator());
            var sound = new ToolStripMenuItem(current.Sound is { } s && sounds.FirstOrDefault(x => x.Sound == s).Name is { } name ? $"Sound: {name}" : "Sound: by notification type");
            void AddSound(string text, ToastSound? value)
            {
                var item = new ToolStripMenuItem(text) { Checked = current.Sound == value };
                item.Click += (_, _) =>
                {
                    var copy = current.Copy();
                    copy.Sound = value;
                    chosen(copy);
                    if (value is { } v && ChipSounds.IsChip(v))
                        Popups.PopupManager.PlaySound(v); // a preview
                };
                sound.DropDownItems.Add(item);
            }
            AddSound("By notification type (Look && sound)", null); // && shows "&" in a menu (one & marks a shortcut key)
            foreach (var (value, text) in sounds)
                AddSound(text, value);
            menu.Items.Add(sound);
        }

        var palette = Theme.Current;
        ThemedMenu.Apply(menu, palette);
        foreach (var sub in menu.Items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
            ThemedMenu.Apply(sub.DropDown, palette);
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose); // after the click is handled
        return menu;
    }

    private static ClientChoice WithMode(ClientChoice c, ClientMode mode)
    {
        c.Mode = mode;
        return WithMute(c, false, null);
    }

    private static ClientChoice WithMute(ClientChoice c, bool muted, DateTimeOffset? until)
    {
        c.IsMuted = muted;
        c.MutedUntil = muted ? until : null;
        return c;
    }
}
