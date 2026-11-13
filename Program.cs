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

        // Developer helper: count the requests of the Zendesk Agent Workspace in a browser window, then exit.
        if (args.Length >= 3 && args[0] == "--measure-zendesk-web")
        {
            ApplicationConfiguration.Initialize();
            Tools.WebMeasure.Run(Settings.Load().Subdomain, int.Parse(args[1]), args[2]);
            return;
        }

        // Developer helper: write the chiptune jingles and songs as .wav files, then exit.
        if (args.Length >= 2 && args[0] == "--export-sounds")
        {
            UI.ChipSounds.Export(args[1]);
            return;
        }

        // Developer helper: previews of the Optimizely-style icon, then exit.
        if (args.Length >= 2 && args[0] == "--export-brand-icons")
        {
            UI.BrandIcon.ExportPreviews(args[1]);
            return;
        }

        // Developer helper: draw the sample popups in every theme to PNG files, then exit.
        if (args.Length >= 2 && args[0] == "--render-popups")
        {
            ApplicationConfiguration.Initialize();
            UI.Popups.PopupForm.RenderSamples(args[1]);
            return;
        }

        // Developer helper: draw the themed dialog in every appearance to PNG files, then exit.
        if (args.Length >= 2 && args[0] == "--render-dialog")
        {
            ApplicationConfiguration.Initialize();
            UI.ThemedDialog.RenderSamples(args[1]);
            return;
        }

        // Developer helper: the tray menu in each app theme (--render-menu <dir>), then exit.
        if (args.Length >= 2 && args[0] == "--render-menu")
        {
            ApplicationConfiguration.Initialize();
            UI.ThemedMenu.RenderSamples(args[1]);
            return;
        }

        // Developer helper: CPU use of an idle Settings page (--measure-settings <page> <seconds> <file>), then exit.
        if (args.Length >= 4 && args[0] == "--measure-settings")
        {
            ApplicationConfiguration.Initialize();
            SettingsForm.MeasureIdle(int.Parse(args[1]), int.Parse(args[2]), args[3]);
            return;
        }

        // Developer helper: draw the Settings window in every appearance to PNG files, then exit.
        if (args.Length >= 2 && args[0] == "--render-settings")
        {
            ApplicationConfiguration.Initialize();
            SettingsForm.RenderSamples(args[1]);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\ZendeskWatcher", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // A click on a toast is delivered to the running instance; a second manual start asks the running one
            // to say it's there (as a notification), then exits.
            if (!ToastNotificationManagerCompat.WasCurrentProcessToastActivated())
                SignalRunningInstance();
            return;
        }

        // A second start sets this event; the running app then shows "already running".
        using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, AlreadyRunningEvent);
        new Thread(() =>
        {
            while (shown.WaitOne())
                TrayAppContext.Current?.ShowAlreadyRunning();
        }) { IsBackground = true, Name = "Second start" }.Start();

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);

        ToastNotificationManagerCompat.OnActivated += e =>
            TrayAppContext.Current?.OnToastActivated(ToastArguments.Parse(e.Argument));

        Log.DropOldLogsOnce();
        Log.Info("Started");
        var app = new TrayAppContext();
        if (args.Contains("--test-popups"))
            app.ShowTestPopups(); // for trying the notification styles without waiting for real tickets
        Application.Run(app);
        Log.Info("Stopped");
    }

    private const string AlreadyRunningEvent = @"Local\ZendeskWatcher.AlreadyRunning";

    private static void SignalRunningInstance()
    {
        try
        {
            using var shown = EventWaitHandle.OpenExisting(AlreadyRunningEvent);
            shown.Set();
        }
        catch (Exception)
        {
            // The running copy is an older version (or just starting): fall back to the old message.
            MessageBox.Show("Zendesk Watcher is already running. Look for the icon in the system tray.", "Zendesk Watcher");
        }
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
