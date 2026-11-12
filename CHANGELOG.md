# Changelog

Each release takes its notes from the section with its version number. The first bullet points also show in the
app's "Update ready" notification, so put the most interesting changes first.

## 1.3.6

- **New Optimizely theme**: the optimizely.com look (dark fir green, lime accents), for popups and the Settings window. It's the default for new installs.
- The tray icon pulses gently while the app is checking Zendesk.
- A welcome notification on the very first start explains what to set up.
- A successful sign-in saves the subdomain right away, so closing Settings without Save doesn't lose the connection.
- The sign-in window shows "Opening the Zendesk sign-in…" while the login page loads, instead of a plain text page.
- Settings → Account: no Sign in button while the app is still checking whether you're signed in.
- Checking keeps running while Settings is open, and **Save applies changes right away** (e.g. a newly ticked group starts being watched immediately). It only pauses while signing in or out.
- "Check for updates" answers with a notification ("No updates · You have the latest version") instead of a dialog box.

## 1.3.5

- **Colleagues' tickets** get their own sound and display time (Settings → Appearance), default *Reminder* and *Long*, plus a test button.
- Notifications can be clicked while Settings is open (Settings is no longer a modal window).

## 1.3.4

- Change notifications show priorities and statuses the way Zendesk does: "Priority Normal (P3) → Urgent (P1)", "Status Pending → Open".

## 1.3.3

- The icon is the same plain Z everywhere (tray, taskbar, window, Start menu).
- Settings: the Save button only shows when there are unsaved changes.

## 1.3.2

- When you start monitoring a group, one notification shows what's already waiting there ("Now watching Tier 2 · 3 unassigned tickets already waiting"); new tickets after that notify one by one as before.
- The tray icon turns lime as soon as you're signed in, instead of staying gray until the first check finishes.
- Notifications use English day and month names whatever the PC's language ("Fri 01:06", "12 Nov").
- Fixed: re-ticking a group right after unticking it didn't show the "Now watching" summary.
- Fixed: checking could hang ("Connecting…" in the tray tooltip) if a check started while the hidden browser was going to sleep. Requests now have a time limit, and an unresponsive browser is restarted.
- The tray tooltip says "Paused while Settings is open" while Settings is open.
- Faster checks (about half the time): ticket lists fetch the 30 most recently updated tickets instead of 100. Checks slower than 5 seconds are logged with a breakdown.

## 1.3.1

- Settings: greyed-out options (e.g. the Help colleagues group list) are readable in the dark themes.

## 1.3.0

- **Help colleagues** (Settings → Notifications): get notified when a customer replies on a colleague's open ticket while they're off work, so you can step in. "Off work" means outside working hours (default Mon–Fri 08:00–17:00) in the colleague's own time zone from their Zendesk profile. Uses your monitored groups, or a custom selection, for Urgent (P1) and High (P2) tickets by default, and only while you're working yourself (can be changed).

- **New icon** in the Optimizely style: a lime Z with a dark-green extrusion (with a small watching eye where there's room).
- The log shows how many Zendesk requests the app made every 10 minutes.

## 1.2.2

- **Privacy**: Zendesk responses are no longer kept in the browser cache, the log never contains comment text (older logs are removed once), and saved state holds only ticket IDs and status, no subjects.
- **New setting**: *Show the comment text in notifications* (Settings → Notifications). Turn it off to only see who replied or added a note.
- Settings opens without waiting: groups show as soon as they're loaded, suggestions fill in afterwards, and reopening Settings within half an hour is instant.

## 1.2.1

- The same notification no longer shows twice (e.g. "Update ready" when a manual and the daily update check coincide).
- Notification buttons say what they do: **Update now**, **Sign in**, **Open Settings**.
- **Sign out** now sticks: the app no longer signs you back in silently, and Settings opens right away. When your session expires, it also gives up waiting for a silent sign-in sooner.
- Settings saved by a newer version (e.g. a new theme) no longer stop an older version from opening Settings.

## 1.2.0

- **Take it**: new-ticket notifications have a button that assigns the ticket to you and opens it. If a colleague was faster, you're told who has it.
- **Priorities with P-levels**: Urgent (P1), High (P2), Normal (P3), Low (P4).
- **CC and follower notifications** (optional, under Settings → Notifications).
- **Group suggestions**: Settings → Groups suggests groups you're a member of or that have your tickets.
- New-ticket notifications show how long ago the ticket was created ("Created 09:48 · 7 min ago").
- **New Cozy theme**: dusky purple with warm, soft colors.
- The Settings window follows the chosen appearance (Neon dark, Terminal, Clean light, Cozy, or Windows light/dark).
- New icon: the watching eye.
- Smaller installer (about 17 MB). Setup installs the .NET 10 Desktop Runtime if it's missing; that asks for admin rights once.
- Settings: the group list no longer jumps when you tick or untick a group, monitored groups that no longer exist in Zendesk are flagged, and the drop-downs have more room.
- Notification text no longer shows HTML codes like `&nbsp;`.
- Uses less memory (about 40% less) and less CPU while idle.
- Update checks run once a day instead of every 6 hours.

## 1.1.1

- Only notify about tickets that are really unassigned. Zendesk search can list tickets that were assigned long ago; they're now checked first.

## 1.1.0

- First release with an installer and automatic updates.
