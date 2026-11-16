# Zendesk Watcher

A Windows tray app that watches Zendesk with **your own agent permissions**:

- notifications for **new unassigned tickets** in the groups you pick
- notifications when **someone else changes a ticket assigned to you**: public replies, internal notes, status (also custom statuses such as "In progress → Escalated"), reassignment or group moves, priority
- optionally the same for tickets where you're **CC'd or a follower**
- **Help colleagues** (on by default): a notification when a customer is waiting for an answer on a colleague's ticket while that colleague is off work or away (no activity in Zendesk for 3 hours)
- **Catch up**: what still needs you since you stopped working last time, one line each (customers waiting, SLAs past or due today, still unassigned, CC'd and colleagues' tickets, Escalated tickets without a reply for 2+ days, Pending ones without an update for 7+ days), then your own open tickets by status; as a greeting when the app starts, by itself when your working day starts, and from the tray menu

Clicking a notification opens the ticket in the browser. New-ticket notifications show who wrote and the start of their message, and have **Take it**, which assigns the ticket to you and opens it. If a colleague was faster, you're told who has it and the ticket opens anyway. When someone takes the ticket (or it's solved) while its notification is still up, the notification closes by itself within a minute. The **▾** next to Take it lists up to 3 colleagues in the ticket's group who could take it (in their working hours, active in Zendesk lately, least busy first); pick one to assign it to them, after a confirmation. Assigning an unassigned ticket (to you or a colleague you pick) is the only thing the app ever changes in Zendesk.

Priorities are shown with their P-level: Urgent (P1), High (P2), Normal (P3), Low (P4).

## Sign-in

You sign in as usual (Okta / SSO) in a built-in Edge (WebView2) window, so no admin setup is needed. The app then makes the same `/api/v2` calls the Zendesk web app makes, in your own session and with your agent permissions. When the session expires, the app first tries to renew it silently through Okta. If that fails, you get a notification asking you to sign in again.

Check that using your browser session this way is acceptable under your company's policy.

## Code

`Core/` (`ZendeskWatcher.Core`) holds what doesn't need Windows: the Zendesk calls, the checks and their rules, settings, state
and statistics. The Windows app around it adds the tray icon, notifications, the Settings window and the WebView2 sign-in,
so another platform (e.g. a Mac app) could reuse `Core/`.

## Run

```
dotnet run -c Release
```

Or build and start `bin\Release\net10.0-windows\ZendeskWatcher.exe`.

**Tests:** `dotnet test tests/ZendeskWatcher.Tests`. They cover the notification rules (`Core/Tracking/TicketRules.cs`), the
checks end to end against a fake Zendesk (unassigned tickets, ticket changes, Help colleagues, Catch up, client filter), the Settings
pages and the settings file, in a temp data folder (never your real state). The release workflow runs them first; a
failing test stops the release.

On first start the **Settings** window opens, with a short guide at the top that leads you through four steps (sign in, groups, working hours, colleagues; **Skip** closes it):

1. **Account:** enter the subdomain (`yourcompany` for yourcompany.zendesk.com) and click **Sign in**. A window opens with the Zendesk/Okta login. It closes on its own once you're signed in.
2. **Groups:** tick the groups to monitor. **(No group)** covers tickets with neither a group nor an assignee. The unassigned count of the monitored groups shows on the tray icon. On **Clients** (under Groups) untick clients you don't need to hear about (or click **Mute ▾** on a notification's client line); everyone is included by default, new clients too.
3. **Notifications:** choose whether new unassigned tickets notify you, which changes on your own tickets notify you, and your working hours. Below it: **Colleagues**, **Behavior** (how long notifications stay: Short, Normal or Long; snooze), **Look & sound** (style, sounds, an optional glow at the screen edge for the most urgent ones, and how they appear: below each other, stacked like cards, peeking from the screen edge, a pill at the top, a ticker at the screen edge, or "achievement unlocked") and **SLA**.
4. **Insights** shows what's waiting now, your team's workload in your region, and how you used notifications in the last 30 days. **Experimental** has two optional extras worked out on your PC: a priority guess and quiet thank-yous.

**How it works**, the last page, explains in plain words when and why each notification appears.

**Save** stores your changes and applies them right away; the window stays open, so you can continue on other pages. **Close** asks before discarding unsaved changes.

To reopen Settings, click the tray icon or right-click it and choose **Settings…**. Checking keeps running while Settings is open (with the saved settings); it only pauses while you sign in or out, or look at a different Zendesk account. While signed out, Groups, Clients and Colleagues show the lists as last loaded, locked until you sign in.

**Send feedback** (tray menu or General → Updates & feedback) opens a small window for your message and, if you tick it, attaches a zip of `log.txt`, `settings.json` and app details (no comment text). Classic Outlook sends it to the maintainer (it shows in your Sent Items); otherwise a pre-filled email opens, with Explorer showing the zip. Nothing is sent without you.

## Tray icon

| Color | Meaning |
|---|---|
| Green | Online and watching (hover the icon for how many unassigned tickets are waiting) |
| Gray | Not configured, sign-in needed, or an error (hover the icon for details) |

## How it works

- **Unassigned tickets:** Zendesk search (`group:<id> assignee:none status<solved`). The search index lags about 1–3 minutes.
- **Unassigned check:** search results are re-read directly before notifying, because the search index can still list tickets that were assigned long ago. The same lookup also re-reads new tickets whose notification may still be up, and closes the notification once the ticket is assigned or solved.
- **Your tickets:** your assigned-tickets list plus the ticket audits. These are exact, with no search lag. Changes you make yourself are ignored.
- **CC / follower:** your CC'd and followed ticket lists plus audits, the same way. Tickets assigned to you are left to "Your tickets", so nothing is reported twice.
- **Help colleagues:** one search per covered group for assigned tickets updated since the last check (any status before solved). A notification when the newest public comment is from the customer (an end-user who isn't from your own email domain; the requester always counts) and the assignee is outside working hours in their own time zone (unless they changed or commented on a ticket in the last 30 minutes), or in their hours but with no activity in Zendesk for 3 hours. Colleagues without a time zone are skipped. Their activity is read only when a customer wrote (1 search for their recently updated tickets + up to 5 audit reads), remembered for 15 minutes. Any status counts, so the customer is never left waiting because of a status.
- **Catch up:** since the end of your previous working day: one notification with the counts (customers still waiting on your own, CC'd and colleagues' tickets, SLA timers past or due today, tickets still unassigned, quiet Escalated and Pending tickets) and your own open tickets by status. Up to 5 waiting customers and up to 3 unassigned tickets also come one by one, once per working day or when you ask.
- **Status changes:** custom statuses are read once at start (`/api/v2/custom_statuses.json`) and shown by name; New → Open and Solved → Closed don't notify.
- **First run:** the first poll only records what already exists, so you're not flooded with notifications.
- **Pause notifications:** changes are still recorded but no notifications are shown, so nothing floods in when you resume.

## Files (`%LOCALAPPDATA%\ZendeskWatcher`)

| File | Content |
|---|---|
| `settings.json` | Settings written by the Settings window. `settings.json.bak` is the version before the last save; a damaged file is kept as `settings.json.bad` and the backup is used |
| `WebView2\` | Browser profile with your Zendesk/Okta cookies. API responses are not cached (`no-store`), and the cache is cleared at every start |
| `state.json` | Ticket IDs, status and timestamps of tickets already seen. No subjects or other ticket content |
| `cache\` | The lists Settings loads from Zendesk (groups, group members, clients: names only), reused for 24 hours so Settings opens fast; **Reload** fetches them again |
| `log.txt` | Log, useful when something doesn't work. Ticket IDs and subjects, names and change types, but never comment text |

**What is read from Zendesk:** the API always returns complete objects (tickets with their description, audits with comment text, user profiles); the app keeps only what it needs in memory (ticket ID, subject, status, priority, group, assignee, CC/follower IDs, timestamps; change types and, for notifications, the first 140 characters of a comment and the first 300 of a new ticket's first message; user names, roles, time zones and email addresses, the last only to tell your company's people from customers; the names of custom statuses) and discards the rest. The only change it ever makes is assigning an unassigned ticket: **Take it** (to you) or to a colleague you pick under **▾**. Turn off *Show the comment text in notifications* (Notifications → Look & sound) to keep comment text out of notifications too; then a new ticket's first message isn't even read.

Delete `state.json` to reset what has been seen. Use **Sign out** in Settings to clear the cookies.

## Install (colleagues)

The short version for colleagues is in **[INSTALL.md](INSTALL.md)**; send them that link. The details:

Download **`ZendeskWatcherApp-win-Setup.exe`** from the [latest release](https://github.com/optidada/zendeskwatcher/releases/latest) and run it. No GitHub account is needed. It installs for the current user under `%LOCALAPPDATA%\ZendeskWatcherApp`, adds Start menu and desktop shortcuts, turns on "Start with Windows" (you can change this in Settings) and starts the app. Uninstall from **Settings → Apps** in Windows. Your settings in `%LOCALAPPDATA%\ZendeskWatcher` are kept.

**Requirements:** Windows 10 (1809+) or 11, x64. The app needs the **.NET 10 Desktop Runtime** and the Microsoft Edge WebView2 Runtime. Setup installs either one if it's missing. Installing the .NET runtime asks for admin rights (UAC) once; if the runtime is already there, no admin rights are needed. Bundling .NET instead would make the installer about 65 MB instead of about 17 MB.

**SmartScreen:** the installer is not code-signed, so Windows may show "Windows protected your PC" the first time (More info → Run anyway). Signing with a company code-signing certificate removes this (`vpk pack --signParams …`).

## Updates

The installed app checks GitHub Releases two minutes after starting and then once a day. When a new version has downloaded, a notification shows the highlights from the release notes; click it to restart and update now. If you ignore it, the update installs the next time the app starts. You can also use **Check for updates** in the tray menu. Updates are deltas, so they're usually small.

Update checks only run in the installed app, not when started from `bin\`.

## Releasing a new version

1. Add a section for the new version at the top of [CHANGELOG.md](CHANGELOG.md), most interesting changes first. It becomes the release notes, and the bold titles of the first three bullets show in the "Update ready" notification.
2. Commit your changes.
3. Tag and push: `git tag v1.2.0 && git push origin main v1.2.0`

The [Release workflow](.github/workflows/release.yml) builds the app with that version, packs it with [Velopack](https://velopack.io) and publishes the GitHub Release with `Setup.exe`, a portable zip and the update feed. Installed apps pick it up automatically. The tag is the version; the `<Version>` in the csproj is only used for local builds.

To build the installer locally instead (output in `Releases\`):

```
dotnet tool install -g vpk --version 1.2.161
dotnet publish -c Release -r win-x64 --self-contained false -p:Version=1.2.0 -o publish
vpk pack -u ZendeskWatcherApp -v 1.2.0 -r win-x64 -p publish -e ZendeskWatcher.exe --packTitle "Zendesk Watcher" -i Assets/app.ico -f net10-x64-desktop,webview2
```

## Author

Author: Claude & Daniel Dahlin @ Optimizely
