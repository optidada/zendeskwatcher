# Installing Zendesk Watcher

Zendesk Watcher sits in the Windows tray and notifies you about new unassigned tickets in the groups you pick, about
changes on your own tickets, and about customers waiting on colleagues' tickets while those colleagues are off work.

1. **Download** the installer:
   [ZendeskWatcherApp-win-Setup.exe](https://github.com/optidada/zendeskwatcher/releases/latest/download/ZendeskWatcherApp-win-Setup.exe)
   (always the newest version; release notes on the [latest release](https://github.com/optidada/zendeskwatcher/releases/latest) page). No GitHub account needed.
2. **Run it.** Windows may say *"Windows protected your PC"* because the installer isn't signed. Click
   **More info → Run anyway**.
3. **Allow the .NET install if asked.** If your PC doesn't have the .NET 10 Desktop Runtime yet, Setup installs it,
   which asks for admin rights once. If you can't approve that, ask IT to install the
   *.NET 10 Desktop Runtime (x64)*.
4. **Sign in.** Settings opens. The subdomain (the part before `.zendesk.com` in the address you use for Zendesk) is usually filled in already; otherwise enter it. Click **Sign in**. You sign in with Okta as usual.
5. **Pick your groups** under *Groups*, look through *Notifications* (with *Colleagues* and *Look & sound*), and click **Save**.

That's it. The app starts with Windows (you can turn that off in Settings) and updates itself: when a new version
is ready, you get a notification with **Restart now**.

**Tips**

- Click the tray icon to open Settings. Right-click it for *Check now*, *Pause notifications* and more.
- Green icon: watching. Orange: there are unassigned tickets in your groups. Gray: not signed in, or a problem
  (hover for details).
- **Take it** on a new-ticket notification assigns the ticket to you and opens it.
- **Catch up** (tray menu, or the button on the morning summary) shows what still needs you since you stopped working
  last time. It also runs by itself when your working day starts.
- Wondering why you got a notification, or didn't? Settings → **How it works** explains the rules.
- Sounds are off to begin with; pick one per kind under Notifications → Look & sound.

**Problems or ideas?** Tell Daniel Dahlin on Teams. *Open log folder* in Settings → General shows `log.txt`, which helps
when something doesn't work.

**Uninstall:** Windows Settings → Apps → Zendesk Watcher.
