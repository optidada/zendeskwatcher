namespace ZendeskWatcher;

/// <summary>Per-user data folder: %LOCALAPPDATA%\ZendeskWatcher (tests point it elsewhere with ZENDESKWATCHER_DATA_DIR).</summary>
public static class AppPaths
{
    public const string DataDirVariable = "ZENDESKWATCHER_DATA_DIR";

    public static string DataDir { get; } = CreateDataDir();

    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");

    private static string CreateDataDir()
    {
        var dir = Environment.GetEnvironmentVariable(DataDirVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZendeskWatcher");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    /// <summary>
    /// Starts a fresh log once: logs from before 1.2.2 could contain comment text, which is no longer logged.
    /// </summary>
    public static void DropOldLogsOnce()
    {
        var marker = Path.Combine(AppPaths.DataDir, "log-without-comments");
        if (File.Exists(marker))
            return;
        try
        {
            File.Delete(AppPaths.LogFile);
            File.Delete(AppPaths.LogFile + ".old");
            File.WriteAllText(marker, "Logs no longer contain comment text; older logs were removed.");
        }
        catch
        {
            // Never block startup over this; try again next time.
        }
    }
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 2_000_000)
                {
                    file.MoveTo(AppPaths.LogFile + ".old", overwrite: true);
                }
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break the app.
            }
        }
    }
}
