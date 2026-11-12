# Zendesk Watcher – overview for the Zendesk owner

**What it is:** a small Windows tray app for Optimizely support agents. It notifies the agent about new unassigned
tickets in chosen groups, changes on their own tickets (and optionally tickets they're CC'd on or follow), and,
optionally, customer replies on colleagues' tickets when the colleague is off work. It's a notification layer on top
of Zendesk; all work is still done in Zendesk itself.

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
| The agent's own tickets | 2 (assigned list + `show_many`) |
| CC / follower tickets (optional, off by default) | 3 |
| Help colleagues (optional, off by default) | 1 search per chosen group |
| Only when a ticket changed | 1 audit request per changed ticket |

That is **about 5 requests per minute with default settings, about 10 with everything on**, plus one per changed
ticket. User names are cached; settings data (groups) at most twice an hour; one update check per day (GitHub, not
Zendesk). Zendesk rate limits are respected (on HTTP 429 the app waits as instructed by `Retry-After`). The app logs
its actual request count every 10 minutes.

For comparison, an open Agent Workspace tab also polls continuously (views, ticket counts, notifications, plus
internal APIs and live-update connections), so the Watcher's load is in the same range as one more agent tab, or
lower.

## Data handling

- **Read from Zendesk:** the API returns complete objects; the app keeps only what it needs, in memory: ticket ID,
  subject, status, priority, group, assignee, CC/follower IDs, timestamps, change types, user names and time zones,
  and – for notifications – the first 140 characters of a comment (can be turned off).
- **Stored on disk** (only in the agent's Windows profile, `%LOCALAPPDATA%`): settings (subdomain, chosen groups),
  ticket IDs/status/timestamps already seen, a log without comment text, and the browser profile with the login
  cookies. API responses are not cached.
- **Nothing is sent anywhere except to Zendesk.** No telemetry, no analytics.
- **Colleagues' time zones** (for "Help colleagues") come from their Zendesk profiles, which agents can already see.

## Technology

- .NET 10 Windows desktop app (Windows Forms), Microsoft Edge WebView2 for sign-in and API calls.
- Per-user installer and automatic updates with Velopack, from the GitHub releases of
  [optidada/zendeskwatcher](https://github.com/optidada/zendeskwatcher). Source code is public there; only the owner
  can publish releases.

**Contact:** Daniel Dahlin (Optimizely Support).
