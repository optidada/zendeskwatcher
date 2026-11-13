using AppKit;
using Foundation;

namespace ZendeskWatcher.Mac;

public static class Program
{
    public static void Main(string[] args)
    {
        NSApplication.Init();
        // Like WinForms: after an await, code continues on the main thread (AppKit and WKWebView need that).
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new MainThreadContext());

        Log.Info($"Mac app starting ({AppInfo.Version}, {AppInfo.System})");
        var app = NSApplication.SharedApplication;
        app.ActivationPolicy = NSApplicationActivationPolicy.Accessory; // menu bar only, no Dock icon
        app.Delegate = new AppDelegate();
        app.Run();
    }
}

/// <summary>Posts continuations to the main thread.</summary>
internal sealed class MainThreadContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) =>
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() => d(state));

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (NSThread.IsMain)
            d(state);
        else
            NSApplication.SharedApplication.InvokeOnMainThread(() => d(state));
    }

    public override SynchronizationContext CreateCopy() => this;
}

internal static class AppInfo
{
    public static string Version =>
        NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "0.0.0";

    public static string System
    {
        get
        {
            var v = NSProcessInfo.ProcessInfo.OperatingSystemVersion;
            return $"macOS {v.Major}.{v.Minor}.{v.PatchVersion}";
        }
    }
}
