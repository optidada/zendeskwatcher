using System.IO.Compression;

namespace ZendeskWatcher.Tests;

public class FeedbackMailTests
{
    [Fact]
    public void Subject_and_body_carry_the_version_details()
    {
        Assert.Equal("Zendesk Watcher feedback (1.3.10)", FeedbackPackage.Subject("1.3.10"));

        var body = FeedbackPackage.Body("1.3.10", "Windows 10.0.26200", @"C:\Temp\ZendeskWatcher-feedback\ZendeskWatcher-feedback.zip", attached: true);
        Assert.Contains("Version: 1.3.10", body);
        Assert.Contains("System: Windows 10.0.26200", body);
        Assert.Contains("Attached: ZendeskWatcher-feedback.zip", body);
    }

    [Fact]
    public void Without_attachment_the_body_says_where_the_zip_is()
    {
        var body = FeedbackPackage.Body("1.4.1", "macOS 15.1", "/tmp/x/ZendeskWatcher-feedback.zip", attached: false);

        Assert.Contains("Please attach ZendeskWatcher-feedback.zip", body);
        Assert.Contains("/tmp/x/ZendeskWatcher-feedback.zip", body);
    }

    [Fact]
    public void Address_is_safe_for_mailto_and_outlook()
    {
        var address = FeedbackPackage.Address("1.3.10", "Windows 10.0.26200", @"C:\Logs & stuff\f.zip", attached: false);

        Assert.StartsWith($"{FeedbackPackage.Email}?subject=", address);
        Assert.DoesNotContain(" ", address);   // spaces would split Outlook's /m argument
        Assert.DoesNotContain("\n", address);  // line breaks are encoded
        Assert.Contains("%0D%0A", address);
        Assert.Equal(2, address.Split('?').Length); // only the ? that starts the query
        Assert.Equal(2, address.Split('&').Length); // only the & between subject and body; the one in the path is encoded
    }

    [Fact]
    public void Zip_has_log_settings_and_about()
    {
        File.AppendAllText(AppPaths.LogFile, "test line\n");
        var folder = Path.Combine(Path.GetTempPath(), "zw-feedback-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The app keeps writing to the log meanwhile: the zip must still be created.
            using var openLog = new FileStream(AppPaths.LogFile, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            var zipPath = FeedbackPackage.CreateZip("1.4.1", "Windows 10.0.26200", folder);

            using var zip = ZipFile.OpenRead(zipPath);
            Assert.Contains(zip.Entries, e => e.Name == "log.txt");
            Assert.Contains(zip.Entries, e => e.Name == "about.txt");
            using var about = new StreamReader(zip.GetEntry("about.txt")!.Open());
            var text = about.ReadToEnd();
            Assert.Contains("Zendesk Watcher 1.4.1", text);
            Assert.Contains("System: Windows 10.0.26200", text);
            Assert.Contains("Checking:", text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
