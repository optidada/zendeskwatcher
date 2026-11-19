# Changelog

Each release takes its notes from the section with its version number. The first bullet points also show in the
app's "Update ready" notification, so put the most interesting changes first.

## 1.9.5

- **The Optimizely theme follows the Optimizely brand**: light green-grey and white instead of dark green, dark green text, and buttons, toggles and choices in dark green with lime, like the logo. Notifications are white with the brand's colors on the edge and buttons: lime for P2, pink for P1, light blue for new tickets, green for your own.
- **Easier on the eyes**: secondary text in the Optimizely theme is a calm grey-green, and no gradients or glow.

## 1.9.4

- **Updates come from a new download place**: new versions and the installer now live at github.com/optidada/zendeskwatcher-releases. Nothing changes for you; the app keeps updating itself as before.

## 1.9.3

- **No notification lost when Zendesk hiccups**: if a request fails in the middle of a check (Zendesk busy, or the app's browser restarting), whatever wasn't notified yet comes with the next check. Before, such a ticket could be skipped for good: new tickets, "Already taken", tickets newly assigned to you or CC'd, and customer replies on colleagues' tickets.
- **The copy buttons show their tooltips**: point at the copy icons on a notification to see what each copies.
- **A reply without text says so**: a customer who sends only a screenshot or a file shows "sent a file or image" instead of empty quotes.
- **Steadier connection**: when the app's hidden browser stops answering, it restarts once instead of several times in a row, and a browser that couldn't start tries again with the next check instead of waiting for an app restart. A colleague's name that couldn't be read is asked again later instead of showing "User 123".
- **Notifications always close**: a notification on its way out can no longer get stuck on screen, and closed ones free their memory. The Catch up spinner on the Account page stops when you get signed out.
- **Less customer data in the log**: the log names tickets by number and says "the customer", never subjects or customers' names (the log goes along with Send feedback). It also notes when a notification shows, is clicked and closes, to help find problems.
- **Less memory** for Team workload (Insights) and "Who could take it": only the few details they count are kept.

## 1.9.2

- **How it works explains what's on a notification**: a new topic, "On a notification", right after Notifications, covers Take it, who could take it instead, "Taken by Anna", the copy icons and Options ▾ on the client line. Groups now just covers which tickets you hear about, and Summaries comes right after.

## 1.9.1

- **"Working late?" sounds like an app message**: it uses the App messages sound and time on screen (Look & sound), not the one for your tickets.

## 1.9.0

- **Notifications on the monitor you're working on**: with two or more monitors, notifications (and the pill, ticker and glow) appear where your mouse is, not always on the main monitor. The ones that follow join them there, so a stack never splits. They're drawn sharp and the right size for each monitor's own scaling, and their menus (Options ▾, ▾ next to Take it) stay on the same monitor.
- **"Who could take it" says who knows the client**: each suggestion under ▾ next to Take it adds "handled 12 of their tickets" when that colleague has the client's tickets in the group (last 90 days). It counts in the order too: knowing the client is worth up to 3 open tickets, so someone who knows it comes first unless they're clearly busier, and is never skipped for being a bit busier.
- **Who usually handles a client**: point at a client's name on the Clients page (under Groups) and its card says who their tickets in that group are assigned to, most first, and you: "Usually: Maria S. 12 · Alex K. 5 · you 3". Handy for a ticket from a client you don't know: you can see who to ask. The client list loads once more after the update (about 40 s for a busy group), then in seconds.
- **Clients you've worked with**: the Clients page can show only the clients you've had a ticket from in the last 90 days, or only the others (All · Worked with · Not worked with), and yours show "you 6" after their name. Handy for setting up: show "Not worked with", untick the group, tick back the few to keep.
- **Clients page loads in seconds and is up to date**: after the first time, only the tickets that changed since the last load are read (a busy group took ~40 s, now usually one quick request), each time you open the page (if 15 minutes have passed), so new clients from today's tickets are there. The list shows at once while it updates. Reload still reads everything.
- **Options ▾ on your own tickets' notifications**: make a client Important from a "Ticket updated" notification too. Muting isn't offered there: your own tickets always notify.
- **"Who could take it" suggests people who work the group** (▾ next to Take it): only colleagues with unsolved tickets in the ticket's group are offered, so a Tier 2 ticket suggests Tier 2 agents, not members who work elsewhere or managers.
- **Quicker the second time**: the suggestions' workloads are remembered for 15 minutes (until someone assigns a ticket), so opening ▾ again reads almost nothing from Zendesk.
- **A safety limit on requests**: the app never sends more than 60 requests to Zendesk in a minute (normal is 3-4). If a bug ever made it run away, it slows down instead of using the team's shared Zendesk budget.
- **Fewer empty client lists**: when Zendesk has a hiccup ("database timeout") while the Clients page loads a big group, the app tries again after a few seconds instead of giving up, and a group is loaded only once at a time.
- **"Working late?" once a day**: it no longer comes again after the app restarts or updates the same evening.
- **A second click closes the menu**: clicking Options ▾ or the ▾ next to Take it again closes its menu instead of opening it once more.

## 1.8.24

- **Fairer "Who could take it"** (▾ next to Take it): someone's open tickets now count in every group, not just the ticket's. Before, a busy colleague with nothing in that group showed as "nothing open".
- **No managers suggested**: people with no unsolved tickets at all aren't offered.
- **Waiting tickets shown**: each suggestion also says how many pending, on-hold, escalated and scheduled tickets they have, like "2 open, 5 waiting".

## 1.8.23

- **Updates install by themselves**: when the app starts, a new version installs in a few seconds and then says what's new. "Restart to update" is only needed for a version that comes out while the app runs.
- **No "already running" after logon**: Windows starts its startup apps one by one, sometimes minutes after logon. If you've opened Zendesk Watcher yourself by then, the late start now ends quietly.
- **Fewer update checks**: once at start and once a day, instead of an extra check two minutes after start.

## 1.8.22

- **New sounds: Americana and Nature**: dark americana pieces (banjo road, slide guitar, hollow fingerpicking) and nature sounds (a quiet stream, wind chimes, a distant bird). Softer than the retro sounds. Choose them on Look & sound.

## 1.8.21

- **Colleagues' tickets show why, always**: who wrote and when, then why you're asked (off work, last active), then the message. A long message is now what gets cut, never the reason.
- **"+N more" in Catch up opens the rest**: when you have more tickets to work on than fit, click "+N more" to see all of them in Zendesk.

## 1.8.20

- **Catch up in seconds**: about 5 seconds instead of 2–3 minutes. It checks first whether a colleague is away before reading their tickets, reads only the messages instead of each ticket's full history, and does several things at once.
- **Complete Clients page**: the client list now comes from all of a group's tickets in the last 90 days, not just the first 500, so busy groups no longer miss clients.
- **Take it is quicker**: assigning a ticket (to yourself or a colleague) takes one round trip to Zendesk instead of two.
- **Lighter on Zendesk**: the colleague check reads only the messages, and a ticket's history isn't read again while the ticket hasn't changed.

## 1.8.19

- **Team workload on Insights**: pick a group to see who's busy right now, as a bar per person with their open, urgent, escalated and waiting tickets. Click "Workload ▼" to see the least busy first. People who are off work aren't shown.
- **Insights opens instantly**: each group's workload is remembered, so it shows right away and refreshes in the background.
- **"Who could take it" (▾ next to Take it) is faster**: it shares the group's workload with Insights for up to 5 minutes. The ticket and colleagues' activity are still checked fresh.
- **Fairer "acted on" in Insights**: copying just the ticket number and snoozing now count too.
- **Clearer names**: Insights has the cards Your tickets, Team workload and Notification usage. On Behavior, "On screen" is now Display time and "Skip old news" is Already handled.
- **Lighter on Zendesk**: big groups' open tickets take one request instead of up to ten.

## 1.8.18

- **Catch up once a day**: it comes by itself at the first start of the day (or when your working day starts), not again after a restart or an update. Ask for it any time as before (tray menu, Ctrl+Alt+Z).
- **New tickets show sooner**: new unassigned tickets are also checked halfway between the regular checks, so they show within about 1½ minutes.
- **"Taken by Anna"**: when someone takes a new ticket (or it's solved) while its notification is up, it says so for a few seconds before closing.
- **A card for each client**: point at a client's name on the Clients page to see its tickets in the last 90 days and its products, tidy even with many products.
- **Priority levels on How it works**: what P1–P4 mean, as a reminder when a client's priority doesn't match the case.
- **Lighter on Zendesk**: names, the group list and the Priority guess history are remembered for a week, a colleague's activity takes one request instead of six, and starting the app asks Zendesk for much less.
- The Colleagues page no longer loads the same group members several times at once.

## 1.8.17

- **Don't miss a P1, even when it was taken within a minute**: Notifications → New tickets → "Also when someone took it before I saw it" (Off · P1 · P1, P2 · All, off by default). Tickets someone took between two checks come as "Already taken", saying who took it and how fast, and whether it's already solved.

## 1.8.16

- **Copy just the ticket number**: a new icon at the top right of ticket notifications (the copy icon with a #) copies only the number, e.g. 123456. Pointing at either copy icon says what it copies.
- The guide and README show the new Settings pages and notifications.

## 1.8.15

- **Priorities per group for new tickets**: Notifications → New tickets lets each monitored group notify for All, P1–P3, P1 and P2, or P1 only. A quiet ticket raised to a chosen priority while still unassigned comes as "Priority raised".
- **See a client's products**: point at a client on the Clients page to see the products Zendesk lists for it, and filter by product (the row says which product matched). Zendesk's list is often out of date, so it's a hint, never a rule.
- **One way to choose priorities**: New tickets, Ticket changes and Colleagues all use the same buttons, and priorities show as P1–P4 tags only.
- **Clearer when signed out**: a "Not signed in" banner with a Sign in button, and Groups, Clients, Colleagues and SLA greyed with a lock in the sidebar.
- The Groups page is only about which groups you monitor again.

## 1.8.14

- **"Client" everywhere**: the app says client instead of customer, as the team does (Colleagues page, Catch up, How it works, SLA and more).

## 1.8.13

- **Options for a whole group of clients**: Options ▾ on a group's row on the Clients page sets Only urgent, Important, a mute or a sound for all its clients at once.
- **A guide with screenshots** on GitHub shows every notification and Settings page (linked from the install guide).
- The Groups list starts from the top after Save, like Clients; How it works topics have their titles centered.

## 1.8.12

- **Calmer notifications**: who it's from and how long ago on one line, the reply deadline at its right end, and what they wrote in quotes. No more "Client:" or "replied:", and a title never ends with one word alone on its line.
- **Mute a client until your shift ends or your next shift starts** (instead of for a day), from Options ▾ on a notification or on the Clients page.
- **Ctrl+Alt+Z opens Catch up** from anywhere.
- **Working late?** Notifications → Working hours: "Only notify me during my working hours" can now also notify while you're active in Zendesk outside them.
- **How it works follows the Settings pages**: one topic per page, each starting with what it's for, then where to change it and the details, with links.
- "Customer waiting" is now "Colleague's ticket"; Options ▾ on a notification replaces "Mute ▾"; the Clients summary also counts "only urgent"; Clients start from the top after Save and open groups stay open; Catch up puts "Your tickets:" on its own line; times follow your Windows clock everywhere.

## 1.8.11

- **"Update ready" lists every version you'll get**: skipping a few updates, you see the main change of each version in between, not only the newest one. Works from the next update on.

## 1.8.10

- **Catch up is easier to read**: an empty line before "To work on", so the tickets to start with stand apart.
- The ▶ next to a sound says "Stop" while it plays.

## 1.8.9

- **Volume stands on its own**: at the top of Look & sound → Sound, as buttons (25% to 100%), so it doesn't look like one more kind of notification. It's for all the app's sounds.

## 1.8.8

- **Sounds are quieter**: a new Volume on Look & sound, 50% to start with, so a notification doesn't startle you with headphones on. It only affects this app; ▶ plays at the chosen volume, and shows ■ while playing (click it to stop).
- **Filter Groups and Clients**: type to show only the matching groups or clients; ✕ or Esc clears it. While filtering, a group's box applies to the clients shown.
- **Settings can be maximized**: the Groups and Clients lists grow with the window.
- **Lighter on Zendesk**: the app checks every 3 minutes (every 10 while you're away, and right away when you're back) and combines its requests, about a third of before. If Zendesk ever says it's busy, the whole app pauses until it may go on.
- **See where you changed something**: pages with unsaved changes get a dot in the sidebar.
- How it works now starts each topic with what it does for you. Notifications → Colleagues says "Change my work hours".

## 1.8.7

- **Clients are easier to customize**: one client per line, each with "Options ▾" (important, only urgent, its own sound, mute for a while). Groups → Clients.
- **Bot updates stay quiet**: the automatic "your request has reached Premium Support" reply and group move no longer notify on your own or CC'd tickets, nor do other changes by Zendesk triggers or service accounts. Status changes still do. Turn them back on in Notifications → Which changes: Automatic updates (bots).

## 1.8.6

- **Taken tickets close by themselves**: when a colleague takes a new ticket (or it's solved) while its notification is still up, the notification closes within a minute.

## 1.8.5

- **New tickets show what the customer wrote**: the start of their first message, under their name. Turn off Look & sound → Show the comment text and it isn't even read.
- **Try it picks a different sample each time**, with funnier made-up customers, clients and tickets.
- **Ticket lists stay tidy**: in "Group added" and "several new tickets", each ticket keeps to one row and long subjects end in "…".
- "Now watching" is now called "Group added" (How it works explains it), and the search box on Groups and Clients empties after Save or Discard.

## 1.8.4

- **Who could take it**: the ▾ next to Take it on a new-ticket notification lists up to 3 colleagues in the ticket's group who are in their working hours and active in Zendesk, least busy first. Pick one to assign the ticket to them (you're asked first).
- **Insights: Your team** gets a Solved column (solved or closed in the last 7 days); "Waiting" is now "Pending" (Pending and On-hold), and names are shortened so no column is cut off.
- **Smoother Insights**: the page scrolls smoothly, without smudges, and its cards have clearer titles.
- In Pill mode, an open menu on a notification keeps the pill open.

## 1.8.3

- **Catch up: "To work on"**: under your tickets, up to 5 of your New, Open and In progress tickets by name, most urgent first, then the nearest SLA. Click a number to open the ticket.
- **One glow at a time**: several notifications arriving together glow at the screen edge once, not one glow after another.

## 1.8.2

- **Smaller and lighter**: the app takes about 23 MB less on disk (a Windows library it no longer needs), and sounds now play through Windows' simple audio output, which closes as soon as a sound ends instead of keeping Windows' media player loaded.

## 1.8.1

- **Clients stay put**: changing a client (Important, Only urgent, Muted, own sound) no longer moves its row. The list sorts itself again after Save or Discard, or the next time you open the page, as on the Groups page.

## 1.8.0

- **Catch up greets you**: when the app starts and when your working day begins. One line per thing that needs you, number first: customers waiting, SLAs past or due today, unassigned tickets, Escalated tickets the customer hasn't heard about, quiet Pending ones. Then your tickets by status. It replaces the start summary and is much faster with many unassigned tickets.
- **New ways to show notifications** (Look & sound → How they appear): a pill at the top, a ticker at the screen edge, or "Achievement unlocked". Optional screen-edge glow for Urgent tickets, important clients and SLA breaches.
- **Colleagues: activity counts**: no notification when an off-work colleague is clearly working in Zendesk; one when a colleague in their working hours has done nothing for 3 hours, with the reason.
- **Priority tags in the text**, and a clearer priority guess (Experimental): "Set as P3 · looks like P1", or "Set as P1 · probably not".
- **Guided first start**: Settings leads new users through sign-in, groups, working hours and colleagues.
- **Send feedback in the app**: write it in a small window; your Outlook sends it, with the logs if you like.
- **Insights → Your team**: In progress and each escalated status get their own column.
- **Easier to read**: better contrast in every theme, links stand out from text, and messages that aren't about one ticket show all their text.
- **Lighter**: notifications counting down use much less CPU, Settings → Save no longer reloads the ticket history, and fewer Zendesk requests.

## 1.7.3

- **Lists kept after signing in again**: your groups, clients and colleagues stay in Settings when Zendesk asks you to sign in again. They're only loaded again for a different account, or when you click Reload.
- **Clients ready right away**: they load in the background after you sign in, so the Clients page doesn't have to wait.
- **Refreshed quietly**: lists over a day old stay in view while fresh ones load. If a group can't be loaded, its last list stays too.
- **Fix**: while signed out, lists older than a day disappeared instead of showing as last loaded.

## 1.7.2

- **Always-visible tray icon**: the icon now sits next to the clock instead of behind the ^ arrow. If you hide it in Windows Settings, it stays hidden.
- **Links in notifications**: ticket numbers (also in the title) and "Settings → Groups" stand out and open that ticket or Settings page. In the Terminal theme, links are cyan.
- **No groups chosen yet?** The Account page points you to the Groups page, and the start summary reminds you each time the app starts.
- **Fix**: in Settings, the first click on a button sometimes moved the page a bit instead of doing anything.

## 1.7.1

- **What's new after an update**: one notification with the highlights since the version you used before, also after a big jump. A click opens the release notes.
- **Priorities as colored tags**: P1 red, P2 orange, P3 and P4 in the tick boxes, the SLA table, Insights and notification headings.
- **Clients fold per group**: one tick box per group mutes or unmutes all its clients, with a count. Groups with changes start open, and search opens the right group.
- **Ticket lists in notifications**: the ticket numbers look like links and open that ticket; "See all" opens the whole list in Zendesk.

## 1.7.0

- **New Native theme**: looks like Windows 11, a bit edgier, and follows your Windows light/dark mode and accent color. For the Settings window and for notifications. It replaces the Windows notification style, which is gone.
- **Simpler and faster Settings**: fewer options, shorter texts, and references like "Notifications → Working hours" are links. Pages open up to four times faster; Insights opens again instantly.
- **Colleagues by region**: tick or untick a whole region, and newcomers follow it. A customer's reply on a colleague's ticket now notifies right away, whatever the status.
- **Insights → Your team**: open, P1/P2, escalated, scheduled and pending tickets per colleague in your region. Plus an "Acted on" column: how many notifications you opened, took or copied.
- **Short / Normal / Long** sets how long all notifications stay. Working hours are one bar to drag, and times follow your Windows clock (12-hour with AM/PM too).
- **List notifications**: click a ticket in "Now watching" or "New tickets" to open it, or anywhere else to see them all in Zendesk.
- **Tray icon**: green while online, gray when not.
- **Fewer settings**: checking every minute, notifications taking turns, the trust time for "In progress" and "Scheduled", and Snooze's "always" are now fixed, and How it works explains them.
- **Experimental**: only Priority guess and Quiet thank-yous are left.
- **SLA**: Zendesk's own timers now win over the team's defaults, and targets are marked ≈ (from Zendesk) or ✎ (yours).
- **Catch up** always answers when you ask, also twice in a row. With Peek, app messages and the welcome summary slide out by themselves.
- **Account** shows how long you've been signed in.

## 1.6.0

- **New: Experimental page** with smart extras, each off until you switch it on: a priority guess ("Looks like P1: site down · frustrated tone"), a "Same issue?" alert when several new tickets read alike, a similar solved ticket on new tickets, "Fits you", quiet thank-yous and this week's topics.
- **All on your PC**: a small English language model (about 8 MB) comes with the app and only loads while one of these is on. Ticket text never leaves your PC, and Zendesk is never changed.
- **Catch up no longer fails** on tickets with a chat or call in their history.
- **Settings sidebar tidied**: General comes before Insights, Updates & feedback is its own page under General, and Experimental is last.

## 1.5.0

- **SLA deadlines on notifications**: in red bold, counting down, e.g. "First reply due in 25 min" or "Reply overdue by 1 h". Zendesk's own SLA timer when the ticket has one; otherwise the target for the client's SLA type (from Zendesk) and the priority. New page **Notifications → SLA** with the targets, color coded: default, estimated from Zendesk's timers (also for new SLA types), or your own.
- **Settings pages fit the window again** at display scaling above 100%: since 1.4.4, pages opened later were too narrow and cramped, and some tiles were cut off.
- **Groups tick boxes no longer disappear** now and then (Windows swapped in its own, faint ones after styling the list).
- **Scheduled tickets with nobody on them stand out**: "New ticket · Scheduled", marked in ticket lists, and counted in the morning summary, Catch up and "While you were away".
- **Easier to read summaries**: the morning summary and the welcome are short rows instead of long sentences.
- **Fewer repeats**: a reply that reopens a ticket no longer also says "Status Pending → Open".
- **Soft chime**: a new, very quiet sound, the default for new tickets on new installs (Look & sound).
- **How it works**: topics fold open with a click, so you find what you're looking for.
- **Insights** suggest getting fewer notifications rather than showing them longer, count copying for Teams as using a notification, and say so when there's nothing to change.
- The Windows notification style is marked "Fewer features" on Look & sound; the installer is a little smaller.

## 1.4.4

- **Settings opens much faster**: about 0.1 s instead of 0.75 s, by building each page the first time you open it; no white flash, less memory while it's open.
- **Faster checks**: the parts of a check and the searches per group now run side by side (up to 4 requests at once), so notifications arrive sooner. Settings loads Clients and colleagues the same way.
- **Tick boxes on the Groups page are clearly visible in every theme**: drawn in the theme's colors like the other lists, instead of Windows' own boxes that were faint on dark themes. Drop-downs match the theme too.
- **Keyboard in the Clients and Colleagues lists**: Tab into the list, arrow keys move, Space ticks or unticks (or All / None), Enter opens a client's menu.
- **Less memory and work per check**: the built-in browser now passes on only the ticket fields the app uses (not the whole first message, custom fields and tags), often a fraction of the size.
- **The built-in browser does less in the background**: no component updates, sync, extensions or safety lookups, which the Zendesk and Okta pages don't need.

## 1.4.3

- **Update checks try again sooner**: if GitHub can't be reached or is busy, the app tries again after 15 minutes (then 30, 60 … up to 6 hours) instead of the next day, and says so in plain words.

## 1.4.2

- **Nothing pops up while you present**: in Zendesk Watcher's own styles, notifications now wait while you present, share your screen or use a full-screen app, and appear when you're done (several become one summary, outdated ones are dropped). Before, they turned into Windows notifications.
- **No sounds while presenting** or in a full-screen app, in every style, so nobody in a screen share hears a jingle.

## 1.4.1

- **Peek from the edge**: notifications wait as tabs at the screen edge; point at one to slide it out. Tabs wiggle when something new arrives and fill with color the longer a ticket waits; urgent and important ones come out by themselves (Look & sound → Several at once). New positions too: **Right center** and **Bottom center**.
- **Send feedback attaches your log**: a zip with the log, settings and app details. Classic Outlook attaches it; other mail apps show it, ready to drag in. Nothing is sent without you.
- **Clear customizations** on the Clients page puts the listed clients back to normal in one go.
- **Check now works every time**: it could wait until the next regular check (Catch up too). Both are greyed out in the tray menu while the app isn't checking.
- **Signed out**: Groups, Clients and Colleagues say so, show the lists as last loaded, and lock them until you sign in.
- **Discard no longer signs you out** of Settings. While signed in, the subdomain is locked (sign out to switch accounts); a connection problem says what to check instead of a raw error.
- **Settings opens faster**: while the app is checking, it shows who is signed in right away instead of asking Zendesk again. Cards stacked like a deck are all the same height, and only the front card counts down, so none disappears unseen behind it.
- Notifications listing several tickets show up to 5, then "… and 3 more". Shorter texts throughout Settings.

## 1.4.0

- **Clients**: a new page under Groups lists the clients of your groups. Click one to make it **Important ★** (always notifies, also outside your hours, and stays until you close it), hear **only urgent** tickets, **mute** it for a day, a week or for good, or give it its **own sound**. Everyone is normal by default, new clients too.
- **Mute ▾ on notifications**: the same choices right from a new-ticket or customer-waiting notification. Muting keeps a client's other choices; unmute brings them back.
- **Catch up on the Account page**, in its own card: "Catch up since yesterday 17:00" at any time, also right after setting up.
- **Where notifications appear**: also top center, left middle and center; and several at once can be **stacked like cards**, the newest in front and the ones behind fanning out (Look & sound).
- **Find instead of filter** on Groups and Clients: the whole list stays, typing jumps to the match and highlights it, Enter goes to the next.
- Buttons that load something (Sign in, Catch up, Reload, Check for updates) show a spinner while they work.
- Clicking a notification fades out a song that's still playing. Close is always at the bottom left.
- Insights suggests muting a client whose notifications you never open.
- Groups, colleagues and clients load once a day (kept between restarts; Reload fetches them again), so Settings opens fast. Ticking a group no longer moves it away from under the mouse; Save sorts the lists.
- Discard on the Groups page now also unticks groups ticked since the last save, and keeps the full group list.
- Settings is a little taller, so the Account page fits without scrolling.

## 1.3.21

- **How it works** also explains the "only while I'm working myself" switch for colleagues' tickets, and that Catch up brings up what it held back.
- The install guide links straight to the newest installer, and it, the README and the overview for the Zendesk owner describe Help colleagues, Catch up and the status rules.

## 1.3.20

- **"Scheduled" is trusted like "In progress"**: a customer's reply on a Scheduled ticket (often just "Thanks, see you Tuesday") only notifies once it has gone unanswered for the time you set on the Colleagues page (4 hours by default). Also in Catch up.

## 1.3.19

- **Catch up**: what still needs you since you stopped working last time: tickets still unassigned in your groups, and customers still waiting on your own, CC'd and colleagues' tickets. From the tray menu, the morning summary, and by itself when your working day starts (Notifications → Working hours).
- **Customer waiting, whatever the status**: Pending, On-hold, Escalated and your own statuses count too, and the status is shown. "In progress" is trusted for a while first (4 hours; change it on the Colleagues page).
- **How it works**: a new page at the end of Settings that explains, in plain words, when and why you get each notification.
- Colleagues from your own company on a ticket (also CC'd with an end-user account) no longer count as the customer; their public replies count as answers.
- Status changes on your tickets show your own statuses ("In progress → Escalated"); New → Open and Solved → Closed no longer notify.
- New installs: no sounds by default, and Help colleagues is on, using your monitored groups, with colleagues in your own region left out. Existing settings are kept.
- Colleagues page: clearer when no groups are chosen yet. Groups page: says "sign in" instead of showing a lone "(No group)".
- Tray: the menu's top line says just "Sign-in required"; the icon's tooltip says to click it. The sign-in window no longer flashes a blank Zendesk page.

## 1.3.18

- **One click on the tray icon opens Settings** (it was a double-click), like Teams and OneDrive. Right-click still shows the menu.
- The tray icon's tooltip says what to do: "Sign-in required – click this icon to sign in".

## 1.3.17

- **See who's working right now**: the Colleagues list shows each colleague's local time and a dot, green while they're working, an empty circle when they're off and you'll hear about their customers' replies. It follows the working hours on the same page.

## 1.3.16

- **The start summary reads like a short message**: what's waiting in your groups, your own and CC'd tickets, and which colleagues' tickets you cover, one line each.
- **A friendlier welcome** on first start: what Zendesk Watcher does, and where to sign in and pick your groups.
- **Release date** next to the version, in the Settings sidebar and on the General page.

## 1.3.15

- **Ticket notifications stay longer, by priority**: Urgent (P1) until you dismiss it, High (P2) 1 minute, Normal (P3) 30 seconds, Low (P4) 15 seconds. Change it under Notifications → Behavior → On screen. This replaces the earlier per-type display time.
- **Right now on Insights**: unassigned tickets per group, your tickets (open, pending, on hold) and CC / following, updated after every check. Click a number to open that list in Zendesk.
- **A clearer start summary** ("Good morning…"): every line has a label, and the colleagues line says what you get: customer replies on their Urgent (P1) and High (P2) tickets.
- **Safer settings**: saved through a temporary file with a backup. If the settings file is ever damaged, the backup is used and a notification tells you; the damaged file is kept.
- Settings: how long notifications stay is now on the Behavior page (cards "On screen" and "After waiting"); Look & sound only has sounds. Priorities are written the same everywhere: "Urgent (P1)", "High (P2)", "Normal (P3)", "Low (P4)".
- The welcome on first start lists the two steps: sign in, then pick your groups.
- Tray menu: the text sits in the middle of the hover highlight, with even space at the top and bottom. "Open log folder" moved to Settings → General only.
- Copied tickets read "#123 Title · High (P2)" instead of nested brackets.
- Fixed: the note under "Show the comment text" (Look & sound) was cut off.

## 1.3.14

- **Three new chiptune songs** with an Amiga feel: Quest, Turbo and Starfield (6–8 seconds). Sounds are now grouped: Silent first, then Normal, Retro (short blips) and Nostalgic (songs).
- **Retro**: a new theme in deep blue, white and orange, like a classic 80s home computer. All themes now fit on two rows.
- **The tray menu follows the app theme**, with more room between the lines.
- **Save, Discard and Save & Close** at the bottom of Settings when something has changed; just Close otherwise.
- The Groups list has a thin scrollbar and check boxes in the theme's colors.
- Insights only counts notifications that actually appeared: ones dropped while waiting (no longer needed) don't count.
- Fixed: input boxes (group filter, Zendesk address) had a gap in their right border at 125% display scaling; the ▶ sound buttons look the same in every theme.

## 1.3.13

- **Client first**: ticket notifications show "Client: Contoso" large and bold, and the priority moved into the top line ("NEW TICKET · P2 · SUPPORT TIER 2"). Names of people who replied are bold.
- **Coming back after a long time away**: more than 5 waiting notifications become one "While you were away" summary, and notifications that waited are checked first: dropped when the ticket was taken, solved, or a colleague already answered the customer. Settings → Notifications → Behavior.
- **Night**: a new theme for late evenings, with warm dark colors, dimmed text and no glow. For the app (General) and for notifications (Look & sound).
- **Working hours and days** for yourself (Notifications page), with a switch to only get notified while you're working. Colleagues' working days can include weekends too.
- **Notification size**: 100% to 175%, under Look & sound.
- "Customer waiting" isn't shown when a colleague has already replied to the customer.
- Several notifications are read top to bottom: the oldest is always at the top and counts down first, also in the top-right corner.
- Check boxes and option buttons follow the theme colors instead of Windows blue.
- Snoozed notifications are also dropped once someone has answered the customer or you've acted on your own ticket.
- Settings texts: "How often to check" (was Checking), and the Groups page says that Colleagues can use the same groups.

## 1.3.12

- **Requester and organization** on ticket notifications: "Jane Doe (Acme Corp)" on new tickets, ticket updates and colleagues' tickets.
- **Away from the computer?** While the screen is locked, or after a minute without mouse or keyboard input, notifications stop counting down, so they're still there when you come back. Both this and taking turns can be switched off on the new *Behavior* page (under Notifications), which also has Snooze.
- **Several notifications take turns**: only the oldest one counts down; the others wait with a full bar and get their full time when it's their turn, instead of all disappearing at once.

## 1.3.11

- **Copy** on ticket notifications (the icon next to ✕, or "Copy" on Windows notifications): the ticket as a clickable title with its details, ready to paste into Teams. Counted under Insights.
- **Insights** (new page in Settings): ticket notifications of the last 30 days per kind, and what you did with them: opened, took it, snoozed, closed or let them time out. Kept on your PC only.
- **Suggestions** on the Insights page: whether a kind of notification is mostly ignored, closed right away, snoozed a lot or too frequent, with what to change (or that it works well).
- **App theme** under Settings → General: the look of the Settings window and its dialogs. Notifications use the same style, or their own one (Look & sound). Your current style is kept for both.
- **Priorities for ticket changes** under Settings → Notifications: choose which priorities notify, separately for your own tickets (all by default) and for CC / following tickets (Urgent and High by default). The ticket's current priority counts, so a ticket raised to Urgent notifies.
- **Sound and display for every kind of notification** under Settings → Look & sound: CC / following tickets (until now the same as My tickets, and they start with your My tickets choice), the start summary and app messages (update ready, welcome, group not found) each have their own row. New display time: Medium (~10 s).
- "Show the comment text" moved to Settings → Look & sound → Content: it applies to your tickets, CC tickets and colleagues' tickets.
- "Check Zendesk every": five round buttons (30 s, 1, 2, 3 or 5 min) instead of a seconds box; other saved values move to the nearest one.
- **Button shapes follow the style**: rounded in Optimizely and Clean light, fully round in Neon dark and Cozy, square in Terminal, lightly rounded in Windows. Applies to the Settings window and the popups.
- **Reset to defaults** under Settings → General: everything except your account and monitored groups, applied when you click Save.
- New ticket notifications stay about 10 seconds by default (was 5).
- Every popup has a Close button (the same as ✕ and right-click). Ticket notifications also have Snooze; the start summary and app messages don't.
- Starting Zendesk Watcher while it's already running shows a short notification instead of a dialog box.
- The Zendesk subdomain is filled in on first start.
- Help colleagues: the first time you switch it on, colleagues in your own region start unticked. Your region is listed last, and in each region the covered colleagues come first.
- Questions and messages in Settings look like the rest of the app, with buttons that say what they do (Save / Don't save / Cancel).
- Settings sidebar: Help colleagues has its own page (*Colleagues*), shown with *Look & sound* (was Appearance) under Notifications, so Notifications is shorter. Statistics is now *Insights*. No more smears when scrolling Settings pages.
- Under the hood: unit tests for the notification rules (run before every release), and the Settings window and tray app split into smaller parts.

## 1.3.10

- **Send feedback** in the tray menu and on the new Settings → General page: opens an email to Daniel with the app and Windows version filled in, in Outlook if you have it, otherwise your default mail app. If something went wrong, attach the latest log from the log folder.
- **General page** in Settings: start with Windows and the start summary (moved from Notifications), check for updates, send feedback and open the log folder.
- **Snooze** can be set up under Settings → Notifications: show again after 5 minutes to 1 hour (default 10), and only if the ticket still needs attention (default) or always. A snoozed new ticket no longer comes back once someone has taken it, and snoozed popups wait while notifications are paused.
- Help colleagues: the groups list shows the selected groups first, then monitored ones, then groups you're a member of.

## 1.3.9

- **Start summary**: after the first check, a short greeting ("Good morning, Daniel") with what's waiting: unassigned tickets per group and the oldest one, your tickets (open, pending, on hold), CC tickets and Help colleagues. Silent, about 10 seconds; can be switched off under Settings → General.

## 1.3.8

- **Help colleagues: choose the assignees.** Settings lists the members of the covered groups (your monitored groups, or the ones you chose), all ticked. Untick anyone you don't want to cover. New group members are included automatically.
- The assignees are grouped by region (from their Zendesk time zone), with All / None per region and your own region marked, so it's one click to cover only colleagues in other regions. Hover a name to see their time zone.

## 1.3.7

- **Chiptune sounds**: five little Amiga-style jingles (Blip, Arpeggio, Bounce, Alert, Fanfare) and three short songs with melody, bass and drums (Bouncy, Groove, Celebration, about 4–5 s) to choose from under Sound and display.
- A ▶ button next to each sound plays it, without showing a notification.
- App messages have their own sensible behavior instead of the "My tickets" settings: "No updates" and similar are silent and short, "Update ready", the welcome and "group not found" stay about 25 s, and "Sign-in required" stays until dismissed.

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
