using System.Diagnostics;

namespace ZendeskWatcher;

/// <summary>
/// "Send feedback": a pre-filled email to the maintainer, in classic Outlook if it's installed (most colleagues use
/// it, while mailto may go to e.g. Gmail), else the default mail app; if neither opens, it's copied to the clipboard.
/// A new email can't carry attachments, so the text points at the log folder.
/// </summary>
public static class FeedbackMail
{
    public static string Subject(string version) => $"Zendesk Watcher feedback ({version})";

    public static string Body(string version, string windows, string logFolder) => string.Join("\r\n",
        "What happened, or what would you like?",
        "",
        "",
        "",
        "---",
        $"Version: {version}",
        $"Windows: {windows}",
        $"Log folder: {logFolder}",
        "(If something went wrong, please attach the latest log from that folder.)");

    /// <summary>The address with subject and body, as used after "mailto:" (and by Outlook's /m switch).</summary>
    public static string Address(string version, string windows, string logFolder) =>
        $"{AppUpdater.FeedbackEmail}?subject={Uri.EscapeDataString(Subject(version))}&body={Uri.EscapeDataString(Body(version, windows, logFolder))}";

    public static void Send(string version, Notifier notifier)
    {
        var windows = Environment.OSVersion.Version.ToString();
        var address = Address(version, windows, AppPaths.DataDir);

        if (ClassicOutlookPath() is { } outlook)
        {
            try
            {
                Process.Start(outlook, ["/c", "ipm.note", "/m", address]);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not open Outlook for feedback; trying the default mail app", ex);
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{address}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a mail app for feedback", ex);
            Clipboard.SetText($"To: {AppUpdater.FeedbackEmail}\r\nSubject: {Subject(version)}\r\n\r\n{Body(version, windows, AppPaths.DataDir)}");
            notifier.ShowInfo("No mail app found", $"The feedback email is copied to the clipboard. Paste it into an email to {AppUpdater.FeedbackEmail}.");
        }
    }

    /// <summary>Classic desktop Outlook (OUTLOOK.EXE), which can open a pre-filled email; null if not installed.</summary>
    private static string? ClassicOutlookPath()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
        return key?.GetValue(null) is string path && File.Exists(path) ? path : null;
    }
}
