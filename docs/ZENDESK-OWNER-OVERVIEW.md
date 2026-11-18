# Zendesk Watcher – overview for the Zendesk owner

**What it is:** a small Windows tray app for Optimizely support agents. It notifies the agent about:

- new unassigned tickets in groups they choose,
- changes on their own tickets (and, optionally, tickets they're CC'd on or follow),
- "Help colleagues" (on by default, the agent can switch it off): a customer waiting for an answer on a colleague's
  ticket while that colleague is outside working hours (and hasn't been active in Zendesk in the last 30 minutes), or
  in their working hours but with no activity in Zendesk for 3 hours, so someone can step in,
- "Catch up": when the app starts, when the agent's working day starts, and on request, what still needs attention
  since they last worked: customers still waiting, SLA timers past or due today, tickets still unassigned, and the
  agent's own open tickets by status.

It's a notification layer on top of Zendesk; all work is still done in Zendesk itself.

## It works like Zendesk open in the agent's browser

| | Zendesk in the browser | Zendesk Watcher |
|---|---|---|
| Sign-in | Agent signs in via Okta | The same Okta sign-in, in an embedded Microsoft Edge (WebView2) window |
| Permissions | The agent's own | The same – the agent's own session, nothing more |
| How data is read | Zendesk REST API (`/api/v2`) plus internal APIs | Only the public, documented REST API (`/api/v2`), same session |
| Setup in Zendesk | None | None – no API token, OAuth app, or admin change |
| Servers / third parties | None | None – runs only on the agent's PC, talks only to Zendesk |
| Changes to tickets | Anything the agent does | Only assigning an *unassigned* ticket, on an explicit click: **Take it** (to the agent) or, after a confirmation, to a colleague the agent picks; everything else is read-only |

## Load on Zendesk

Checks run every 3 minutes, and every 10 minutes while the agent is away (screen locked, or no mouse or keyboard
input for 10 minutes), with one check right away when they're back. Fixed; not configurable. Per check:

| What | Requests |
|---|---|
| Re-reading tickets as they are now | 1 `show_many` for all of the below together (it also brings the SLA timers) |
| New unassigned tickets | 1 search per monitored group (the tickets found, and new tickets whose notification may still be up, are re-read in the shared `show_many`, to close a notification once someone takes the ticket) |
| The agent's own tickets | 1 (30 most recently updated assigned tickets) |
| CC / follower tickets (optional, off by default) | 2 (30 most recent CC'd and followed tickets) |
| Help colleagues (on by default) | 1 search per chosen group, only tickets updated since the last check |
| Only when a ticket changed | 1 audit request per changed ticket (newest first, stops at already-seen changes); for colleagues' tickets only the comments (`/tickets/{id}/comments`, 20 per page), which is all that check needs |
| Only when a customer wrote on a colleague's ticket | the colleague's recent activity: 1 search (their tickets updated in the last 3 hours) + up to 5 audit requests; remembered for 15 minutes per colleague. Skipped for colleagues without a time zone |
| Only for a notification that shows a client or an SLA deadline | the client's organization (once per session), and for a ticket found by search 1 `show_many` for its SLA timer (tickets read with `show_many` already carry it) |
| Only when a snoozed notification is due | 1 ticket lookup, to check it still needs attention (e.g. still unassigned), plus 1 audit request to see whether it was answered since |
| Only for notifications that waited long (15 min, e.g. while the agent was away) | 1 ticket lookup (`show_many`, up to 100 at once), plus 1 audit request per notification about a ticket change (comments only for colleagues' tickets) |
| Catch up (when the app starts, when the working day starts, and on request) | 1 `show_many` for the agent's own and CC'd tickets, 1 search per covered group, the activity lookup above once per colleague, then 1 comments request per ticket updated since the agent last worked (own and CC'd tickets, and colleagues' tickets only when that colleague is away), 1 per own Escalated ticket; up to 3 at a time. Comments instead of the full history: who wrote what is all Catch up needs (measured: about 5 s instead of 7 for the same result). Unassigned tickets come from the check that just ran; when there are more than 3, they're only counted, not looked up one by one |
| Only when the agent opens "Who could take it" (▾ next to Take it) | 1 ticket lookup, the group's members and 1 search export for its assigned open tickets (up to 1,000 per request; shared with Insights and reused for 15 minutes), and the activity lookup above for up to 8 colleagues (least busy first, remembered 15 minutes), plus 1 search export per active one for their unsolved tickets in all groups (reused for 15 minutes), and 1 search export for the client's tickets in the group in the last 90 days, for "handled 12 of their tickets" (reused for 24 hours; up to 7 days it's used at once while read again in the background) |
| Once at start | the custom ticket statuses (`/api/v2/custom_statuses.json`), to show them by name |
| Experimental features (off by default) | when switched on: 90 days of tickets per monitored group (1 search, up to 5 pages each), every 6 hours |

**Measured** on an agent's PC with CC tracking and Help colleagues switched on (the app's own log, 10-minute
averages, November 2026): about **3 requests per minute** while working, fewer while away; more for a moment while
the agent has the Settings window or Insights open or runs Catch up. Fewer groups and default settings mean fewer. A check takes a few seconds. Also: user names and organizations are cached for the session. The lists the
Settings window shows (the group list, the agent's group memberships and open tickets for group suggestions, the
members of the covered groups, and the clients of the monitored groups from 90 days of tickets, read with search export: up to 1,000 per request,
with the names of the agents those tickets are assigned to: from the 7-day user cache, unknown ones 100 per request; after the first
read only the tickets updated since the last one are asked for, usually 1 request, and only new clients' names are looked up) are loaded when needed
and reused for 24 hours. Insights → Team workload reads the members and open tickets of the chosen monitored group (or all of them), at most
every 10 minutes while that page is open: 1 members request and 1 search export per group (Zendesk's export endpoint
for bigger result sets: up to 1,000 tickets per request instead of 100), plus the custom statuses; a group read for "Who could take it" in the last 5 minutes isn't read again. The update check is once a day (GitHub, not Zendesk). Zendesk rate limits are respected:
on HTTP 429 the whole app pauses (every request, not just the limited one) for the time `Retry-After` asks. It also
limits itself, without needing Zendesk's headers: at most 60 requests in any minute per app (normal use is 3-4); a
request over that waits until the minute rolls on, it isn't dropped. The app
logs its own request count every 10 minutes. All its requests are small, read-only reads, apart from the assign on an
explicit click. Planned with the move to an OAuth client: pausing as soon as more than half of the account's
per-minute limit is in use (from Zendesk's rate-limit headers, which the current browser-session requests don't get).

## Data handling

- **Read from Zendesk:** the API returns complete objects; the app keeps only what it needs, in memory: ticket ID,
  subject, status (and custom status names), priority, group, assignee, CC/follower IDs, timestamps, change types,
  user names, roles, time zones and email addresses (only to tell the agent's own company from customers), and – for
  notifications – the first 140 characters of a comment and the first 300 of a new ticket's first message (the agent
  can turn both off; then the first message isn't even read). With the Experimental features
  switched on (off by default), ticket and comment text is also read by rules and a small language model that runs on
  the PC; it is never sent anywhere.
- **Stored on disk** (only in the agent's Windows profile, `%LOCALAPPDATA%`):
  - settings (subdomain, chosen groups, preferences), with a backup copy of the previous version,
  - ticket IDs, status and timestamps already seen (no subjects or other ticket content),
  - the lists the Settings window shows (groups, group members, clients: names, and per client the agents its tickets are assigned to with a count), reused for 24 hours (clients: updated with what changed when Settings opens after 15 minutes; per ticket of the last 90 days only its number, client, assignee and creation date),
  - a log with ticket IDs and subjects, names and change types, but never comment text,
  - the embedded browser profile with the login cookies. API responses are not cached (`no-store`), and the cache
    is cleared at every start.
- **Nothing is sent anywhere except to Zendesk.** No telemetry, no analytics. The only exception is "Send feedback",
  which the agent writes and sends themselves (an email from their own Outlook, optionally with the log and settings).
- **Colleagues' time zones** (for "Help colleagues") come from their Zendesk profiles, which agents can already see.
  "Outside working hours" is the agent's own setting (working days and hours, default Monday–Friday 08:00–17:00, in
  the colleague's time zone). Whether a colleague was active lately is read from their own recent ticket changes
  (only when a customer is waiting on one of their tickets), kept in memory for 15 minutes, and never stored.

## Technology

- .NET 10 Windows desktop app (Windows Forms); Microsoft Edge WebView2 for sign-in and API calls, kept small (low
  memory mode, suspended between checks).
- Per-user installer and automatic updates with Velopack, from the GitHub releases of
  [optidada/zendeskwatcher](https://github.com/optidada/zendeskwatcher). The source code is public there; only the
  owner can publish releases.

**Contact:** Daniel Dahlin (Optimizely Support).
