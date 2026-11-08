# CLAUDE.md

## Rule #1: No git write operations. No GitHub CLI.

- **Never run any git command that changes the repository, the index, refs, config, or the working tree through git.** That includes (not exhaustive): `add`, `commit`, `push`, `pull`, `fetch`, `merge`, `rebase`, `reset`, `revert`, `checkout`, `switch`, `restore`, `branch`, `tag`, `stash`, `rm`, `mv`, `cherry-pick`, `clean`, `init`, `clone`, `remote`, `config`, `worktree`, `submodule`. Don't create worktrees either.
- Read-only git is fine: `git status`, `git diff`, `git log`, `git show`, `git blame`.
- **Never use the `gh` CLI**, for any purpose.
- Phil handles all commits, branches, pushes, and PRs. If something needs a git action, say so and stop.
- These rules are also enforced as deny rules in `.claude/settings.json`. Don't try to get around them (wrappers, aliases, scripts, `git -C`, other shells).

## Project

Tennis Game Scheduler: an SMS-first service that fills tennis games (singles/doubles) for a single organizer. Players reply YES / NO / OUT by text or use a per-game link. No player accounts.

- `docs/overview.md`: plain-language description of how the service works.
- `docs/spec.md`: technical spec for v0.1, complete and updated to match the build (§15 lists the changes from the draft).
- `docs/enhancements.md`: future enhancements, not yet decided.

Status: building v0.1. All v0.1 pieces exist: core domain logic, wiring (handlers, inbound pipeline, outbox, simulator gateway, timers), the phone simulator, organizer login and pages, and the player game page (`/g/{token}`). Twilio (spec §13) is not started. Don't change the docs unless asked.

## Layout

- `src/TennisSms.Core`: domain only. Events, `Game`/`Player` aggregates, `GameDecider` (pure decisions: state + command → events + notices), reply parser, message templates (`MessageComposer`, GSM-7 checks), `GameClock` (local time → UTC). No database or Wolverine references; JasperFx.Events is there only for Polecat's aggregate source generator.
- `src/TennisSms.Web`: the single app. Polecat/Wolverine setup (`Infrastructure/Storage.cs`, `AppServices.cs`), `Sms/` (gateway abstraction, simulator gateway, `SendSms` outbox handler, `MessageLog`), `Games/` and `Players/` (commands and handlers; `GameWork` locks a game, applies a decision, renders texts), `Inbound/` (the inbound pipeline and reply resolver), `Simulator/` (fake phones, auto-replies, scenarios; registered only with the Simulator gateway), `Auth/` (SMS login codes), `Organizer/` (page queries), Blazor pages (organizer pages are interactive server and `[Authorize]`; `/login` and `/g/{token}` are static SSR). Blazor talks to handlers in-process via Wolverine, not over HTTP. Wolverine only discovers handler classes whose names end in `Handler`.
- `tests/TennisSms.Core.Tests`: fast unit tests. `tests/TennisSms.IntegrationTests`: against SQL Server 2025 (`TennisSms_Tests` on localhost, override with `TENNISSMS_TEST_DB`).
- Local dev database: `TennisSms` on the default local SQL Server 2025 instance, Windows auth. Created on startup in Development.

## Commands

- Build and test everything: `dotnet test TennisSms.slnx`
- Run the app: `dotnet run --project src/TennisSms.Web` (http://localhost:5234; phone simulator at `/dev/simulator`, Development only). The simulator's Reset wipes the dev database. To log in, enter the organizer number from config (dev: 555-010-0001); the code appears on the organizer phone in the simulator.

## Planned stack (from spec, subject to revision)

- .NET 10, single ASP.NET Core app (Blazor Web App + minimal APIs + background services).
- Polecat (JasperFx, the SQL Server counterpart to Marten) for all storage: events, documents, inline read models. No EF Core. Azure SQL Database in Azure, SQL Server 2025 locally.
- Wolverine (`WolverineFx.Polecat`) for handlers, transactional outbox, durable inbound queue, scheduled-message timers. Single app instance.
- Fake SMS gateway and a phone simulator page for development; Twilio later.

## Working with Polecat

- Polecat is new and moves fast. Don't rely on memory for its APIs. Look things up with the context7 MCP (library id `/llmstxt/polecat_jasperfx_net_llms-full_txt`). Wolverine is `/jasperfx/wolverine`, Marten is `/jasperfx/marten`.
- Polecat docs: https://polecat.jasperfx.net
