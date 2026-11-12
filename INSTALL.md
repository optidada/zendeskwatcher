# Installing Zendesk Watcher

Zendesk Watcher sits in the Windows tray and notifies you about new unassigned tickets in the groups you pick, and
about changes on your own tickets.

1. **Download** `ZendeskWatcherApp-win-Setup.exe` from the
   [latest release](https://github.com/optidada/zendeskwatcher/releases/latest) (under *Assets*). No GitHub account needed.
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

- Double-click the tray icon to open Settings. Right-click it for *Check now*, *Pause notifications* and more.
- Green icon: watching. Orange: there are unassigned tickets in your groups. Gray: not signed in, or a problem
  (hover for details).
- **Take it** on a new-ticket notification assigns the ticket to you and opens it.

**Problems or ideas?** Tell Daniel Dahlin on Teams. *Open log folder* in the tray menu shows `log.txt`, which helps
when something doesn't work.

**Uninstall:** Windows Settings → Apps → Zendesk Watcher.
