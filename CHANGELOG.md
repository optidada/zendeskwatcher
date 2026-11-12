# Changelog

Each release takes its notes from the section with its version number. The first bullet points also show in the
app's "Update ready" notification, so put the most interesting changes first.

## 1.2.2

- **Privacy**: Zendesk responses are no longer kept in the browser cache, the log never contains comment text (older logs are removed once), and saved state holds only ticket IDs and status, no subjects.
- **New setting**: *Show the comment text in notifications* (Settings → Notifications). Turn it off to only see who replied or added a note.

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
