namespace ZendeskWatcher;

/// <summary>Per-user data folder: %LOCALAPPDATA%\ZendeskWatcher.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = CreateDataDir();

    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");

    private static string CreateDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZendeskWatcher");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
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
