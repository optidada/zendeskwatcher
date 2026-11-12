# Changelog

Each release takes its notes from the section with its version number. The first bullet points also show in the
app's "Update ready" notification, so put the most interesting changes first.

## 1.2.0

- **Take it**: new-ticket notifications have a button that assigns the ticket to you and opens it. If a colleague was faster, you're told who has it.
- **Priorities with P-levels**: Urgent (P1), High (P2), Normal (P3), Low (P4).
- **CC and follower notifications** (optional, under Settings → Notifications).
- New-ticket notifications show how long ago the ticket was created ("Created 09:48 · 7 min ago").
- The Settings window follows the chosen appearance (Neon dark, Terminal, Clean light, or Windows light/dark).
- New icon: the watching eye.
- Smaller installer (about 17 MB). Setup installs the .NET 10 Desktop Runtime if it's missing; that asks for admin rights once.
- Settings: the group list no longer jumps when you tick or untick a group, monitored groups that no longer exist in Zendesk are flagged, and the drop-downs have more room.
- Update checks run once a day instead of every 6 hours.

## 1.1.1

- Only notify about tickets that are really unassigned. Zendesk search can list tickets that were assigned long ago; they're now checked first.

## 1.1.0

- First release with an installer and automatic updates.
