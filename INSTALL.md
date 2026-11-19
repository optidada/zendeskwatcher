# Installing Zendesk Watcher

Once installed, the [guide](GUIDE.md) shows what the notifications and Settings pages look like.

Zendesk Watcher sits in the Windows tray and notifies you about new unassigned tickets in the groups you pick, about
changes on your own tickets, and about customers waiting on colleagues' tickets while those colleagues are off work or away.

1. **Download** the installer:
   [ZendeskWatcherApp-win-Setup.exe](https://github.com/optidada/zendeskwatcher-releases/releases/latest/download/ZendeskWatcherApp-win-Setup.exe)
   (always the newest version; release notes on the [latest release](https://github.com/optidada/zendeskwatcher-releases/releases/latest) page). No GitHub account needed.
2. **Run it.** Windows may say *"Windows protected your PC"* because the installer isn't signed. Click
   **More info → Run anyway**.
3. **Allow the .NET install if asked.** If your PC doesn't have the .NET 10 Desktop Runtime yet, Setup installs it,
   which asks for admin rights once. If you can't approve that, ask IT to install the
   *.NET 10 Desktop Runtime (x64)*.
4. **Sign in.** Settings opens with a short guide (sign in, groups, working hours, colleagues). The subdomain (the part before `.zendesk.com` in the address you use for Zendesk) is usually filled in already; otherwise enter it. Click **Sign in**. You sign in with Okta as usual.
5. **Pick your groups** under *Groups*, set your working hours under *Notifications*, choose whose tickets you cover under *Colleagues*, and click **Save**.

That's it. The app starts with Windows (you can turn that off in Settings) and updates itself: when a new version
is ready, you get a notification: click it to restart and update now, or it installs on the next start.

**Tips**

- Click the tray icon to open Settings. Right-click it for *Check now*, *Pause notifications* and more.
- Green icon: watching (hover for how many unassigned tickets are waiting). Gray: not signed in, or a problem
  (hover for details).
- **Take it** on a new-ticket notification assigns the ticket to you and opens it.
- **Catch up** greets you when the app starts: what still needs you since you stopped working last time, and your
  tickets by status. It also comes by itself when your working day starts, and from the tray menu at any time.
- Wondering why you got a notification, or didn't? Settings → **How it works** explains the rules.
- New tickets have a soft chime; the rest are silent to begin with. Pick a sound per kind under Notifications → Look & sound.

**Problems or ideas?** *Send feedback* (tray menu, or Settings → General → Updates & feedback): write your message, and
it's emailed to Daniel Dahlin from your Outlook, with your log attached if you like (it helps when something doesn't
work). Or tell him on Teams.

**Uninstall:** Windows Settings → Apps → Zendesk Watcher.
