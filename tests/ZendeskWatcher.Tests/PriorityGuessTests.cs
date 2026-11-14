using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>The experimental priority guess: only higher guesses, with readable reasons; calm and solved texts stay quiet.</summary>
public class PriorityGuessTests
{
    private static PriorityHint? Guess(string? priority, string subject, string text, string sla = Sla.StandardKey, double waitedHours = 0) =>
        PriorityGuess.Guess(priority, subject, [text], sla, TimeSpan.FromHours(waitedHours));

    [Fact]
    public void A_site_down_ticket_at_normal_looks_like_P1()
    {
        var hint = Guess("normal", "Help", "Our website is down!! Nothing loads since 10:00.");

        Assert.Equal("urgent", hint?.Priority);
        Assert.Equal("site down", hint!.Reasons[0]);
        Assert.Contains("frustrated tone", hint.Reasons);
        Assert.StartsWith("Looks like P1: site down", hint.Text);
    }

    [Fact]
    public void Checkout_failing_for_all_customers_is_P1()
    {
        Assert.Equal("urgent", Guess(null, "Checkout fails for all customers", "Orders are not going through.")?.Priority);
    }

    [Fact]
    public void A_launch_blocker_tomorrow_is_at_least_P2()
    {
        var hint = Guess("low", "Publishing issue", "Any update? This is blocking our launch tomorrow.");

        Assert.NotNull(hint);
        Assert.Contains("deadline soon", hint!.Reasons);
    }

    [Fact]
    public void An_ordinary_question_gets_no_hint()
    {
        Assert.Null(Guess("normal", "How do I add a block?", "Hi, how can we add a new block type to the start page? Thanks"));
        Assert.Null(Guess("normal", "Customer cannot publish content after upgrade", "One editor sees an error when publishing."));
    }

    [Fact]
    public void Only_higher_guesses_are_shown()
    {
        Assert.Null(Guess("urgent", "Site down", "The website is down"));
        Assert.Null(Guess("high", "Question", "Is this urgent? Seems critical to the editor")); // says urgent only: P2 at most
    }

    [Fact]
    public void Calm_words_cancel_the_signal()
    {
        Assert.Null(Guess("normal", "Small thing", "Not urgent, but the footer link is wrong. No rush."));
    }

    [Fact]
    public void The_customer_saying_it_works_now_stays_quiet()
    {
        var texts = new[] { "The website is down for all users!", "Thanks, that fixed it. You can close the ticket." };

        Assert.Null(PriorityGuess.Guess("normal", "Site down", texts, Sla.StandardKey, TimeSpan.Zero));
    }

    [Fact]
    public void Quoted_earlier_mails_do_not_count()
    {
        Assert.Null(Guess("normal", "Follow-up", "Sounds good, talk Monday. On Tue, 3 Nov 2026 Jane wrote: the website is down for everyone"));
    }

    [Fact]
    public void A_long_wait_counts_only_on_top_of_what_the_text_says()
    {
        Assert.Null(Guess("normal", "Question", "Hello, any news on the invoice question?", waitedHours: 48));

        var hint = Guess("normal", "Still broken", "Still not working, this is unacceptable", waitedHours: 30);
        Assert.Equal("high", hint?.Priority);
        Assert.Contains("waited 30 h", hint!.Reasons);
    }

    [Fact]
    public void A_demanding_SLA_tips_a_borderline_text_over()
    {
        const string text = "We cannot log in to the CMS and our go-live is tomorrow."; // users blocked + deadline soon = 3
        Assert.Equal("high", Guess("normal", "Login", text)?.Priority);
        Assert.Equal("high", Guess("normal", "Login", text, sla: "slapremium")?.Priority);
        Assert.Equal("urgent", Guess("normal", "Login", text, sla: "slapremium", waitedHours: 5)?.Priority);
    }

    [Fact]
    public void Shouting_counts_as_frustrated()
    {
        var hint = Guess("normal", "Login", "WHERE IS MY ANSWER, we cannot log in");

        Assert.Contains("frustrated tone", hint!.Reasons);
    }

    [Fact]
    public void The_notification_shows_the_guess_under_the_SLA_line()
    {
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1 Site down", "Jane · Created 10:00")
            { PriorityHint = new PriorityHint("urgent", ["site down", "frustrated tone"]), SlaDue = DateTimeOffset.UtcNow.AddMinutes(30) };

        var lines = content.DisplayBody(DateTimeOffset.UtcNow).Split('\n');

        Assert.StartsWith("!!", lines[0]);
        Assert.Equal("**Looks like P1:** site down · frustrated tone", lines[1]);
        Assert.Equal("Jane · Created 10:00", lines[2]);
    }
}
