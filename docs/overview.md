# Tennis Game Scheduler: How It Works

## The problem

Getting a tennis game together by text is a chore. One person has to be the "wrangler": book the court, text a bunch of people, keep track of who said yes, who said no, and who never answered, tell everyone when the game is full, and start over when someone drops out the day before.

## The idea

A simple service that does the wrangling, entirely over regular text messages. Players don't install an app, create an account, or remember a password. They just reply to a text.

## How it works for players

**Joining (one time).** The organizer adds you to the list first. Then you text **JOIN** to the service's number. That's your permission to receive game invitations. You can stop at any time by replying STOP, and come back later by texting JOIN again.

**Getting invited.** When a game has open spots, you get a text like:

> Doubles Fri 11/13 1-2pm at the club. 2 spots open. Reply YES or NO, or tap [link]

**Replying.**
- Reply **YES**. If there's still a spot, you're in, and you'll see who else is playing.
- Reply **NO**. That's it. No hard feelings, no follow-up.
- Spots are **first come, first served**. If the game fills before you answer, you'll get a quick "it filled up, thanks anyway."
- **When the game fills**, everyone playing gets a "game on" text with the final lineup.

**Added by the organizer.** If the organizer puts you straight into a game (say you'd already agreed to play), you get a text saying you've been added, with who else is playing and the link.

**Invited to more than one game?** If you just reply YES and it could mean either game, you'll get a quick question back ("Which game? 1) Fri 1pm 2) Sun 9am"). Reply 1 or 2. The link in each invitation always works too.

**If the game changes.** If the organizer moves the time or place, everyone who's in or invited gets one text with the new details.

You'll never get two texts at once about the same thing. For example, if your YES is the one that fills the game, you get a single "you're in, game on" text.

**The link.** Every invitation includes a link to a simple page for that game. It shows when and where, who's in, who's been invited, and how many spots are left. You can tap Yes, No, or "I'm out" right there. No login needed.

**Can't make it after all?** Reply **OUT** (or tap "I'm out" on the page). Your spot opens up and the organizer is told right away.

**Reminders.** The evening before, everyone playing gets a reminder with the time, place, and who's playing. If you only just signed on that evening, you're skipped, since you don't need a reminder an hour after saying yes.

**Cost to players.** Nothing beyond your normal texting plan.

## How it works for the organizer

The organizer uses a simple web page (phone or computer) to:

- **Create a game:** singles or doubles, day and time, and where. The time and place can be changed later.
- **Fill in who's already confirmed**, including themselves if they're playing.
- **Pick who to invite** from the list of people who've signed up.
- **Invite more people any time** until the game is full.
- **See everything at a glance:** who's in, who said no, who hasn't answered.
- **Remove someone or cancel the game.** Everyone affected gets a text automatically.

The service also watches the clock for the organizer:

- **If a game still isn't full** a few hours before the court's free-cancellation deadline (24 hours before the game), the organizer gets a heads-up, so there's time to find someone or cancel without a fee.
- **If someone drops out**, the organizer is told immediately, including whether it's inside that 24-hour window. One tap re-invites everyone who was told the game had filled. People who said no aren't asked again.

## Privacy

- Only people the organizer has added, and who then sign up themselves, receive texts.
- If someone the organizer hasn't added texts JOIN, they get one reply saying the number is for invited players only, and nothing after that.
- Players can see who else is playing or invited for a game they're invited to. That's it.
- Phone numbers aren't shared or used for anything else.
- Replying STOP ends all texts immediately.

## What's not in the first version

- A waitlist (if a game is full, it's full).
- Repeating weekly games set up automatically.
- More than one organizer.
- Understanding long free-form replies. Short answers like YES, NO, and OUT work best.

## Still being decided

- The name of the service (working name: TennisSMS) and its web address.

## Where things stand

The service is being designed and built now. The first version will be tested with 6 to 8 simulated phones on one screen, where replies can be typed by hand or sent automatically and the whole exchange can be watched as it happens. Before it can text real people, it has to be registered with the phone carriers, a standard requirement for any automated texting service. That step comes once a handful of players are on board.
