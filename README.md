# TennisSMS

An SMS-first service that fills tennis games (singles or doubles) for one organizer. The organizer creates a game and invites players. Players reply YES, NO or OUT by text, or tap a per-game link. Players don't have accounts.

- [docs/overview.md](docs/overview.md): how the service works, in plain language.
- [docs/spec.md](docs/spec.md): the technical spec for v0.1.

Status: v0.1 runs end to end against a **simulated** SMS gateway, with fake phones in the browser. The Twilio integration (spec §13) isn't built yet, so no real texts are sent.

## Prerequisites

- **.NET 10 SDK.**
- **SQL Server 2025**, as the default local instance (`localhost`) with Windows authentication. Developer Edition is fine. The app creates its database (`TennisSms`) on first start, and the tests create theirs (`TennisSms_Tests`).
  - To use a different server, change `ConnectionStrings:TennisSms` in [appsettings.Development.json](src/TennisSms.Web/appsettings.Development.json). For the tests, set the `TENNISSMS_TEST_DB` environment variable.
- **Visual Studio 2026** (needed for .NET 10, and it opens `.slnx` solutions), or any editor plus the `dotnet` CLI.

## Run it

### Visual Studio

1. Open `TennisSms.slnx`.
2. Set **TennisSms.Web** as the startup project and pick the **http** launch profile.
3. Press F5. The browser opens the phone simulator at http://localhost:5234/dev/simulator.

### Command line

```bash
dotnet run --project src/TennisSms.Web
```

Then open http://localhost:5234/dev/simulator.

## Try it in the phone simulator

The simulator (`/dev/simulator`) shows 8 fake player phones, the organizer's phone and one unknown number. Every text a fake phone sends goes through the same inbound pipeline real texts will use.

1. **Reset**: wipes the dev database and seeds 8 players. They start as "Pending" because they haven't texted JOIN yet.
2. **JOIN all**: every player texts JOIN and is opted in.
3. **Quick game**: creates a doubles game about 20 hours out and invites everyone.
4. Reply from any phone with the YES / NO / OUT buttons, or type a text.
5. Or give phones an **Auto-reply** mode (Always YES, Always NO, YES then OUT, Random, Silent) and watch. Auto-replies show a "typing…" countdown first. **Pause** holds them, and **Step** releases one at a time.

**Scenarios** are one-click scripts: fill a doubles game, a race for the last spot, a drop and refill, two invitations, STOP mid-game, and a stranger texting. Each one resets first.

Next to the phones, the page shows:
- **Timers**: each game's not-full alert, reminder and start time, with a **Fire now** button. Timers otherwise run on real time.
- **Event timeline**: every event recorded, newest first. It shows why each text was sent.

## Organizer pages

Open http://localhost:5234/ to sign in. In development:

1. Enter the organizer number **(555) 010-0001** (set in `TennisSms:Organizer` in [appsettings.Development.json](src/TennisSms.Web/appsettings.Development.json)).
2. The 6-digit code arrives on the **organizer phone in the simulator**. Enter it to sign in.

| Page | What it's for |
|---|---|
| `/` | Upcoming games |
| `/games/new` | Create a game: details, plus who's invited or already playing |
| `/games/{id}` | Roster, invite more, re-invite, change time or place, remove a player, cancel, message history |
| `/players` | Add and edit players, see who has joined, see unknown numbers |
| `/g/{token}` | A player's own game page (the link in their texts). No login |

A handy setup is the simulator in one browser window and a game page in another: both refresh on their own while replies come in.

## Tests

```bash
dotnet test TennisSms.slnx
```

- `tests/TennisSms.Core.Tests`: fast unit tests for the game rules, message templates, reply parser and time handling.
- `tests/TennisSms.IntegrationTests`: run against SQL Server. They cover the inbound pipeline, the last-spot race and stream lock, timers, login codes, and every simulator scenario run headless.

## Project layout

| Path | Contents |
|---|---|
| `src/TennisSms.Core` | Domain only: events, `Game` and `Player` aggregates, `GameDecider` (pure game rules), reply parser, message templates, `GameClock` |
| `src/TennisSms.Web` | The single app: Polecat and Wolverine setup, handlers, inbound pipeline, SMS gateway and simulator, Blazor pages |
| `tests/` | Unit and integration tests |
| `docs/` | Overview and spec |

**Stack:** .NET 10 and Blazor; [Polecat](https://polecat.jasperfx.net) for storage (events, documents and read models on SQL Server); [Wolverine](https://wolverinefx.net) for handlers, the transactional outbox, the durable inbound queue and scheduled timers.
