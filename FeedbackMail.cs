using System.Diagnostics;
using ZendeskWatcher.UI;

namespace ZendeskWatcher;

/// <summary>
/// "Send feedback" on Windows: the user writes it in the app (<see cref="FeedbackForm"/>), and classic Outlook sends it
/// by itself, with the feedback zip (<see cref="FeedbackPackage"/>) attached if chosen; it shows in their Sent Items.
/// When Outlook can't be used that way (the new Outlook, none installed, or the user declined Outlook's prompt), a
/// pre-filled email opens instead, as before: classic Outlook attaches the zip, other mail apps get Explorer with the
/// zip selected. If no mail app opens, the text goes to the clipboard.
/// </summary>
public static class FeedbackMail
{
    public static string WindowsVersion => $"Windows {Environment.OSVersion.Version}";

    /// <summary>Asks what to send, then sends it. Runs on the UI thread; never throws (it's not awaited).</summary>
    public static async Task SendAsync(string version, Notifier notifier)
    {
        if (_open)
            return; // already open (tray menu clicked again)
        _open = true;
        try
        {
            await AskAndSendAsync(version, notifier);
        }
        catch (Exception ex)
        {
            Log.Error("Send feedback failed", ex);
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>The feedback window is open or the email is being sent (UI thread only).</summary>
    private static bool _open;

    private static async Task AskAndSendAsync(string version, Notifier notifier)
    {
        string message;
        bool attach;
        using (var form = new FeedbackForm())
        {
            if (form.ShowDialog() != DialogResult.OK)
                return;
            message = form.Message;
            attach = form.AttachLogs;
        }

        var system = WindowsVersion;
        string? zip = null;
        if (attach)
        {
            try
            {
                zip = FeedbackPackage.CreateZip(version, system);
            }
            catch (Exception ex)
            {
                Log.Error("Could not create the feedback zip", ex);
                zip = AppPaths.LogFile; // still send the log
            }
        }

        if (await SendWithOutlookAsync(version, system, zip, message))
        {
            notifier.ShowInfo("Feedback sent", "Thanks! It's in your Outlook's Sent Items.");
            return;
        }
        OpenDraft(version, system, zip, message, notifier);
    }

    /// <summary>
    /// Classic Outlook sends the email itself (through its automation interface; the new Outlook has none). If Outlook
    /// was started just for this, it's kept until the email has left the Outbox (up to a minute). False if it can't.
    /// </summary>
    private static async Task<bool> SendWithOutlookAsync(string version, string system, string? zip, string message)
    {
        if (Type.GetTypeFromProgID("Outlook.Application") is not { } type)
            return false;
        dynamic? outlook = null;
        try
        {
            var wasRunning = Process.GetProcessesByName("OUTLOOK").Length > 0;
            outlook = Activator.CreateInstance(type)!;
            dynamic mail = outlook.CreateItem(0); // olMailItem
            mail.To = FeedbackPackage.Email;
            mail.Subject = FeedbackPackage.Subject(version);
            mail.Body = FeedbackPackage.Body(version, system, zip, attached: true, message);
            if (zip is not null)
                mail.Attachments.Add(zip);
            mail.Send();
            Log.Info($"Feedback sent through Outlook{(zip is null ? "" : $", with {Path.GetFileName(zip)}")}");

            if (!wasRunning)
            {
                // Started just for this: give it time to send before it may close again.
                dynamic outbox = outlook.Session.GetDefaultFolder(4); // olFolderOutbox
                outlook.Session.SendAndReceive(false);
                for (var i = 0; i < 30 && outbox.Items.Count > 0; i++)
                    await Task.Delay(2000);
            }
            return true;
        }
        catch (Exception ex)
        {
            // The new Outlook, no profile, or "A program is trying to send email" was declined.
            Log.Error("Could not send the feedback through Outlook; opening an email instead", ex);
            return false;
        }
        finally
        {
            if (outlook is not null)
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(outlook);
        }
    }

    /// <summary>The fallback: a pre-filled email to send by hand.</summary>
    private static void OpenDraft(string version, string system, string? zip, string message, Notifier notifier)
    {
        if (ClassicOutlookPath() is { } outlook)
        {
            try
            {
                var args = new List<string>();
                if (zip is not null)
                    args.AddRange(["/a", zip]);
                args.AddRange(["/m", FeedbackPackage.Address(version, system, zip, attached: true, message)]);
                Process.Start(outlook, args);
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Could not open Outlook for feedback; trying the default mail app", ex);
            }
        }

        if (zip is not null)
            ShowInExplorer(zip);
        try
        {
            Process.Start(new ProcessStartInfo($"mailto:{FeedbackPackage.Address(version, system, zip, attached: false, message)}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open a mail app for feedback", ex);
            Clipboard.SetText($"To: {FeedbackPackage.Email}\r\nSubject: {FeedbackPackage.Subject(version)}\r\n\r\n" +
                FeedbackPackage.Body(version, system, zip, attached: false, message));
            notifier.ShowInfo("No mail app found",
                $"The feedback email is copied to the clipboard. Paste it into an email to {FeedbackPackage.Email}" +
                (zip is null ? "." : " and attach the zip from the folder that opened."));
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
