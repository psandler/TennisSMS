# Tennis Game Scheduler — Specification v0.2

Status: **Draft for review.** Not started.

v0.2 is v0.1 plus the changes below. Everything not mentioned here stays as described in [spec-v0.1.md](spec-v0.1.md). "v0.1 §N" refers to that document.

Items marked **OPEN** are undecided. Items marked **DEFAULT** are proposed answers to confirm.

## 1. Scope

**In scope:**
- Flexible player counts: a minimum and a maximum per game, instead of a fixed singles or doubles capacity (enhancements E-1). Sections 2–8.
- Development: SQL Server 2025 in a container, as an option next to the installed instance (§9). v0.1 §3 left this open.

**Out of scope:** everything else in [enhancements.md](enhancements.md), and Twilio (v0.1 §13).

## 2. Purpose

Most games don't need an exact number of players.
- **Tennis:** Phil has a court and invites three people. Any total from 2 to 4 works: 2 is singles, 3 is a three-player game such as king of the court, and 4 is doubles. That's min 2, max 4.
- **Other activities:** a trivia night has no minimum but a maximum of 8. That's min 1, max 8.

Singles (2–2) and doubles (4–4) become presets of the general rule.

## 3. Domain model changes (v0.1 §4.3)

### 3.1 Game state
- `MinPlayers`, `MaxPlayers` replace `Capacity`. Rules: 1 ≤ min ≤ max ≤ 12. **OPEN**: the upper limit. 12 keeps the roster texts within one segment with short names; check it against the segment rule (v0.1 §8.2).
- `Activity`: a short name shown in texts ("Tennis", "Trivia"). At most 10 characters, GSM-7 only. **DEFAULT**: "Tennis".
- `Format` (`Singles` | `Doubles`) is no longer stored. Presets are a UI choice only (§5).
- Derived values:
  - `OpenSpots = MaxPlayers − count(Confirmed)`.
  - `IsOn = count(Confirmed) ≥ MinPlayers`: the game is happening.
  - `Shortfall` (can't-fill alert, v0.1 §5) is measured against the minimum: `max(0, MinPlayers − count(Confirmed) − count(Invited))`.
- `Status` keeps the same values: `Open` | `Full` | `Cancelled` | `Started`. Whether the game is on is derived from the counts, not a separate status. A game can be `Open` (taking more players) and on at the same time.

### 3.2 Events
| Event | Change |
|---|---|
| `GameScheduled` | Adds `MinPlayers?`, `MaxPlayers?`, `Activity?` (all optional) |
| `GameOn` | **New.** Confirmed count reached the minimum. Carries `At` |
| `GameOff` | **New.** A drop, removal or STOP took the confirmed count back below the minimum |
| `GameFilled` | Unchanged: confirmed count reached the maximum. Open invitations close as today |

When one acceptance reaches both min and max (for example the last spot of a 4–4 game), both `GameOn` and `GameFilled` are emitted, but the texts treat it as filling (§4).

**Old events.** **DEFAULT**: no upcaster. The new `GameScheduled` fields are optional. When they're missing, `Game.Create` derives them from the old `Format` and `Capacity`: `Singles` becomes 2–2 and `Doubles` 4–4, with activity "Tennis". The old fields stay on the event type for reading. Games stored before v0.2 have no `GameOn` events; their `IsOn` is derived from the counts, so nothing breaks. Check Polecat's upcasting support (context7) when building, in case it's simpler.

### 3.3 Invariants (v0.1 §4.4)
- Invariant 1 becomes `count(Confirmed) ≤ MaxPlayers` at all times, still under the exclusive stream lock.
- Adding players at creation can't exceed the maximum, as today with capacity.

## 4. Workflow changes (v0.1 §5)

**Accept, under the minimum.** Confirmed. Reply with the roster and how many more are needed: `You're in for Fri 11/13 1-2pm. Playing: Phil, Mike. Need 1 more. Reply OUT if you can't make it.`

**Accept that reaches the minimum (game on), below the maximum.**
- `GameOn`.
- The accepting player gets: `You're in. Game on! Fri 11/13 1-2pm: Phil, Mike, Dave. Room for 1 more.`
- Players already confirmed get: `Game on! Fri 11/13 1-2pm: Phil, Mike, Dave. Room for 1 more.`
- The organizer gets: `Fri 11/13 1-2pm is on: Phil, Mike, Dave. Room for 1 more.`
- Invitations stay open.

**Accept above the minimum, below the maximum.** Confirmed: `You're in for Fri 11/13 1-2pm. Playing: Phil, Mike, Dave. Room for 1 more.` No other texts.

**Accept that reaches the maximum.**
- If the game wasn't on yet, this is today's fill (v0.1 §5).
- If the game was already on: `GameFilled`, and open invitations close with the "filled up" text. The accepting player gets the "you're in, game on" text. **DEFAULT**: players who were already confirmed get nothing more; they already know it's on. The organizer gets `is full`. **OPEN**: should confirmed players hear that it's now full?

**Drop, remove or STOP.**
- Still at or above the minimum: as in v0.1 (organizer alert, `SpotReopened` if it was full). The organizer alert says the game is still on.
- Below the minimum: `GameOff`. The organizer's drop alert says so: `Mike dropped from Fri 11/13 1-2pm. Game is off: need 1 more. {OrganizerLink}`. **DEFAULT**: confirmed players aren't texted; the organizer decides whether to find someone or cancel. **OPEN**: should confirmed players be told it's off again?

**Can't-fill alert (v0.1 §5).** Measured against the **minimum**: the organizer is alerted when the people still waiting can't bring the game up to its minimum. Being unable to reach the maximum is shown on the game page only.

**Not-full alert (v0.1 §9).** Becomes a **below-minimum alert**, at the same time (4 hours before the free-cancellation cutoff), only if the game isn't on: `Fri 11/13 1-2pm still needs 1 to go ahead. Free cancellation ends Thu 1pm. {OrganizerLink}`. A game that's on but not full gets no alert. With a minimum of 1, this alert can only fire when nobody has said yes.

**Reminder (v0.1 §9).** **DEFAULT**: sent to confirmed players only if the game is on. A game that isn't on gets no reminder; the organizer already had the below-minimum alert.

**Game start.** Unchanged: open invitations close. A game below its minimum still "starts"; nothing is cancelled automatically. **OPEN**: should the organizer get a final "starting below minimum" text?

**Changing min or max after creation.** **DEFAULT**: not in v0.2. Cancel and create again, as with format in v0.1. **OPEN**: revisit if it's often needed.

## 5. Organizer pages (v0.1 §11.2)

**New game.**
- An **Activity** field, filled with "Tennis" by default.
- **Players**: a choice of presets, *Singles (2)*, *Doubles (4)*, *2 to 4*, and *Custom*. Custom shows min and max fields. **DEFAULT**: the preset list is fixed in code.
- The roster picker's limit on "Playing" uses the maximum.

**Home and game page.**
- Show "3 playing (2–4)" and an "On" badge when the game is on but not full.
- The game page shows both levels:
  - "Needs 1 more to go ahead" (below the minimum, including the can't-fill shortfall).
  - "Room for 1 more" (on, below the maximum).

**Player game page (v0.1 §7).** The same wording: "Need 1 more" or "Game on, room for 1 more".

## 6. Message templates (v0.1 §8.3)

All still one GSM-7 segment, tested with worst-case names, activity and locations.
- The activity replaces the word "tennis" in the game-changed and reminder texts, and the format word in the invite: `{Brand}: Tennis Fri 11/13 1-2pm at {Location}. 2-4 players, 1 in. Reply YES or NO, or tap {Link}`.
  - **DEFAULT**: when min = max, say "Doubles"/"Singles" for Tennis 4–4 and 2–2, and otherwise "{n} players".
- HELP text: generic ("game invites"), no activity name.
- New and changed templates: §4 above. Exact wording lives in `MessageComposer`, as in v0.1.

## 7. Simulator (v0.1 §11.3)

- **Quick game** stays doubles (4–4).
- New scenarios:
  - *Two to four*: a 2–4 game. Two YES (game on), one more YES, then one OUT (still on), then another OUT (off).
  - *Trivia*: a 1–8 game where everyone says yes and the game fills at 8. This needs more phones: **OPEN**: add phones or invite fewer.
- Timers panel: "Below-minimum alert" replaces "Not-full alert".

## 8. Testing (v0.1 §14)

- Decider tests for every row in §4: under the minimum, reaching it, between min and max, reaching the maximum with and without having been on, drops across the minimum both ways, STOP and removal across the minimum.
- Old-event tests: a stream whose `GameScheduled` has only `Format` and `Capacity` loads as 2–2 or 4–4, activity "Tennis".
- Notification tests: one text per number per command still holds when `GameOn` and `GameFilled` happen together.
- Template tests for the new texts at the 12-player limit with 12-character names.
- Timer tests: the below-minimum alert is skipped when the game is on; the reminder is skipped when it isn't.
- Simulator scenarios from §7 as end-to-end tests.

## 9. Development: SQL Server in a container (optional)

**Why.** v0.1 develops and tests against a SQL Server 2025 instance installed on the machine. A container helps:
- anyone else who clones the repo;
- a future CI build, which can't reach a Windows-installed instance;
- a dev database that can be thrown away without touching the installed server.

**What's added.** No code changes: the app and the tests already take their connection from configuration (`ConnectionStrings:TennisSms`, and `TENNISSMS_TEST_DB` for the tests).
- A `docker-compose.yml` at the repo root:
  - runs Microsoft's SQL Server 2025 Linux image;
  - has a named volume, so data survives `docker compose down` / `up`;
  - has a health check, so `docker compose up --wait` returns only once SQL Server accepts connections.
- **Port:** 14330 on the host, not 1433, so it can run next to the installed instance.
- **Login:** SQL authentication as `sa`, because containers don't support Windows authentication. The SA password goes in a git-ignored `.env` file that compose reads, with a committed `.env.example`. It never goes in `appsettings`.
- **Launch profile:** a second one, "http (container)", that sets `ConnectionStrings__TennisSms` to the container. The existing "http" profile keeps using the installed instance.
- **Tests:** set `TENNISSMS_TEST_DB` to the container's connection string (database `TennisSms_Tests`) to run the integration tests against it.
- **README:** a "Using a container instead" section: start it, pick the profile, run the tests.

**Unchanged.**
- **Default:** the installed instance stays the default (**DEFAULT**; OD-11).
- **Database creation:** the app still creates its database on startup in Development, which works the same against the container.

**Testing.** Run the whole suite once against the container (`TENNISSMS_TEST_DB`) and once against the installed instance before calling it done.

## 10. Open decisions

| # | Decision | Notes |
|---|---|---|
| OD-6 | Upper limit on max players | 12 proposed (§3.1) |
| OD-7 | Texts to confirmed players when a game that's on later fills, or drops back below the minimum | Defaults: no (§4) |
| OD-8 | Reminder for a game that isn't on | Default: none (§4) |
| OD-9 | Changing min or max after creation | Default: not in v0.2 (§4) |
| OD-10 | Extra simulator phones for the trivia scenario | §7 |
| OD-11 | Make the container the default for development | Default: no. Requires Docker Desktop to run the app at all (§9) |
