using System.IO.Compression;
using System.Text;
using ZendeskWatcher.Tracking;

namespace ZendeskWatcher;

/// <summary>
/// "Send feedback", shared by the Windows and Mac apps: the email text and one zip with what helps find a problem
/// (log.txt, settings.json, about.txt). Nothing is sent by itself: the zip goes into an email the user sees first.
/// </summary>
public static class FeedbackPackage
{
    public const string Email = "daniel.dahlin@optimizely.com";

    public static string Subject(string version) => $"Zendesk Watcher feedback ({version})";

    /// <param name="system">E.g. "Windows 10.0.26200" or "macOS 15.1".</param>
    /// <param name="zipPath">The zip; when the mail app couldn't attach it, the body asks to attach it by hand.</param>
    /// <param name="attached">True when the mail app attached the zip itself (classic Outlook, Apple Mail).</param>
    public static string Body(string version, string system, string zipPath, bool attached) => string.Join("\r\n",
        "What happened, or what would you like?",
        "",
        "",
        "",
        "---",
        $"Version: {version}",
        $"System: {system}",
        attached
            ? $"Attached: {Path.GetFileName(zipPath)} (log, settings and app details; no ticket text)."
            : $"Please attach {Path.GetFileName(zipPath)} (log, settings and app details; no ticket text). " +
              $"It's in the folder that just opened: {zipPath}");

    /// <summary>The address with subject and body, as used after "mailto:" (and by Outlook's /m switch).</summary>
    public static string Address(string version, string system, string zipPath, bool attached) =>
        $"{Email}?subject={Uri.EscapeDataString(Subject(version))}&body={Uri.EscapeDataString(Body(version, system, zipPath, attached))}";

    /// <summary>What about.txt says: app, system, and whether checking works right now.</summary>
    public static string About(string version, string system, DateTime now) => string.Join(Environment.NewLine,
        $"Zendesk Watcher {version}",
        $"System: {system}",
        $"Created: {now:yyyy-MM-dd HH:mm}",
        $"Checking: {CheckingState()}");

    private static string CheckingState() =>
        WatchStatus.Problem is { } problem ? problem
        : WatchStatus.Latest is { } latest ? $"works (last check {latest.CheckedAt:HH:mm}, signed in)"
        : "not started yet (not signed in, or still starting)";

    /// <summary>
    /// Writes ZendeskWatcher-feedback-(date and time).zip into a folder under Temp (removing older ones) and returns its path.
    /// Files that are in use (the log) are read with sharing, so this works while the app is writing to them.
    /// </summary>
    public static string CreateZip(string version, string system, string? folder = null)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        CleanUp(TimeSpan.Zero, folder); // each new zip gets its own name; older ones go
        var zipPath = Path.Combine(folder, $"ZendeskWatcher-feedback-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddFile(zip, AppPaths.LogFile, "log.txt");
        AddFile(zip, Path.Combine(AppPaths.DataDir, "settings.json"), "settings.json");
        var about = zip.CreateEntry("about.txt");
        using (var writer = new StreamWriter(about.Open(), new UTF8Encoding(false)))
            writer.Write(About(version, system, DateTime.Now));
        return zipPath;
    }

    private static string DefaultFolder => Path.Combine(Path.GetTempPath(), "ZendeskWatcher-feedback");

    /// <summary>
    /// Removes feedback zips older than <paramref name="age"/>: the email has its own copy once it's attached, so the
    /// file isn't needed afterwards. A zip a mail app still holds open stays until next time. The app calls this at start.
    /// </summary>
    public static void CleanUp(TimeSpan age, string? folder = null)
    {
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder))
            return;
        foreach (var file in Directory.GetFiles(folder, "ZendeskWatcher-feedback*.zip"))
        {
            try
            {
                if (DateTime.Now - File.GetLastWriteTime(file) >= age)
                    File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        try
        {
            if (!File.Exists(path))
                return;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = zip.CreateEntry(name).Open();
            source.CopyTo(target);
        }
        catch (Exception ex)
        {
            Log.Error($"Feedback: could not add {name}", ex);
        }
    }
}
