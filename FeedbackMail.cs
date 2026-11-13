using System.Diagnostics;

namespace ZendeskWatcher;

/// <summary>
/// "Send feedback" on Windows: builds the feedback zip (<see cref="FeedbackPackage"/>) and opens a pre-filled email to
/// the maintainer. Classic Outlook attaches the zip itself; other mail apps (new Outlook, mailto) can't, so Explorer
/// opens with the zip selected and the email asks to attach it. If no mail app opens, the text goes to the clipboard.
/// </summary>
public static class FeedbackMail
{
    public static string WindowsVersion => $"Windows {Environment.OSVersion.Version}";

    public static void Send(string version, Notifier notifier)
    {
        var system = WindowsVersion;
        string zip;
        try
        {
            zip = FeedbackPackage.CreateZip(version, system);
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the feedback zip", ex);
            zip = AppPaths.LogFile; // still point at the log
        }

        if (ClassicOutlookPath() is { } outlook)
        {
            try
            {
                Process.Start(outlook, ["/a", zip, "/m", FeedbackPackage.Address(version, system, zip, attached: true)]);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not open Outlook for feedback; trying the default mail app", ex);
            }
        }

        ShowInExplorer(zip);
        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{FeedbackPackage.Address(version, system, zip, attached: false)}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a mail app for feedback", ex);
            Clipboard.SetText($"To: {FeedbackPackage.Email}\r\nSubject: {FeedbackPackage.Subject(version)}\r\n\r\n" +
                FeedbackPackage.Body(version, system, zip, attached: false));
            notifier.ShowInfo("No mail app found",
                $"The feedback email is copied to the clipboard. Paste it into an email to {FeedbackPackage.Email} and attach the zip from the folder that opened.");
        }
    }

    /// <summary>Opens Explorer with the file selected, so it can be dragged into the email.</summary>
    private static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the feedback zip in Explorer", ex);
        }
    }

    /// <summary>Classic desktop Outlook (OUTLOOK.EXE), which can open a pre-filled email with an attachment; null if not installed.</summary>
    private static string? ClassicOutlookPath()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
        return key?.GetValue(null) is string path && File.Exists(path) ? path : null;
    }
}
