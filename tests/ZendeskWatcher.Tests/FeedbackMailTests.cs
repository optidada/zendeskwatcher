namespace ZendeskWatcher.Tests;

public class FeedbackMailTests
{
    [Fact]
    public void Subject_and_body_carry_the_version_details()
    {
        Assert.Equal("Zendesk Watcher feedback (1.3.10)", FeedbackMail.Subject("1.3.10"));

        var body = FeedbackMail.Body("1.3.10", "10.0.26200", @"C:\Users\x\AppData\Local\ZendeskWatcher");
        Assert.Contains("Version: 1.3.10", body);
        Assert.Contains("Windows: 10.0.26200", body);
        Assert.Contains(@"Log folder: C:\Users\x\AppData\Local\ZendeskWatcher", body);
    }

    [Fact]
    public void Address_is_safe_for_mailto_and_outlook()
    {
        var address = FeedbackMail.Address("1.3.10", "10.0.26200", @"C:\Logs & stuff");

        Assert.StartsWith($"{AppUpdater.FeedbackEmail}?subject=", address);
        Assert.DoesNotContain(" ", address);   // spaces would split Outlook's /m argument
        Assert.DoesNotContain("\n", address);  // line breaks are encoded
        Assert.Contains("%0D%0A", address);
        Assert.Equal(2, address.Split('?').Length); // only the ? that starts the query
        Assert.Equal(2, address.Split('&').Length); // only the & between subject and body; the one in the path is encoded
    }
}
