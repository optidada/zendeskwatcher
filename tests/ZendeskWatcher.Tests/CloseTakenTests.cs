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
