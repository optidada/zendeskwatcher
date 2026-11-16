using ZendeskWatcher.Tracking;
using ZendeskWatcher.UI;
using ZendeskWatcher.UI.Popups;

namespace ZendeskWatcher.Tests;

/// <summary>The experimental priority guess: higher guesses with readable reasons, "probably not" for Urgent/High without any sign; calm and solved texts stay quiet.</summary>
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
        Assert.StartsWith("Set as P3 · looks like P1: site down", Markdown.Strip(hint.Text));
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
    public void Agreeing_guesses_are_not_shown()
    {
        Assert.Null(Guess("urgent", "Site down", "The website is down"));
        Assert.Null(Guess("high", "Question", "Is this urgent? Seems critical to the editor")); // says urgent only: P2 at most
    }

    [Theory]
    [InlineData("urgent", "P1")]
    [InlineData("high", "P2")]
    [InlineData("High", "P2")]
    public void Urgent_or_high_without_any_sign_is_probably_not(string priority, string tag)
    {
        var hint = Guess(priority, "Footer link", "Hi, the footer link on the about page points to the old address. Thanks");

        Assert.NotNull(hint);
        Assert.True(hint!.Lower);
        Assert.Equal($"Set as {tag} · probably not: no signs of urgency", Markdown.Strip(hint.Text));
        Assert.Equal($"**Set as {Markdown.Tag(tag)} · probably not:** no signs of urgency", hint.Text);
    }

    [Theory]
    [InlineData("urgent", "Login", "We cannot log in to the CMS")]        // one signal is enough to say nothing
    [InlineData("high", "Question", "Any update on this?")]               // a frustrated tone
    [InlineData("urgent", "Fixed", "Thanks, that fixed it. You can close the ticket.")] // solved: no hint at all
    [InlineData("normal", "Footer link", "The footer link points to the old address.")] // only for P1 and P2
    [InlineData("low", "Footer link", "The footer link points to the old address.")]
    [InlineData(null, "Footer link", "The footer link points to the old address.")]
    public void Probably_not_only_for_P1_and_P2_with_no_signal_at_all(string? priority, string subject, string text)
    {
        Assert.False(Guess(priority, subject, text)?.Lower ?? false);
    }

    [Fact]
    public void Similar_earlier_P1s_count_as_a_signal_for_probably_not()
    {
        Assert.Null(PriorityGuess.Guess("urgent", "Footer link", ["The footer link points to the old address."], Sla.StandardKey,
            TimeSpan.Zero, ("urgent", 3)));
    }

    [Fact]
    public void Wording_for_higher_guesses_names_both_priorities()
    {
        Assert.Equal("Set as P3 · looks like P1: site down · frustrated tone",
            Markdown.Strip(new PriorityHint("urgent", ["site down", "frustrated tone"], "normal").Text));
        Assert.Equal("Set as P4 · looks like P2: deadline soon", Markdown.Strip(new PriorityHint("high", ["deadline soon"], "low").Text));
        Assert.Equal("No priority · looks like P1: site down", Markdown.Strip(new PriorityHint("urgent", ["site down"]).Text));
        Assert.False(new PriorityHint("urgent", ["site down"], "normal").Lower);
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
        var content = new NotificationContent(NotificationKind.NewTicket, "New ticket", "#1 Site down", "Jane\n“It's down”")
            { PriorityHint = new PriorityHint("urgent", ["site down", "frustrated tone"], "normal"), SlaDue = DateTimeOffset.UtcNow.AddMinutes(30) };

        var lines = content.DisplayBody(DateTimeOffset.UtcNow).Split('\n');

        Assert.StartsWith("Jane\t!!", lines[0]); // who, with the deadline at the right end
        Assert.Equal($"**Set as {Markdown.Tag("P3")} · looks like {Markdown.Tag("P1")}:** site down · frustrated tone", lines[1]);
        Assert.Equal("“It's down”", lines[2]);
    }
}
