# Zendesk Watcher – overview for the Zendesk owner

**What it is:** a small Windows tray app for Optimizely support agents. It notifies the agent about:

- new unassigned tickets in groups they choose,
- changes on their own tickets (and, optionally, tickets they're CC'd on or follow),
- optionally ("Help colleagues"), a customer reply on a colleague's open ticket while that colleague is outside
  working hours, so someone can step in.

It's a notification layer on top of Zendesk; all work is still done in Zendesk itself.

## It works like Zendesk open in the agent's browser

| | Zendesk in the browser | Zendesk Watcher |
|---|---|---|
| Sign-in | Agent signs in via Okta | The same Okta sign-in, in an embedded Microsoft Edge (WebView2) window |
| Permissions | The agent's own | The same – the agent's own session, nothing more |
| How data is read | Zendesk REST API (`/api/v2`) plus internal APIs | Only the public, documented REST API (`/api/v2`), same session |
| Setup in Zendesk | None | None – no API token, OAuth app, or admin change |
| Servers / third parties | None | None – runs only on the agent's PC, talks only to Zendesk |
| Changes to tickets | Anything the agent does | Only **Take it** (assigns an *unassigned* ticket to the agent) on an explicit click; everything else is read-only |

## Load on Zendesk

Checks run every 60 seconds by default (the agent can choose 30 s – 1 h). Per check:

| What | Requests |
|---|---|
| New unassigned tickets | 1 search per monitored group + 1 ticket lookup (`show_many`) |
| The agent's own tickets | 2 (30 most recently updated assigned tickets + `show_many`) |
| CC / follower tickets (optional, off by default) | 3 (30 most recent CC'd and followed tickets + `show_many`) |
| Help colleagues (optional, off by default) | 1 search per chosen group, only tickets updated since the last check |
| Only when a ticket changed | 1 audit request per changed ticket (newest first, stops at already-seen changes) |

**Measured** on an agent's PC with 2 groups, CC tracking and Help colleagues all switched on: **3–11 requests per
minute** (11-minute averages; the higher one includes opening the Settings window). With default settings it's about
5. A check takes a few seconds. Also: user names are cached, the group list, the agent's group memberships and
their open tickets (for group suggestions), and, with Help colleagues on, the members of the covered groups (1 request per group, to choose which colleagues to cover) are loaded at most twice an hour, when Settings is opened, and the update check is once a day (GitHub, not Zendesk). Zendesk rate limits are respected
(on HTTP 429 the app waits as instructed by `Retry-After`), and the app logs its own request count every 10 minutes.

For comparison, an open Agent Workspace tab also polls continuously (views, ticket counts, notifications, plus
internal APIs and live-update connections), so the Watcher adds about the load of one more agent tab, or less.

## Data handling

- **Read from Zendesk:** the API returns complete objects; the app keeps only what it needs, in memory: ticket ID,
  subject, status, priority, group, assignee, CC/follower IDs, timestamps, change types, user names, roles and time
  zones, and – for notifications – the first 140 characters of a comment (the agent can turn that off).
- **Stored on disk** (only in the agent's Windows profile, `%LOCALAPPDATA%`):
  - settings (subdomain, chosen groups, preferences),
  - ticket IDs, status and timestamps already seen (no subjects or other ticket content),
  - a log with ticket IDs, names and change types, but never comment text,
  - the embedded browser profile with the login cookies. API responses are not cached (`no-store`), and the cache
    is cleared at every start.
- **Nothing is sent anywhere except to Zendesk.** No telemetry, no analytics.
- **Colleagues' time zones** (for "Help colleagues") come from their Zendesk profiles, which agents can already see.
  "Outside working hours" is the agent's own setting (default Monday–Friday 08:00–17:00 in the colleague's time zone);
  it is not tracked or stored.

## Technology

- .NET 10 Windows desktop app (Windows Forms); Microsoft Edge WebView2 for sign-in and API calls, kept small (low
  memory mode, suspended between checks).
- Per-user installer and automatic updates with Velopack, from the GitHub releases of
  [optidada/zendeskwatcher](https://github.com/optidada/zendeskwatcher). The source code is public there; only the
  owner can publish releases.

**Contact:** Daniel Dahlin (Optimizely Support).
