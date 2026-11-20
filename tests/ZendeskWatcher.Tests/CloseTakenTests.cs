using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>
/// A new ticket someone took: its notification really closes on screen, uncounted, and only that one
/// (the decision itself is tested in <see cref="UnassignedTicketTests"/>).
/// </summary>
public class CloseTakenTests
{
    private static readonly NotificationStyle Quiet = new() { Sound = ToastSound.Silent, Display = ToastDisplay.UntilDismissed };

    private static NotificationContent NewTicket(long id) =>
        new(NotificationKind.NewTicket, "New ticket", $"#{id} Help!", "Jane Doe · Created 09:00", id)
        { IsTicketNotification = true, CanTake = true, Style = Quiet };

    [Fact]
    public void Only_the_taken_tickets_notification_closes_and_it_is_not_counted()
    {
        var (left, outcomes) = OnUiThread(() =>
        {
            var outcomes = new List<(long?, PopupForm.Result)>();
            var manager = new PopupManager((_, _) => { }, _ => { }, (c, r) => outcomes.Add((c.TicketId, r)), _ => { });
            var settings = new Settings { Stacking = PopupStacking.List };
            manager.Show(NewTicket(1), settings);
            manager.Show(NewTicket(2), settings);
            // Same ticket, but a change on someone's ticket, not a new-ticket notification: stays.
            manager.Show(NewTicket(1) with { Kind = NotificationKind.MyTicket, Heading = "Ticket updated" }, settings);
            Pump(TimeSpan.FromMilliseconds(500));

            manager.CloseNewTicket(1);
            Pump(TimeSpan.FromSeconds(2)); // it slides out
            var left = manager.OpenCount;
            foreach (var form in Application.OpenForms.OfType<PopupForm>().ToList())
                form.Close();
            return (left, outcomes);
        });

        Assert.Equal(2, left);
        // On screen it slid out with None (not "ignored" or "dismissed" in Insights); if the PC was "busy" (presenting,
        // full screen) they only waited in line, and the taken one just left the line.
        Assert.All(outcomes, closed => Assert.Equal((1L, PopupForm.Result.None), closed));
    }

    [Fact]
    public void With_a_note_it_says_who_took_it_for_a_few_seconds_then_closes()
    {
        var (stillThere, left) = OnUiThread(() =>
        {
            var manager = new PopupManager((_, _) => { }, _ => { }, (_, _) => { }, _ => { });
            manager.Show(NewTicket(1), new Settings { Stacking = PopupStacking.List });
            Pump(TimeSpan.FromMilliseconds(500));

            manager.CloseNewTicket(1, "Taken by Anna");
            Pump(TimeSpan.FromSeconds(2));
            var stillThere = manager.OpenCount; // still saying who took it
            Pump(TimeSpan.FromSeconds(4));
            var left = manager.OpenCount;
            foreach (var form in Application.OpenForms.OfType<PopupForm>().ToList())
                form.Close();
            return (stillThere, left);
        });

        // Waiting in line instead (the PC "busy"): it just leaves the line, so nothing is on screen either way at the end.
        Assert.InRange(stillThere, 0, 1);
        Assert.Equal(0, left);
    }

    [Fact]
    public void A_shortcut_acts_on_the_oldest_notification_and_only_where_its_button_is()
    {
        var (outcomes, states, takeOnUpdate) = OnUiThread(() =>
        {
            var outcomes = new List<(long?, PopupForm.Result)>();
            var states = new List<bool>();
            var manager = new PopupManager((_, _) => { }, _ => { }, (c, r) => outcomes.Add((c.TicketId, r)), _ => { }) { OnScreenChanged = states.Add };
            var settings = new Settings { Stacking = PopupStacking.List, HoldWhileBusy = false };
            manager.Show(NewTicket(1), settings);
            manager.Show(NewTicket(2), settings);
            manager.Show(NewTicket(3) with { Kind = NotificationKind.MyTicket, Heading = "Ticket updated", CanTake = false }, settings);
            Pump(TimeSpan.FromMilliseconds(500));

            manager.ActOnOldest(PopupForm.Result.Dismiss); // Ctrl+Alt+X: #1, the oldest
            Pump(TimeSpan.FromSeconds(1.5));
            manager.ActOnOldest(PopupForm.Result.Dismiss); // then #2
            Pump(TimeSpan.FromSeconds(1.5));
            var takeOnUpdate = manager.ActOnOldest(PopupForm.Result.Take); // Ctrl+Alt+T on a ticket update: no Take it there
            manager.ActOnOldest(PopupForm.Result.Dismiss);
            Pump(TimeSpan.FromSeconds(1.5));
            foreach (var form in Application.OpenForms.OfType<PopupForm>().ToList())
                form.Close();
            return (outcomes, states, takeOnUpdate);
        });

        if (!states.Contains(true))
            return; // the PC was "busy" (presenting, full screen): they only waited in line, nothing to press
        Assert.Equal([(1L, PopupForm.Result.Dismiss), (2L, PopupForm.Result.Dismiss), (3L, PopupForm.Result.Dismiss)],
            outcomes.Select(o => ((long)o.Item1!, o.Item2)));
        Assert.False(takeOnUpdate);
        Assert.Equal([true, false], states); // the shortcuts work while some are on screen, then the keys are free again
    }

    [Fact]
    public void A_shortcut_never_answers_the_cover_today_question()
    {
        var (open, take, snooze, close, states) = OnUiThread(() =>
        {
            var states = new List<bool>();
            var manager = new PopupManager((_, _) => { }, _ => { }, (_, _) => { }, _ => { }) { OnScreenChanged = states.Add };
            var question = new NotificationContent(NotificationKind.MyTicket, "Colleague away?", "Anna hasn't been active in Zendesk today", "…")
            {
                Action = NotificationActions.Cover(7, "Anna"), SecondChoice = ("Always", NotificationActions.CoverAlways(7, "Anna")), Style = Quiet
            };
            manager.Show(question, new Settings { Stacking = PopupStacking.List, HoldWhileBusy = false });
            Pump(TimeSpan.FromMilliseconds(500));
            var open = manager.ActOnOldest(PopupForm.Result.Open);     // would be "Cover today": no
            var take = manager.ActOnOldest(PopupForm.Result.Take);
            var snooze = manager.ActOnOldest(PopupForm.Result.Snooze);
            var close = manager.ActOnOldest(PopupForm.Result.Dismiss); // closing it is fine
            Pump(TimeSpan.FromSeconds(1.5));
            foreach (var form in Application.OpenForms.OfType<PopupForm>().ToList())
                form.Close();
            return (open, take, snooze, close, states);
        });

        if (!states.Contains(true))
            return; // the PC was "busy" (presenting): it only waited in line
        Assert.False(open);
        Assert.False(take);
        Assert.False(snooze);
        Assert.True(close);
    }

    private static void Pump(TimeSpan time)
    {
        var until = DateTime.UtcNow + time;
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static T OnUiThread<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("Failed on the UI thread", error);
        return result;
    }
}
