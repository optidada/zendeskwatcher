using Microsoft.Toolkit.Uwp.Notifications;
using Velopack;

namespace ZendeskWatcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Must run first: handles the installer/updater hooks (these run the exe briefly and exit).
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => StartupRegistration.Set(true)) // tray app: start with Windows by default
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Set(false))
            .Run();

        // Developer helper: write the app icon (.ico) and preview PNGs, then exit.
        if (args.Length >= 2 && args[0] == "--export-icons")
        {
            ExportIcons(args[1]);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\ZendeskWatcher", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A click on a toast is delivered to the running instance; a second manual start just exits.
            if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
                MessageBox.Show("Zendesk Watcher is already running. Look for the icon in the system tray.", "Zendesk Watcher");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ToastNotificationManagerCompat.OnActivated += e =>
            TrayAppContext.Current?.OnToastActivated(ToastArguments.Parse(e.Argument));

        Log.Info("Started");
        var app = new TrayAppContext();
        if (args.Contains("--test-popups"))
            app.ShowTestPopups(); // for trying the notification styles without waiting for real tickets
        Application.Run(app);
        Log.Info("Stopped");
    }

    private static void ExportIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        UI.AppIcon.SaveIco(Path.Combine(dir, "app.ico"), UI.AppIcon.NeonGreen, 16, 20, 24, 32, 40, 48, 64, 128, 256);
        foreach (var size in new[] { 16, 32, 64, 256 })
        {
            using var green = UI.AppIcon.Render(size, UI.AppIcon.NeonGreen);
            green.Save(Path.Combine(dir, $"preview-{size}.png"));
        }
        using var orange = UI.AppIcon.Render(256, UI.AppIcon.NeonOrange);
        orange.Save(Path.Combine(dir, "preview-256-orange.png"));
        using var orangeSmall = UI.AppIcon.Render(16, UI.AppIcon.NeonOrange);
        orangeSmall.Save(Path.Combine(dir, "preview-16-orange.png"));
    }
}
