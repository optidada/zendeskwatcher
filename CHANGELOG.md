# Changelog

Each release takes its notes from the section with its version number. The first bullet points also show in the
app's "Update ready" notification, so put the most interesting changes first.

## 1.4.4

- **Tick boxes on the Groups page are clearly visible in every theme**: drawn in the theme's colors like the other lists, instead of Windows' own boxes that were faint on dark themes. Drop-downs match the theme too.

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
