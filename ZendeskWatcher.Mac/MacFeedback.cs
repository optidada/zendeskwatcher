using System.Diagnostics;
using AppKit;
using Foundation;

namespace ZendeskWatcher.Mac;

/// <summary>
/// "Send feedback" on the Mac: the same zip as on Windows (<see cref="FeedbackPackage"/>). Apple Mail gets it attached
/// via AppleScript; if that fails (another mail app, or Mail not allowed), Finder shows the zip and a mailto email asks
/// to attach it. Never sent by itself.
/// </summary>
public static class MacFeedback
{
    public static void Send(MacNotifier notifier)
    {
        var version = AppInfo.Version;
        var system = AppInfo.System;
        string zip;
        try
        {
            zip = FeedbackPackage.CreateZip(version, system);
        }
        catch (Exception ex)
        {
            Log.Error("Could not create the feedback zip", ex);
            zip = AppPaths.LogFile;
        }

        if (TryAppleMail(version, system, zip))
            return;

        NSWorkspace.SharedWorkspace.ActivateFileViewer([NSUrl.FromFilename(zip)]);
        if (!NSWorkspace.SharedWorkspace.OpenUrl(new NSUrl("mailto:" + FeedbackPackage.Address(version, system, zip, attached: false))))
            notifier.Info("No mail app found", $"Send an email to {FeedbackPackage.Email} and attach the zip shown in Finder.");
    }

    /// <summary>Apple Mail, if it's the default mail app: a new visible message with the zip attached.</summary>
    private static bool TryAppleMail(string version, string system, string zip)
    {
        var mailApp = NSWorkspace.SharedWorkspace.UrlForApplication(new NSUrl("mailto:x@example.com"));
        if (mailApp?.Path is not { } path || !path.EndsWith("/Mail.app", StringComparison.Ordinal))
            return false;

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\" & return & \"") + "\"";
        var script = string.Join("\n",
            "tell application \"Mail\"",
            $"  set m to make new outgoing message with properties {{subject:{Quote(FeedbackPackage.Subject(version))}, content:{Quote(FeedbackPackage.Body(version, system, zip, attached: true))}, visible:true}}",
            $"  tell m to make new to recipient at end of to recipients with properties {{address:{Quote(FeedbackPackage.Email)}}}",
            $"  tell content of m to make new attachment with properties {{file name:(POSIX file {Quote(zip)})}} at after last paragraph",
            "  activate",
            "end tell");
        try
        {
            using var process = Process.Start("/usr/bin/osascript", ["-e", script]);
            process.WaitForExit(15000);
            if (process.ExitCode == 0)
                return true;
            Log.Info($"Apple Mail feedback failed (exit {process.ExitCode}); using mailto");
        }
        catch (Exception ex)
        {
            Log.Error("Apple Mail feedback failed", ex);
        }
        return false;
    }
}
