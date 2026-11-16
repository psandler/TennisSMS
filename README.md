# TennisSMS

An SMS-first service that fills tennis games (singles or doubles) for one organizer. The organizer creates a game and invites players. Players reply YES, NO or OUT by text, or tap a per-game link. Players don't have accounts.

- [docs/overview.md](docs/overview.md): how the service works, in plain language.
- [docs/architecture.md](docs/architecture.md): how the app is put together: components, data, and the main message flows (diagrams).
- [docs/spec-v0.1.md](docs/spec-v0.1.md): the technical spec for v0.1, as built.
- [docs/spec-v0.2.md](docs/spec-v0.2.md): the spec for v0.2 (draft): flexible player counts, an optional SQL Server container for development, and real texting through Twilio.
- [docs/enhancements.md](docs/enhancements.md): ideas for after v0.1.

Status: v0.1 is complete. It runs end to end against a **simulated** SMS gateway, with fake phones in the browser. The Twilio integration is planned for v0.2 (spec-v0.2 §10). Until then, no real texts are sent.

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

The simulator (`/dev/simulator`) shows the organizer's phone (top left), 7 fake player phones and one unknown number. Every text a fake phone sends goes through the same inbound pipeline real texts will use.

1. **Reset**: wipes the dev database and seeds 7 players. They start as "Pending" because they haven't texted JOIN yet.
2. **JOIN all**: every player texts JOIN and is opted in.
3. **Quick game**: creates a doubles game about 20 hours out and invites everyone.
4. Reply from any phone with the YES / NO / OUT buttons, or type a text.
5. Or give phones an **Auto-reply** mode (Always YES, Always NO, YES then OUT, Random, Silent) and watch. Auto-replies show a "typing…" countdown first. **Pause** holds them, and **Step** releases one at a time.

**Scenarios** are one-click scripts: fill a doubles game, a race for the last spot, a drop and refill, two invitations, STOP mid-game, and a stranger texting. Each one resets first.

Next to the phones, the page shows:
- **Timers**: each game's not-full alert, reminder and start time, with a **Fire now** button. Timers otherwise run on real time.
- **Event timeline**: every event recorded, newest first. It shows why each text was sent.

## Record the scenarios as videos

`tools/TennisSms.ScenarioRecorder` uses Playwright to record a guided `tour` of the app as a video. Every step is a visible click with a drawn pointer. The action pauses for explanatory notes beside what they describe, and title cards mark each section. The tour covers:
- the phone simulator explained, and players signing up by texting JOIN;
- the organizer creating a game (the recorder signs in off camera);
- players replying by text and by tapping their link;
- the can't-fill alert, then two more invited: one takes the spot and the other is told it filled;
- a player dropping out, the organizer's options (re-invite or invite someone new), and the refill;
- a reminder.

The script is `Tour.cs`; new recordings go in their own files and are listed in `Recorder.Available`.

**Every recording resets the dev database.**

```bash
dotnet run --project tools/TennisSms.ScenarioRecorder
```

- It uses the app if it's already running on http://localhost:5234 (for example from Visual Studio). Otherwise it starts the app and stops it afterwards.
- Videos go to `recordings/` (git-ignored) as `<name>-<yyyy-MM-dd-HHmm>.webm`, so earlier takes are kept.
- The first run downloads Playwright's Chromium and FFmpeg (a few hundred MB, kept in `%LOCALAPPDATA%\ms-playwright`).
- Options (after `--`):
  - `--list`: what can be recorded. Name some to record only those, e.g. `opening` (just the tour's first section, for quick reviews).
  - `--pace 1.3`: stretch every pause, note, pointer glide and typing delay (1 = normal, 1.5 = slower, 0.8 = faster). All note text is in `Tour.cs`; the overall feel settings (including how long notes stay up per word) are at the top of `Stage.cs`.
  - `--mp4`: also save an `.mp4` (H.264) copy. Use it for sharing: WebM doesn't play everywhere (iPhone, Mac, PowerPoint, many chat apps). Needs ffmpeg on the PATH (`winget install Gyan.FFmpeg`); Playwright's own copy can't write MP4.
  - `--headed` shows the browser; `--url` and `--out` change the app address and output folder.

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
| `tools/TennisSms.ScenarioRecorder` | Records the guided tour as a video (Playwright) |
| `docs/` | Overview and spec |

**Stack:** .NET 10 and Blazor; [Polecat](https://polecat.jasperfx.net) for storage (events, documents and read models on SQL Server); [Wolverine](https://wolverinefx.net) for handlers, the transactional outbox, the durable inbound queue and scheduled timers.
