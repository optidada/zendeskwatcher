namespace ZendeskWatcher;

/// <summary>
/// When you last signed in yourself (in the sign-in window), for "Signed in for 5 h 12 min" on the Account page:
/// Zendesk and Okta end the session after about 8 hours. Silent sign-ins don't count; signing out forgets it.
/// </summary>
public static class SignInTime
{
    /// <summary>Longer than this, the time says little (e.g. "Keep me signed in"): not shown.</summary>
    private static readonly TimeSpan ShownUpTo = TimeSpan.FromHours(24);

    private static string FilePath => Path.Combine(AppPaths.DataDir, "signed-in-at");

    public static void Remember() => Try(() => File.WriteAllText(FilePath, DateTime.UtcNow.ToString("O")));

    public static void Forget() => Try(() => File.Delete(FilePath));

    /// <summary>"Signed in for 5 h 12 min.", or null when it isn't known (signed in before this was remembered).</summary>
    public static string? Text()
    {
        try
        {
            return File.Exists(FilePath) && DateTime.TryParse(File.ReadAllText(FilePath), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                ? Text(DateTime.UtcNow - at.ToUniversalTime())
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? Text(TimeSpan elapsed) =>
        elapsed < TimeSpan.Zero || elapsed > ShownUpTo ? null
        : elapsed < TimeSpan.FromMinutes(1) ? "Signed in just now."
        : elapsed < TimeSpan.FromHours(1) ? $"Signed in for {(int)elapsed.TotalMinutes} min."
        : $"Signed in for {(int)elapsed.TotalHours} h {elapsed.Minutes} min.";

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("Could not remember the sign-in time", ex); }
    }
}
