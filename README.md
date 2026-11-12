# Zendesk Watcher

A Windows tray app that watches Zendesk with **your own agent permissions** (read-only):

- toast notifications for **new unassigned tickets** in the groups you pick
- toast notifications when **someone else changes a ticket assigned to you**: public replies, internal notes, status, reassignment or group moves, priority

Clicking a toast opens the ticket in the browser.

## Sign-in

You sign in as usual (Okta / SSO) in a built-in Edge (WebView2) window, so no admin setup is needed. The app then makes the same read-only `/api/v2` calls the Zendesk web app makes, in your own session and with your agent permissions. When the session expires, the app first tries to renew it silently through Okta. If that fails, you get a notification asking you to sign in again.

Check that using your browser session this way is acceptable under your company's policy.

## Run

```
dotnet run -c Release
```

Or build and start `bin\Release\net10.0-windows10.0.17763.0\ZendeskWatcher.exe`.

On first start the **Settings** window opens:

1. **Account:** enter the subdomain (`episerver` for episerver.zendesk.com) and click **Sign in**. A window opens with the Zendesk/Okta login. It closes on its own once you're signed in.
2. **Groups:** tick the groups to monitor. **(No group)** covers tickets with neither a group nor an assignee. The unassigned count of the monitored groups shows on the tray icon.
3. **Notifications:** choose whether new unassigned tickets notify you, which changes on your own tickets notify you, and how often to check.

**Save** stores your changes and keeps the window open, so you can continue on other pages. They take effect when you close the window. **Close** asks before discarding unsaved changes.

To reopen Settings, double-click the tray icon or right-click it and choose **Settings…**. Polling pauses while Settings is open.

## Tray icon

| Color | Meaning |
|---|---|
| Green | Watching; no unassigned tickets in your groups |
| Orange | There are unassigned tickets in your groups |
| Gray | Not configured, sign-in needed, or an error (hover the icon for details) |

## How it works

- **Unassigned tickets:** Zendesk search (`group:<id> assignee:none status<solved`). The search index lags about 1–3 minutes.
- **Your tickets:** your assigned-tickets list plus the ticket audits. These are exact, with no search lag. Changes you make yourself are ignored.
- **First run:** the first poll only records what already exists, so you're not flooded with notifications.
- **Pause notifications:** changes are still recorded but no toasts are shown, so nothing floods in when you resume.

## Files (`%LOCALAPPDATA%\ZendeskWatcher`)

| File | Content |
|---|---|
| `settings.json` | Settings written by the Settings window |
| `WebView2\` | Browser profile with your Zendesk/Okta cookies |
| `state.json` | Tickets already seen and notified |
| `log.txt` | Log, useful when something doesn't work |

Delete `state.json` to reset what has been seen. Use **Sign out** in Settings to clear the cookies.

## Install (colleagues)

Download **`ZendeskWatcherApp-win-Setup.exe`** from the [latest release](https://github.com/optidada/zendeskwatcher/releases/latest) and run it. No admin rights or GitHub account are needed. It installs for the current user under `%LOCALAPPDATA%\ZendeskWatcherApp`, adds Start menu and desktop shortcuts, turns on "Start with Windows" (you can change this in Settings) and starts the app. Uninstall from **Settings → Apps** in Windows. Your settings in `%LOCALAPPDATA%\ZendeskWatcher` are kept.

**Requirements:** Windows 10 (1809+) or 11, x64, and the Microsoft Edge WebView2 Runtime (built into Windows 11, and usually present on Windows 10).

**SmartScreen:** the installer is not code-signed, so Windows may show "Windows protected your PC" the first time (More info → Run anyway). Signing with a company code-signing certificate removes this (`vpk pack --signParams …`).

## Updates

The installed app checks GitHub Releases a minute after starting and then every 6 hours. When a new version has downloaded, a notification offers **Restart now**. If you ignore it, the update installs the next time the app starts. You can also use **Check for updates** in the tray menu. Updates are deltas, so they're usually small.

Update checks only run in the installed app, not when started from `bin\`.

## Releasing a new version

1. Commit your changes.
2. Tag and push: `git tag v1.2.0 && git push origin main v1.2.0`

The [Release workflow](.github/workflows/release.yml) builds the app with that version, packs it with [Velopack](https://velopack.io) and publishes the GitHub Release with `Setup.exe`, a portable zip and the update feed. Installed apps pick it up automatically. The tag is the version; the `<Version>` in the csproj is only used for local builds.

To build the installer locally instead (output in `Releases\`):

```
dotnet tool install -g vpk --version 1.2.161
dotnet publish -c Release -r win-x64 --self-contained -p:Version=1.2.0 -o publish
vpk pack -u ZendeskWatcherApp -v 1.2.0 -p publish -e ZendeskWatcher.exe --packTitle "Zendesk Watcher" -i Assets/app.ico
```

## Author

Author: Claude + Daniel Dahlin @ Optimizely
