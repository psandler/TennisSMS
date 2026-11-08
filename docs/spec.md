# Tennis Game Scheduler — Specification

Status: **v0.1 complete (2026-11-08).** This spec describes v0.1 of the software (the MVP) as built, against the simulator gateway. It has been updated where the build differed from the draft; §15 lists those changes. Twilio (§13) is the one piece left. New features go in [enhancements.md](enhancements.md), not here.

Items marked **OPEN** are still undecided. Items marked **DEFAULT** are the values in use; they are configuration and can change.

v0.1 priorities, in order:
1. Correct core game logic (§4, §5).
2. A clean SMS gateway abstraction that the Twilio implementation can drop into without touching the core (§8.1, §13).
3. A clear visual test environment with 6–8 simulated phones, manual and automated replies, that can be watched in real time (§11.3).

## 1. Purpose

Remove the "wrangler" burden of filling tennis games (doubles, also singles). The organizer creates a game with open spots and invites players; players respond by SMS (or via a per-game link) without installing anything or creating an account. First come, first served.

## 2. Scope

### In scope (v0.1)
- Single organizer (Phil). All organizer actions are web-only.
- Organizer-seeded player list (name + mobile number).
- Player opt-in by texting `JOIN` (text-to-join).
- Singles (capacity 2) and doubles (capacity 4).
- Invitations, YES / NO / OUT replies, first come first served.
- Per-player, per-game page link with Yes / No / I'm out buttons.
- Editing a game's date, time, or location after it is created.
- Reminders and organizer alerts tied to the court's cancellation policy.
- SMS behind a provider-neutral abstraction, with a simulator gateway and a multi-phone test environment for development (§11.3).

### Out of scope (v0.1)
- Waitlist.
- Recurring games.
- Invite tiers (regulars before subs).
- Multiple organizers.
- Organizer SMS commands (CANCEL, STATUS, etc.).
- LLM parsing of free-text replies.
- Changing a game's format (singles ↔ doubles). Cancel and create a new game instead.
- Real SMS provider (Twilio). Designed for, not built. See §13.

## 3. Technology decisions

| Area | Decision |
|---|---|
| Runtime | .NET 10 |
| App | Single ASP.NET Core app: Blazor Web App + minimal API endpoints, in one process |
| Instances | **Exactly one app instance.** No scale-out in v0.1 |
| Rendering | Player game page: static SSR. Organizer pages: interactive server |
| Storage | Polecat (JasperFx) for everything: event streams, documents, and read models. No EF Core, no other tables of our own |
| Messaging / workflow | Wolverine (`WolverineFx.Polecat`, `IntegrateWithWolverine()`): command handlers, transactional outbox, durable inbound queue, scheduled messages for timers |
| Database (Azure) | Azure SQL Database. Polecat is confirmed to support it (https://polecat.jasperfx.net/events/projections/async-daemon). Tier: see §3.2 |
| Database (local) | SQL Server 2025. **DEFAULT**: the locally installed instance for development; a SQL Server 2025 container (Polecat's docs include a Docker Compose file) is optional, mainly useful for disposable integration-test databases |
| Hosting | Azure App Service with Always On. Tier: the cheapest one that supports Always On; look it up at deployment time (OD-5) |
| Time zone | One configured zone, `America/Chicago` (see §10) |

### 3.1 Polecat and Wolverine conventions
- Stream identity: `Guid` (Polecat's default). Game streams and player streams are both keyed by Guid.
- Aggregates are `partial` classes/records (Polecat 4.0 requirement).
- Aggregates are self-aggregating with **inline snapshots** (`Snapshot<T>(SnapshotLifecycle.Inline)`), so the current state document is written in the same transaction as the events.
- All read models are **inline projections** stored as Polecat documents (§4.5). Async projections and the async daemon are allowed later, but nothing in v0.1 needs them.
- Game command handlers use the Polecat session directly. A small helper (`GameWork`) takes the game stream with `FetchForExclusiveWriting` (§4.4), runs the pure decider (`GameDecider`: state + command → events + notices), appends the events, and turns notices into outgoing `SendSms` messages. Handlers that touch more than one stream (e.g., STOP, §4.2) lock each game the same way, still in one transaction. Wolverine's aggregate handler workflow (`[WriteAggregate]`) was not used, because every roster command needs the exclusive lock and several commands span streams.
- Wolverine durability mode: `Solo` (single node, no leader election).
- One `SaveChangesAsync` (applied by Wolverine's transactional middleware) commits events, snapshots, projected documents, and outgoing messages together.

### 3.2 Database tier and polling
Wolverine's durability agent polls the database (scheduled messages, outbox recovery, health checks; defaults on the order of seconds, all configurable). Any poll more frequent than the serverless auto-pause delay keeps the database awake, so the free auto-pausing tier no longer fits as-is. Polling can be slowed, but only to the point where timers would still fire on time, which is far more often than auto-pause allows.

- **Decided**: Azure SQL Database **Basic** tier (always on, low fixed cost). Leave Wolverine's polling at its defaults, or relax `ScheduledJobPollingTime` to ~30–60 s since no timer needs better precision.
- Not planned, recorded only as a fallback if cost ever matters: the free serverless tier with Wolverine in `ServerlessWithOutbox` mode, driving `RunDurabilityPassAsync()` from our own timer.

## 4. Domain model

### 4.1 Player (event-sourced aggregate)
One stream per player. The stream holds both profile changes and consent, so the consent audit trail and the player's current state come from one place.

| Event | Data |
|---|---|
| `PlayerRegistered` | PlayerId, Name, PhoneE164 (organizer-seeded) |
| `PlayerDetailsChanged` | Name?, PhoneE164? |
| `PlayerOptedIn` | Keyword, InboundMessageId, OccurredAt |
| `PlayerOptedOut` | Source (`StopKeyword`), InboundMessageId, OccurredAt |
| `OrganizerRoleGranted` | GrantedBy, OccurredAt. The player can organize games (added during the build, see below) |

Snapshot document `Player`: `Id`, `Name`, `PhoneE164`, `ConsentStatus` (`Pending` | `OptedIn` | `OptedOut`), `IsOrganizer`, `CreatedAt`, `UpdatedAt`.
- **The organizer is a player.** On startup (and after the simulator's Reset) the app makes sure a player exists for the configured organizer: registered with the configured name and number, opted in, and granted the organizer role, each recorded with source `config`. The organizer's own phone already receives login codes and alerts, so no JOIN is needed. A STOP from the organizer is still respected. It runs again on every start and adds no events once everything is in place. v0.1 still identifies the organizer by the configured phone. The role is there so that more organizers can be added later (enhancements.md, E-3).
- `UniqueIndex` on `PhoneE164`.
- `Name` is what appears in texts: at most 12 characters, GSM-7 only (e.g., "Mike" or "Mike R"). Validated on entry. This bound is what lets templates guarantee one SMS segment (§8).
- Phone numbers are normalized to E.164 on entry.

### 4.2 Consent rules
- Only `OptedIn` players can be invited or added to games.
- STOP takes effect immediately. In **one transaction**: append `PlayerOptedOut`; close each of the player's open invitations (`InvitationClosed`, reason `OptedOut`); for each upcoming game where they are confirmed, append `PlayerDropped` (reason `OptedOut`) and alert the organizer. The player's games (§4.5) supply the list of affected games.
- After STOP, no further messages go to the player except the STOP confirmation.

### 4.3 Game (event-sourced aggregate)
One stream per game.

State:
- `Format` (`Singles` | `Doubles`) → `Capacity` (2 | 4)
- `StartsAtLocal`, `EndsAtLocal` (local date-time, see §10)
- `Location` (free text)
- `Status`: `Open` | `Full` | `Cancelled` | `Started`
- Per-player participation: `Confirmed` | `Invited` | `Declined` | `Closed` (with reason) | `Dropped` | `Removed`
- `OpenSpots = Capacity − count(Confirmed)`
- `Shortfall = max(0, OpenSpots − count(Invited))` while `Open`, else 0: how many more people must be invited before the game can fill

Events:

| Event | Data / notes |
|---|---|
| `GameScheduled` | GameId, Format, Capacity, StartsAtLocal, EndsAtLocal, Location |
| `GameDetailsChanged` | StartsAtLocal?, EndsAtLocal?, Location? |
| `PlayerAddedByOrganizer` | PlayerId, PageToken. Player becomes Confirmed (used for the organizer and pre-agreed players) |
| `PlayerInvited` | PlayerId, PageToken |
| `InvitationAccepted` | PlayerId, Channel (`Sms` \| `Page`), InboundMessageId? |
| `InvitationDeclined` | PlayerId, Channel, InboundMessageId? |
| `GameFilled` | — |
| `InvitationClosed` | PlayerId, Reason (`GameFilled` \| `GameStarted` \| `GameCancelled` \| `OptedOut`) |
| `PlayerDropped` | PlayerId, Channel, WithinCancellationWindow (bool), Reason? |
| `PlayerRemovedByOrganizer` | PlayerId |
| `SpotReopened` | — (emitted when a Full game drops below capacity) |
| `GameCancelled` | — |
| `GameStarted` | — (emitted by timer at StartsAt) |
| `ReminderIssued` | — (idempotency marker for the evening-before reminder) |
| `CancellationWindowAlertIssued` | — (idempotency marker) |
| `ShortfallAlertIssued` | Shortfall (idempotency marker for the can't-fill alert, added after v0.1, see §5) |

A YES that finds no open invitation or no open spot changes nothing about the game, so it is **not** a game event. The outcome is recorded on the inbound message's `MessageLog` entry (§8) and the player gets the "sorry, filled" reply.

### 4.4 Invariants
1. `count(Confirmed) ≤ Capacity` at all times. Two simultaneous YESes for the last spot must never both succeed. **Decided and built**: every command that changes a game takes Polecat's exclusive stream lock, `FetchForExclusiveWriting` (SQL Server `UPDLOCK, HOLDLOCK`). A Wolverine retry-with-cooldown policy handles `StreamLockedException`. The second YES waits for the first to commit, then sees the game full and gets the "sorry, filled" outcome. An integration test against SQL Server checks this.
2. Only `OptedIn` players can be invited or added.
3. A player can only accept or decline an invitation in `Invited` state. No waitlist.
4. A player in `Declined` cannot be re-invited to the same game in v0.1. A player in `Closed` (e.g., told "filled") can be re-invited.
5. No commands other than viewing are accepted after `GameStarted` or `GameCancelled`.
6. The organizer may or may not be a participant. If playing, they are added with `PlayerAddedByOrganizer`.

### 4.5 Read models (Polecat documents, inline)
All are updated in the same transaction as the events that change them.

| Document | Source | Used by |
|---|---|---|
| `Player` | Player snapshot (§4.1) | Phone → player lookup, `/players`, consent checks |
| `Game` | Game snapshot (§4.3) | Organizer pages, game page, handlers |
| `PageLink` | Multi-stream projection of `PlayerInvited` / `PlayerAddedByOrganizer`, Id = token | `/g/{token}` lookup |

**As built:** the draft also had `PlayerOpenItems` (one document per player) and `UpcomingGames` projections. Queries over the `Game` documents (`GameQueries`) replaced them: games that aren't over yet, filtered to the ones a player is in. At one organizer's volume that is a handful of rows, and a query can't drift out of step with the games. If volume grows, the projections can be added back without changing the callers.

Plain documents (stored directly, not projected): `MessageLog` entries, `PendingClarification` (§6.3), `UnknownSender` (§6.4), `LoginCode` (§11.1).

## 5. Workflow rules

**Create game.** Organizer sets format, start/end, location, optionally adds confirmed players (including self), then selects invitees from opted-in players.

**Manually added players.** Each player added with `PlayerAddedByOrganizer` (other than the organizer) is texted that they've been added, with the current roster, the OUT option, and their page link. If the game is Full once the organizer's changes are saved (e.g., created already full), each added player instead receives only the "game on" roster text, not both.

**Invite continuously.** The organizer can invite more players any time while the game is `Open` and not started.

**Accept (YES).**
- Open spot exists: Confirmed. Reply with confirmation and current roster.
- That acceptance fills the game: emit `GameFilled`; all remaining `Invited` players get `InvitationClosed(GameFilled)` and a "filled" text; previously confirmed players get the final roster; organizer is notified. The accepting player gets one combined "you're in, game on" text, not a confirmation followed by a roster.

**No duplicate texts.** Within a single command, each phone number receives at most one text. When several notifications would go to the same number, send the combined template. This includes the organizer when they are also a player: they get the organizer version only (e.g., "is full: …" rather than both "Game on!" and "is full").

**Accept with no open invitation or no open spot.** Reply "sorry, filled" (recorded in `MessageLog`, §4.3). Only open invitations count, so a player previously told "filled" cannot claim a reopened spot by replying; the organizer must re-invite them.

**Decline (NO).** `InvitationDeclined`; short acknowledgment.

**Drop (OUT).** A confirmed player drops: `PlayerDropped`. If the game was Full, `SpotReopened`. Organizer is texted. If within the cancellation window (§9), the alert says so. Organizer's game page offers one-tap re-invite of every player whose invitation was closed (told "filled" etc.). Players who declined are not re-invited.

**Can't-fill alert** (added after v0.1, enhancements E-6). When a player's NO, OUT or STOP leaves a shortfall (above), the organizer is texted once: `Fri 11/13 1-2pm can't fill yet: 2 in, 1 waiting. Invite 1 more. {OrganizerLink}`. If the same reply is an OUT, the shortfall goes into the drop alert instead, so the organizer still gets one text. `ShortfallAlertIssued` stops repeats. It re-arms once the shortfall is back to 0, because the organizer invited or added more or the game filled. The organizer's own changes (remove, create with too few invited) don't text; the game page shows the shortfall all the time.

**Organizer remove.** `PlayerRemovedByOrganizer`; the player is texted that they're no longer in the game.

**Edit game.** `GameDetailsChanged`. Confirmed and invited players are texted the new details (one text each). Timers are rescheduled (§9). If the new start time makes the reminder or not-full alert already past, they are skipped, not sent late. Editing is not allowed after `GameStarted` or `GameCancelled`.

**Cancel.** `GameCancelled`; confirmed and invited players are texted; open invitations close.

**Game start.** Timer emits `GameStarted`; all open invitations close (`GameStarted`). Later replies get "this game already started."

## 6. Inbound SMS handling

### 6.1 Pipeline
1. The gateway adapter (simulator now, Twilio later) turns whatever the provider sent into a provider-neutral `InboundSms` (§8.1). The simulator does not take a shortcut: it produces `InboundSms` exactly as the Twilio webhook will.
2. The adapter publishes `InboundSms` to a **durable Wolverine local queue** (persisted in the database inbox) and returns immediately. No external queue service.
3. The inbound handler:
   a. Stores the raw message as a `MessageLog` entry (always, even if unparseable).
   b. Resolves sender by phone number (`Player` document).
   c. Classifies the text (§6.2) and resolves which game it applies to (§6.3).
   d. Invokes the command handler.
4. The command handler loads the aggregate, decides, appends events, and returns outgoing `SendSms` messages. Wolverine commits events, documents, and outgoing messages in one transaction.

Inbound processing for game commands runs one message at a time (local queue parallelism 1 is acceptable at this volume) in addition to invariant 1's locking.

### 6.2 Keywords and reply grammar
Case-insensitive, trimmed, tolerant of trailing punctuation.

| Input | Meaning |
|---|---|
| `JOIN` (aliases `START`, `UNSTOP`) | Opt in (seeded number). Unknown numbers: see §6.4. `START` and `UNSTOP` are the carrier-standard re-subscribe words, so a player who sent STOP can come back with any of the three |
| `STOP` (and standard variants: STOPALL, UNSUBSCRIBE, CANCEL, END, QUIT) | Opt out |
| `HELP` | Help text |
| `YES`, `Y`, `YEP`, `YEAH`, `IN`, `I'M IN` | Accept |
| `NO`, `N`, `NOPE`, `CAN'T`, `CANT` | Decline |
| `OUT`, `DROP` | Drop (confirmed players) |
| `1`, `2`, … | Answer to a pending clarification (§6.3) |
| iPhone reaction text, e.g. `Liked "…"` / `Loved "…"` | Treat as YES. `Disliked "…"` treat as NO |
| Anything else | "Didn't get that" reply with valid options |

Note: `CANCEL` from a player is an opt-out keyword under carrier conventions, not a game action.

### 6.3 Which game a reply applies to
- If the player has exactly one open invitation (or, for OUT, exactly one upcoming confirmed game), the reply applies to it. Source: the player's games (§4.5).
- If more than one applies, reply with a numbered list and store a `PendingClarification` document (player, original intent, the listed game ids, expiry):
  `Which game? 1) Fri 11/13 1pm 2) Sun 11/15 9am. Reply 1 or 2, or use the links.`
  A reply of `1` / `2` applies the original intent to that game. A new YES/NO/OUT replaces the pending clarification. **DEFAULT** expiry: 30 minutes; an expired or unmatched number gets the "didn't get that" reply.
- The page link is always unambiguous and always works.
- Keep the resolver as its own step so other strategies (e.g., "yes fri") can be added later without reworking the pipeline.

### 6.4 Unknown numbers
A number that isn't a seeded player is untrusted, and every reply to it costs money once Twilio is live (inbound texts cost too, but replies double it and are the part we control). Rules:
- `JOIN` from an unknown number gets exactly one reply: `This number is for invited players only. Ask the organizer to add you.` It doesn't name the organizer or give contact details: someone legitimately invited already knows who to ask, and anyone else learns nothing.
- That reply is sent **at most once per number** (tracked in an `UnknownSender` document). Repeat JOINs get no reply.
- `HELP` and `STOP` from an unknown number: handled the same way the carrier rules require for any number (STOP gets its confirmation, HELP its help text), also at most once per number per day. When Twilio is live, its own STOP/HELP handling may answer these before the app sees them (§13).
- Anything else from an unknown number: logged in `MessageLog`, **no reply**.
- Global cap on all replies to unknown numbers (**DEFAULT**: 20 per hour). Over the cap, log and stop replying, and tell the organizer once.
- The organizer page shows recent unknown senders so a real person can be added if wanted.

## 7. Game page link

- Each `PlayerInvited` and `PlayerAddedByOrganizer` carries a `PageToken`: random, URL-safe, short (≈8–10 chars), unique, scoped to one player and one game. The `PageLink` document (Id = token) maps it back.
- URL: `{BaseUrl}/g/{token}`. Included in invitation texts.
- Page (static SSR, mobile-first) shows: date/time, location, confirmed players, invited players, open spots, and the actions valid for this player's state: **Yes / No** (Invited), **I'm out** (Confirmed).
- Actions post a form and invoke the same command handlers as SMS (via Wolverine), with `Channel = Page`. Re-render shows the result.
- Possession of the link is the identity. Accepted risk: a forwarded link lets someone else answer for that player for that game.
- Tokens stop accepting actions at `GameStarted`, `GameCancelled`, or `EndsAt`; the page shows a read-only "game over/cancelled" view. **DEFAULT**: page returns 404 after EndsAt + 7 days.

## 8. SMS gateway and outbound messages

### 8.1 Gateway abstraction
The core never knows which provider is in use. The boundary is small and provider-neutral:

- **Outbound**: `ISmsSender.SendAsync(OutboundSms) → SendResult`. `OutboundSms` = message id (ours, used for idempotency), to (E.164), body. `SendResult` = accepted / failed, plus the provider's message id if any.
- **Inbound**: each adapter produces `InboundSms` = from (E.164), to (E.164), body, provider message id, received-at, and publishes it to the inbound queue (§6.1). The core only ever sees `InboundSms`.
- **Delivery status** (optional in v0.1, **not built**): adapters may report `DeliveryStatusChanged(our message id, status)`; the core records it on `MessageLog`. The simulator reports delivered immediately; Twilio reports via its status callback.
- Implementations:
  - `SimulatorGateway` (development): sending pushes the message into the simulator (§11.3); inbound comes from the simulator UI or the auto-responders.
  - `TwilioGateway` (later, §13): sending calls the Twilio REST API; inbound is a webhook endpoint that validates Twilio's signature and maps the form post to `InboundSms`.
- Which gateway is active is configuration. Nothing outside the adapter references Twilio types.
- An adapter contract test suite runs against both implementations (Twilio's in a test/sandbox mode when it exists), so swapping providers is checked, not assumed. **Not built yet**, since there is only one implementation so far. Write it along with `TwilioGateway` (§13).

### 8.2 Outbound flow
- Command handlers never call the gateway. They return `SendSms` messages, which Wolverine's transactional outbox persists with the events and delivers to a `SendSms` handler after commit. That handler calls the gateway and writes the outbound `MessageLog` entry.
- Delivery is at-least-once. A crash between the gateway call and Wolverine marking the message handled can cause a rare duplicate text; accepted for v0.1. The `SendSms` handler checks `MessageLog` for its message id before sending to keep this window small.
- `MessageLog` (Polecat document): direction, phone, player id?, game id?, body, timestamp, outcome (for inbound: parsed intent, resolved game, result such as "filled"). It backs the simulator and the organizer's per-game message history.
- **Segment rule:** every message must fit one SMS segment: ≤160 characters, GSM-7 charset only. No emoji, no curly quotes, no em dashes. Enforce with a validator in tests over every template with worst-case data (12-character names, §4.1). If a free-text field (e.g., location) would push a message over, drop that field from the SMS (the page has it).

### 8.3 Message templates (DRAFT)
`{Brand}` is a short service prefix, **OPEN** (OD-4; `TennisSMS` for now). Times render like `Fri 11/13 1-2pm`. The table shows the intent. The exact wording is in `MessageComposer`, and tests hold every template to the segment rule.

| Trigger | Text |
|---|---|
| Invite | `{Brand}: Doubles Fri 11/13 1-2pm at {Location}. 2 spots open. Reply YES or NO, or tap {Link}` |
| Accepted | `You're in for Fri 11/13 1-2pm. Playing: Phil, Mike. 1 spot left. Reply OUT if you can't make it.` |
| Accepted and filled the game | `You're in. Game on! Fri 11/13 1-2pm: Phil, Mike, Dave, Sam. Reply OUT if you can't make it.` |
| Added by organizer | `Phil added you to Fri 11/13 1-2pm. Playing: Phil, Mike. Reply OUT if you can't make it. {Link}` |
| Game filled (to previously confirmed) | `Game on! Fri 11/13 1-2pm: Phil, Mike, Dave, Sam.` |
| Filled (to open invitees) | `Fri 11/13 1-2pm filled up. Thanks anyway!` |
| Late YES | `Sorry, Fri 11/13 1-2pm is already full.` |
| Declined | `Got it, thanks.` |
| Dropped (to player) | `You're out of Fri 11/13 1-2pm. The organizer has been notified.` |
| Removed (to player) | `You're no longer in Fri 11/13 1-2pm.` |
| Game changed | `Change: tennis is now Sat 11/14 2-3pm at {Location}. {Link}` |
| Cancelled | `Fri 11/13 1-2pm has been cancelled.` |
| Reminder | `Reminder: tennis tomorrow, Fri 11/13 1-2pm at {Location}. Playing: Phil, Mike, Dave, Sam.` |
| Started | `Fri 11/13 1-2pm has already started.` |
| Which game? | `Which game? 1) Fri 11/13 1pm 2) Sun 11/15 9am. Reply 1 or 2, or use the links.` |
| Unrecognized | `Sorry, didn't get that. Reply YES, NO, or OUT. Reply HELP for help.` |
| Organizer: player dropped | `Mike dropped from Fri 11/13 1-2pm. 1 spot open. {OrganizerLink}` (adds `Inside 24-hr window.` when applicable) |
| Organizer: game filled | `Fri 11/13 1-2pm is full: Phil, Mike, Dave, Sam.` |
| Organizer: can't fill | `Fri 11/13 1-2pm can't fill yet: 2 in, 1 waiting. Invite 1 more. {OrganizerLink}`. Combined with a drop: `Mike dropped from Fri 11/13 1-2pm. 1 spot open, 1 waiting. Invite 1 more. {OrganizerLink}` |
| Organizer: not full alert | `Fri 11/13 1-2pm still needs 1. Free cancellation ends Thu 1pm. {OrganizerLink}` |
| Unknown number sent JOIN | `This number is for invited players only. Ask the organizer to add you.` (§6.4) |
| Join / HELP / STOP | Placeholders. Final wording comes from the Twilio compliance package (§13) |

## 9. Timers

Per game, computed from local times (§10):

| Timer | When (decided; values are configuration) | Action |
|---|---|---|
| Not-full alert | 4 hours before the free-cancellation cutoff (cutoff = StartsAt − 24h), only if not Full and not Cancelled | Text organizer; append `CancellationWindowAlertIssued` |
| Reminder | 7pm local the evening before | Text confirmed players; append `ReminderIssued`. Skip any player who became confirmed (or received the "game on" text) after the reminder time; if that's everyone, or the game was created after the reminder time, send nothing |
| Game start | StartsAt | Append `GameStarted`, close open invitations |

- `PlayerDropped.WithinCancellationWindow` = drop occurred after the cutoff.
- Implementation: **Wolverine scheduled messages.** When a game is scheduled or its time changes, the handler returns scheduled messages (`NotFullAlertDue`, `ReminderDue`, `GameStartDue`) for the computed UTC due times. They are persisted with the transaction and survive restarts. No in-memory schedule.
- Each scheduled message carries the `StartsAtLocal` it was computed from. When it fires, the handler loads the game and does nothing if the game was cancelled, already started, or its start time no longer matches (a newer message exists). Idempotency markers make a timer safe to fire twice. Changing the start time clears the markers, so a moved game gets its own reminder and not-full alert.
- Court policy values (24h cutoff, 7-day booking window) are configuration, not code.

## 10. Time

- Single configured zone: `America/Chicago`. Players never see a zone.
- Store game times as local date-time (`StartsAtLocal`, `EndsAtLocal`), plus the zone id in configuration.
- Convert to UTC in exactly one place (a clock/time service) for scheduling. Servers run in UTC.
- All relative times (cutoff, reminder) are computed in local time, so DST transitions are handled correctly. Tests must cover a game the day after a DST change.
- Inject `TimeProvider` everywhere; no `DateTime.Now`.

## 11. Organizer web app

### 11.1 Authentication
- Organizer's phone number is configuration.
- Login: enter phone → one-time 6-digit code sent via the SMS gateway (visible in the simulator during development) → cookie session. Codes are stored as `LoginCode` documents (hashed, with expiry and attempt count).
- **DEFAULT**: code valid 10 minutes, 5 attempts; session 30 days sliding.

### 11.2 Pages
| Route | Purpose |
|---|---|
| `/login` | Phone + code |
| `/` | Upcoming games: date/time, format, confirmed/capacity, status |
| `/games/new` | Create game: format, date, start/end, location, add confirmed players, select invitees |
| `/games/{id}` | Roster by state; invite more (checklist of opted-in players not yet in the game, excluding Declined); re-invite closed invitations; edit date/time/location; remove player; cancel game; message history for this game |
| `/players` | Add/edit players (name ≤12 chars, phone); see consent status. Validate and normalize phone to E.164. Lists recent unknown senders (§6.4) with an "add as player" shortcut |
| `/g/{token}` | Player game page (§7), no login |
| `/dev/simulator` | Development only (§11.3) |

### 11.3 Test environment: phone simulator (development only)
The simulator is a first-class part of v0.1, not a debugging extra. Goal: watch the whole system work with 6–8 fake players, replying by hand or automatically. Plain and clear beats pretty.

**Availability.** Registered only when `SimulatorGateway` is active (§8.1); never deployed with the Twilio gateway. Route `/dev/simulator`, interactive server rendering so everything updates live.

**Seed and reset.** A "Reset" control wipes the dev database and seeds 7 players with fake numbers (`+155501000xx`) and short names (Hyo, Jim, Michael, Mike, Ben, Nick, Jed), plus the organizer (Phil), who is already opted in. Seeded players start `Pending`; a "JOIN all" control opts them all in. A "Quick game" control creates a doubles game starting ~20 hours out (inside the cancellation window, so window logic is live) and invites everyone.

**Phone grid.** The organizer phone first (top left), then one card per player phone (7), then one "unknown number" phone. Each card shows:
- Name, number, and consent status badge.
- The message thread (from `MessageLog`), newest at the bottom, outbound and inbound visually distinct, links clickable (opens the game page in a new tab).
- Quick-reply buttons: `YES`, `NO`, `OUT`, `1`, `2`, `JOIN`, `STOP`, `HELP`, plus a free-text box.
- An auto-reply setting (below).

**Auto-replies.** Each phone can be set to:
- `Manual` (default): only you reply.
- `Always YES` / `Always NO`: replies to every invitation.
- `YES then OUT`: accepts, then drops after a set time.
- `Random`: YES / NO / no reply with set weights.
- `Silent`: never replies (to test the not-full alert and reminders).

Replies wait a random delay (**DEFAULT** 2–8 s) shown as a visible "typing…" countdown on the card, so you can watch the order things happen in. A global control pauses all auto-replies and a "step" button releases the next one. Auto-replies go through the same `SimulatorGateway` inbound path as manual replies, so they exercise the real pipeline.

**Scenarios.** A small set of one-click scripts, each a list of timed replies across phones. Starting set:
- *Fill a doubles game*: three YES, one NO, game fills.
- *Race for the last spot*: two phones send YES at the same instant.
- *Drop and refill*: a confirmed player sends OUT, organizer re-invites, someone else takes the spot.
- *Two invitations*: one player gets two games and replies YES (clarification prompt).
- *STOP mid-game*: a confirmed player sends STOP.
- *Stranger*: the unknown phone sends JOIN twice and some junk.

**Watching the logic.** Beside the phones:
- An **event timeline**: every event appended, newest first, with stream (game or player), event type, and key data. This is how you see *why* each text was sent.
- A **timers panel**: each game's not-full alert, reminder, and start time, with status (scheduled / fired / skipped) and a **Fire now** button. Wolverine scheduled messages run on real time, so Fire now is how timers are exercised by hand; automated tests use a fake `TimeProvider` instead (§14).
- A link to the organizer pages, so organizer actions are done through the real UI.

## 12. Open decisions

| # | Decision | Notes |
|---|---|---|
| OD-4 | Service name / `{Brand}` prefix and domain | Deferred. Working name TennisSMS. Needed before Twilio registration (links, compliance package) |
| OD-5 | App Service tier | The cheapest tier with Always On. Look up at deployment time |

Decided:
- Join keyword: `JOIN`, with `START` / `UNSTOP` as aliases (§6.2).
- Unknown numbers: rejected with one reply per number, rate-limited (§6.4).
- Timer and expiry defaults as listed in §6.3, §6.4, §7, §9 and §11.3.
- Database tier: Basic (§3.2).
- Last-spot race: built-in exclusive stream lock (§4.4).
- Which game a reply applies to: numbered clarification plus links (§6.3).
- Storage: everything in Polecat, one transaction per command (§3).

## 13. Future: Twilio integration (not in v0.1)

- `TwilioGateway` implements the §8.1 abstraction. Its inbound webhook validates Twilio's request signature, maps the post to `InboundSms`, and publishes to the same durable inbound queue (§6.1). Nothing else in the app changes.
- Requires A2P 10DLC Sole Proprietor registration, a public site with privacy policy, SMS terms, and the text-to-join call to action, plus campaign description, sample messages, and HELP/STOP wording. Tracked separately in `twilio-compliance.md`.
- Keep the app's STOP/HELP handling consistent with Twilio's own opt-out handling. To check when integrating: Twilio's default opt-in keywords may include `YES`, which would collide with YES as "accept". If so, configure Twilio's Advanced Opt-Out so only `JOIN` / `START` / `UNSTOP` re-subscribe.
- Twilio's built-in STOP handling blocks outbound texts to that number until the person re-subscribes. The app must treat Twilio's opt-out as authoritative and mirror it (`PlayerOptedOut`).

## 14. Testing expectations
- Aggregate tests: given events, when command, then events (or rejection). Cover every invariant in §4.4. Wolverine handlers that return events and messages are tested as plain functions.
- Concurrency test: two acceptances for the last spot, against a real database.
- Projection tests: `PageLink` and snapshots reflect the events after each command.
- Parser tests for every row in §6.2, including iPhone reaction text and clarification numbers.
- Clarification tests: two open invitations → numbered prompt; `2` applies to the second; expiry; a new YES replaces the pending prompt.
- Notification tests: no phone number receives more than one text per command (including the organizer as player); the filling acceptor gets only the combined text; a game created already full sends only "game on" to added players; reminders are skipped for late confirmations.
- Template tests: every template ≤160 chars, GSM-7 only, with worst-case names (12 chars) and locations.
- Timer tests with a fake `TimeProvider`, including a DST boundary, a game edit that moves the start (old timers ignored), and a cancelled game (timers no-op).
- STOP test: player with an open invitation and a confirmed game → one transaction closes the invitation, drops them, alerts the organizer.
- Integration tests run against SQL Server 2025 (local instance or container).
- Simulator scenarios (§11.3) double as end-to-end tests: run headless through `SimulatorGateway` and assert on the resulting texts and events.
- End-to-end through the simulator gateway: create game, invite 3, two YES, one NO, game fills, one OUT, re-invite, fill again.

## 15. Changes from the draft (as built)

These are the places where the build differs from the reviewed draft. The sections above are already updated.

| Area | Draft | Built | Section |
|---|---|---|---|
| Handlers | Wolverine aggregate handler workflow with the `[WriteModel]` exclusive attribute | Handlers that use the session directly, with `FetchForExclusiveWriting` and a pure `GameDecider` | §3.1, §4.4 |
| Read models | `PlayerOpenItems` and `UpcomingGames` projections | Queries over `Game` documents | §4.5 |
| Timers | Idempotency markers | Markers are cleared when the start time changes | §9 |
| Delivery status | Optional | Not built | §8.1 |
| Adapter contract tests | Run against both gateways | Waiting for `TwilioGateway` | §8.1, §13 |
| Simulator | 8 players | 7 named players, organizer phone first | §11.3 |
| Organizer as player | Not covered | A player whose number is the organizer's counts as the organizer and gets only the organizer texts | §5 |
| Can't-fill alert | Not in the draft | Added after v0.1 (enhancements E-6) | §4.3, §5, §8.3 |
| Organizer player record | Organizer exists only in configuration | Created on startup as an opted-in player with `OrganizerRoleGranted` | §4.1 |
