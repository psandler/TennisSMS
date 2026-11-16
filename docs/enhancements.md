# Future enhancements

Ideas for after v0.1. The v0.1 spec ([spec-v0.1.md](spec-v0.1.md)) is complete and describes what's built. Nothing here is decided until it moves into a versioned spec. "Spec §N" below means spec-v0.1.

Each entry says what the idea is, why it's wanted, a rough shape, and the questions to settle first. **Open** marks a question nobody has answered yet.

| # | Enhancement | Status |
|---|---|---|
| E-1 | Flexible player counts (min and max) | **Moved to [spec-v0.2.md](spec-v0.2.md)** |
| E-2 | Player groups | Idea |
| E-3 | Multiple organizers (roles), and sharing players and groups | Roles: planned. Sharing: unsure |
| E-4 | Player pages | Idea |
| E-5 | Carried over from v0.1's out-of-scope list | Known |
| E-6 | Can't-fill alert | **Built** in v0.1 (spec §5) |
| E-7 | Testcontainers for the integration tests | Idea; do it with CI |

## E-1. Flexible player counts (min and max)

Moved to [spec-v0.2.md](spec-v0.2.md), which is now the only place it's described.

## E-2. Player groups

**Idea.** Named groups of players, such as "Tuesday regulars", "Subs" or "Trivia team". Invite a whole group in one tap when creating a game, or later from the game page.

**Rough shape.**
- A group is the organizer's document (name, member ids). Membership isn't something players see.
- On create and invite, pick groups as well as individual players. That expands to the members who are opted in and not already in the game.
- Unlocks **invite tiers** (v0.1 out of scope): invite group A now and group B after a delay if the game still isn't full or at min. That needs a timer, like the existing ones.
- A group might carry defaults, such as a usual location or a usual min and max.

**Open questions.**
- Can a player be in more than one group? Probably yes.
- Is a group a plain list, or can it carry defaults and tiers?
- Should tiered invites be automatic (on a timer) or a one-tap "invite the next group" on the game page?

## E-3. Multiple organizers, and sharing players and groups

There are two steps. **E-3a** (several organizers sharing one player list) is planned, and v0.1 already has its first piece. **E-3b** (separate organizers with private lists, and sharing between them) is uncertain.

### E-3a. Organizer as a role on a player

**Idea.** Being an organizer is a role a player has, not a phone number in config. Every organizer is a player. The owner can mark players as organizers, for example five of them.

**Already in v0.1.** The `OrganizerRoleGranted` event and `Player.IsOrganizer` exist. On startup the configured organizer becomes an opted-in player with the role (granted by `config`). Organizer texts and login still use the configured phone.

**Rough shape.**
- **Events on the player stream:** `OrganizerRoleGranted(GrantedBy)` and `OrganizerRoleRevoked(RevokedBy)`. They are separate from `PlayerRegistered` and `PlayerDetailsChanged`, because a permission change is worth its own audit entry and only the owner may make it.
- **UI:** an "Organizer" checkbox on the add and edit player forms, visible only to the owner. Saving sends separate commands (`RegisterPlayer` or `ChangePlayerDetails`, then `GrantOrganizerRole` or `RevokeOrganizerRole`) in one transaction.
- **Owner vs. organizer:** the configured phone is the owner, the starting point. Only the owner grants and revokes. That's two roles, so the event may need to carry a role name.
- **Games record their organizer:** `GameScheduled` gets an `OrganizerId`. Alerts, "X added you" texts and the organizer's name in texts come from that player, not from config. Existing games without one are treated as the owner's.
- **Login:** any player with the role can request a code to their own phone. The code check becomes "a player with the organizer role" instead of "the configured number".
- **Identify organizers by player id** instead of matching the configured phone, in `PlayerDirectory` and elsewhere.

**Open questions.**
- Do organizers see and manage each other's games, or only their own? A guess: everyone sees every game, and each organizer edits only their own.
- STOP from an organizer: block it for organizers, or allow it and warn the owner? Without SMS they get no alerts and can't log in.
- Can an organizer add players, or only the owner?

### E-3b. Separate organizers, and sharing players and groups

**Idea.** More than one organizer uses the service, each with their own players and games. Maybe organizers can share players or groups. That's still uncertain.

**Why it's hard.** Contact information stops being one organizer's private list.
- **Consent is per phone number, not per organizer.** Carrier rules make STOP apply to the service number. A player who texts STOP is out for every organizer, and JOIN brings them back for everyone.
- **The same person can be on several organizers' lists**, maybe under different names ("Mike" to one organizer, "Mike R" to another).
- **Replies cross organizers.** A YES from someone invited by two organizers needs the clarification prompt that already exists (spec §6.3). It should name the organizer: "1) Phil: Fri 1pm 2) Dana: Sat 9am".
- **Privacy.** Sharing a player gives another organizer that person's phone number. Should the player have to agree?

**Rough shape.**
- Split the current `Player` in two:
  - A **phone/person** record, global: phone number and consent.
  - An **organizer contact**, one per organizer per person: the name that organizer uses, and their groups.
- Organizer accounts: login stays by SMS code, but each organizer is a record rather than one configured phone number.
- Sharing, if done: either **copy** (the other organizer gets their own contact entry) or **live share** (a group both can see and edit). Copying is much simpler.

**Open questions.**
- Is this needed at all, or is it enough for each organizer to run their own instance or number?
- Copy or live share?
- Does sharing a player need that player's consent, for example a text they reply YES to?
- One service number for everyone, or one per organizer? This affects Twilio registration (spec §13).

## E-4. Player pages

**Idea.** A page per player, not per game, where they can see everything: upcoming games (with Yes, No and I'm out), recent past games, and their own details. They might also be able to correct their name. It still uses a link with a token, with no login or password.

**Rough shape.**
- A long-lived token per player: `/p/{token}`, kept on the player (a new event, for example `PlayerPageTokenIssued`). Game texts could link there, or keep linking to the game page with a "see all your games" link on it.
- Lists come from the same game queries used today (spec §4.5).
- **Name correction**: the same rules as now (at most 12 characters, GSM-7). It records `PlayerDetailsChanged` with a source of `Player`, and the organizer sees the change.
- Could also show an opt-out button (the same as STOP) and a short "how this works".
- A way to get the link without a game text, such as a `LINK` or `ME` keyword that replies with it.

**Open questions.**
- The risk is bigger than with a game link: a forwarded player link answers for **every** game. Use a longer token? Offer a "new link" option so the old one stops working?
- Can players change their name freely? Or does the organizer approve it, given the name appears in other people's texts?
- Can players change their phone number? Probably not: that needs a new opt-in from the new number.
- With multiple organizers (E-3), does the page show games from every organizer?

## E-5. Carried over from v0.1's out-of-scope list

From spec §2. These were left out of v0.1 on purpose, not rejected.

- **Waitlist**: a YES on a full game queues the player. They get the spot automatically when someone drops, or are offered it. This interacts with E-1: is the waitlist for reaching max only?
- **Recurring games**: for example, every Tuesday 7pm, auto-created N days ahead within the court's booking window (7 days, spec §9).
- **Invite tiers**: regulars first, then subs. Easiest after groups (E-2).
- **Organizer SMS commands**: STATUS, CANCEL, and so on, from the organizer's phone. Note that CANCEL is a carrier opt-out word, so the command needs another name.
- **LLM parsing of free-text replies**: for example "yes for friday" or "can't, sorry". The reply resolver was kept as its own step for this (spec §6.3).
- **Changing a game's format**: largely replaced by E-1 if min and max become editable.

## E-6. Can't-fill alert

**Built.** The spec (§4.3, §5, §8.3) now describes it. Decisions made while building:
- Only a player's NO, OUT or STOP triggers the text. The organizer's own changes (removing someone, or creating a game with too few invited) don't. The game page shows the shortfall all the time.
- The not-full alert near the cancellation cutoff still fires on its own.
- With a real organizer link (72 characters), the drop alert could already go past one SMS segment. The organizer templates now drop the least important part when a text would be too long. The template tests now use a link that long.

The original notes follow.

**Idea.** Tell the organizer as soon as a game can no longer fill with the people already asked, instead of waiting for the not-full alert near the cancellation cutoff.

Example: a doubles game with four invited. Two say YES, one says NO, and one hasn't answered. Even if the last one says YES, that's 3 of 4, so the organizer needs to invite at least one more person now.

**Rule.** Shortfall = capacity − confirmed − open invitations. When it goes above zero, text the organizer once:
`Fri 11/13 1-2pm can't fill yet: 2 in, 1 waiting. Invite 1 more. {OrganizerLink}`

**Rough shape.**
- A new decision in `GameDecider`. It's checked after every change that can raise the shortfall:
  - a decline;
  - a drop (OUT);
  - the organizer removing a player;
  - an invitation closed by STOP.
- An idempotency marker event (for example `ShortfallAlertIssued(Shortfall)`), like the existing timer markers, so the same shortfall isn't reported twice. The marker resets when the shortfall goes back to zero, either because the organizer invites more or someone joins. A later shortfall alerts again.
- **No duplicate texts** (spec §5). A drop already texts the organizer. When a drop also creates a shortfall, combine them into one text: "Mike dropped from Fri 11/13 1-2pm. 1 spot open, nobody left to ask."
- The organizer's game page shows the shortfall all the time ("Invite at least 1 more"), not only in the text.
- With E-1 (min and max), there are two levels:
  - Can't reach **min**: the game can't happen. This is the important alert.
  - Can't reach **max**: the game happens, just smaller. Probably shown on the page only, or a quieter text.

**Open questions.**
- Alert when the game is created with too few invited and none have answered? The organizer just did that on purpose, so probably the page only.
- Should the not-full alert near the cutoff still fire if the organizer has already had a can't-fill alert? Probably yes. It's about money (the free-cancellation cutoff), not about who's left to ask.
- After the start time, or after the game is cancelled: never.

## E-7. Testcontainers for the integration tests

**Idea.** The integration test fixture starts its own throwaway SQL Server 2025 container (the `Testcontainers.MsSql` package), runs the tests against it, and removes it afterwards.

**Why it's worth it, but not yet.** It earns its keep **when there's a CI build**. A CI runner then needs no database setup and no connection string. Every run starts from an empty server, so nothing left over from an earlier run can make a test pass or fail. Until then it mostly adds 10–20 seconds of container startup to every test run on a machine that already has SQL Server installed. The v0.2 compose file (spec-v0.2 §9) covers local use.

**Rough shape.**
- `AppHostFixture` starts the container when `TENNISSMS_TEST_DB` isn't set and Docker is available. Otherwise it uses `TENNISSMS_TEST_DB` or the installed instance, as today.
- One container per test run, shared by all integration tests (they already share one host and run one at a time).
- CI (GitHub Actions on Linux) runs `dotnet test` with no other setup.

**Open questions.**
- Should Testcontainers be the default for local test runs too, or only when no instance is configured?
- Keep the installed-instance path at all, once CI exists?

Not an enhancement but still to do: the **Twilio integration** (spec §13). It's part of v0.2 ([spec-v0.2.md](spec-v0.2.md) §10).
