using System.Text.RegularExpressions;
using Microsoft.Playwright;

/// <summary>
/// The guided tour. Every step is a visible action in the real UI: the organizer's pages, the
/// players' phones in the simulator, and one player's own game page (via the link in their text).
/// No auto-replies, so the order and timing are always the same.
///
/// Cast: Phil (organizer, playing). Invited: Hyo, Jim, Michael, Mike. Then Ben and Nick.
/// Hyo drops out later; Nick is re-invited and Jed invited; Nick takes the spot.
/// </summary>
sealed class Tour(Stage s)
{
    private string _gamePath = "";

    public async Task RunAsync()
    {
        await s.GoInteractiveAsync("/dev/simulator");
        await s.TitleCardAsync("TennisSMS", "Filling a tennis game by text message: a guided tour", 4000);

        await SetUpPlayersAsync();
        await SignInAsync();
        await CreateGameAsync();
        await InvitationsArriveAsync();
        await RepliesByTextAsync();
        await ReplyByLinkAsync();
        await CantFillAsync();
        await InviteTwoMoreAsync();
        await GameOnAsync();
        await DropOutAsync();
        await OrganizerOptionsAsync();
        await RefillAsync();
        await ReminderAsync();
        await WrapUpAsync();
    }

    // ---------- 1. Players ----------

    private async Task SetUpPlayersAsync()
    {
        await s.CaptionAsync("The phone simulator",
            "Fake phones for Phil (the organizer), seven players and one unknown number. Every text goes through the real pipeline.");
        await s.SpotlightAsync(s.Page.Locator("section.phones"), "Fake phones", 3500);

        await s.CaptionAsync("Start fresh", "Reset wipes the dev database and adds seven players. Phil is already set up as the organizer.");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }), "Reset");
        await s.WaitUntilQuietAsync();

        await s.CaptionAsync("Players opt in by texting JOIN", "Nobody gets invites until they text JOIN. Here everyone does at once.");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "JOIN all", Exact = true }), "JOIN all");
        await s.WaitUntilQuietAsync();
        await s.SpotlightAsync(await s.WaitForMessageAsync("Hyo", "signed up"), "Hyo is signed up", 3000);
    }

    // ---------- 2. Organizer ----------

    private async Task SignInAsync()
    {
        await s.TitleCardAsync("The organizer", "Phil signs in with a code texted to his phone. No passwords.");
        await s.GoAsync("/login");
        await s.CaptionAsync("Organizer sign-in", "Phil enters his mobile number.");
        await s.TypeAsync(s.Page.Locator("input[name=PhoneInput]"), "555-010-0001");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Text me a code" }));
        await s.Page.Locator("input[name=CodeInput]").WaitForAsync();

        var code = await s.ReadLoginCodeAsync();
        await s.CaptionAsync("The code arrives by text", "It's good for 10 minutes and works once.");
        await s.NotificationAsync("Text on Phil's phone", $"TennisSMS login code: {code}. It expires in 10 minutes.");
        await s.TypeAsync(s.Page.Locator("input[name=CodeInput]"), code, 140);
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Log in" }));
        await s.Page.WaitForURLAsync(u => !u.Contains("/login"));
        await s.PauseAsync(1500);
    }

    private async Task CreateGameAsync()
    {
        await s.GoInteractiveAsync("/games/new");
        await s.CaptionAsync("Create a game", "Doubles, two days from now, 6 to 7:30pm.");
        var date = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd");
        await s.Page.Locator("input[type=date]").FillAsync(date);
        await s.TypeAsync(s.Page.Locator("input.wide"), "Oak Park courts");

        await s.CaptionAsync("Who's playing", "Phil is playing. Hyo, Jim, Michael and Mike are invited to fill the other three spots.");
        await s.ClickAsync(RosterButton("Phil", "Playing"), "Phil is playing");
        foreach (var name in new[] { "Hyo", "Jim", "Michael", "Mike" })
            await s.ClickAsync(RosterButton(name, "Invite"));
        await s.PauseAsync(800);

        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Create game and send texts" }));
        await s.Page.WaitForURLAsync(new Regex("/games/[0-9a-f-]{36}$"));
        _gamePath = new Uri(s.Page.Url).AbsolutePath;
        await s.GoInteractiveAsync(_gamePath);
        await s.CaptionAsync("The game page", "Playing: Phil. Invited: four players, waiting for their answers.");
        await s.SpotlightAsync(s.Page.Locator("section.roster"), "Roster", 3500);
    }

    // ---------- 3. Players reply ----------

    private async Task InvitationsArriveAsync()
    {
        await s.TitleCardAsync("The players", "Each invited player gets one text. They answer YES or NO, or tap their link.");
        await s.GoInteractiveAsync("/dev/simulator");
        await s.CaptionAsync("The invitation", "Date, time, place, spots open, and a link to this player's own page for the game.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Hyo", "Reply YES or NO"), "Hyo's invitation", 4000);
    }

    private async Task RepliesByTextAsync()
    {
        await s.CaptionAsync("Hyo texts YES", "He's in. The reply lists who's playing and how many spots are left.");
        await s.ClickAsync(s.PhoneButton("Hyo", "YES"), "Hyo: YES");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Hyo", "You're in"), null, 3000);

        await s.CaptionAsync("Jim texts NO", "A short thank-you, and he won't be asked about this game again.");
        await s.ClickAsync(s.PhoneButton("Jim", "NO"), "Jim: NO");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Jim", "Got it"), null, 2500);
    }

    private async Task ReplyByLinkAsync()
    {
        await s.CaptionAsync("Michael taps the link instead", "Every invitation has a link to the player's own page for the game. No app, no login.");
        var link = (await s.WaitForMessageAsync("Michael", "Reply YES or NO")).Locator("a[href*='/g/']");
        await link.EvaluateAsync("a => a.removeAttribute('target')"); // keep it in this tab, so it's in the video
        await s.ClickAsync(link, "Michael's link");
        await s.Page.WaitForURLAsync(new Regex("/g/"));
        await s.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await s.CaptionAsync("Michael's game page", "What, when, where, who's playing, who's invited, and his choices.");
        await s.SpotlightAsync(s.Page.Locator(".player-page"), null, 2500);
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Yes, I'm in" }), "Yes, I'm in");
        await s.Page.Locator("p.result").WaitForAsync();
        await s.CaptionAsync("Done", "The page answers right away. It works the same as texting YES.");
        await s.SpotlightAsync(s.Page.Locator("p.result"), null, 3000);
    }

    // ---------- 4. Organizer alerts ----------

    private async Task CantFillAsync()
    {
        await s.GoInteractiveAsync("/dev/simulator");
        await s.CaptionAsync("Mike says NO", "Now three are in, and nobody is left to ask: the game can't fill.");
        await s.ClickAsync(s.PhoneButton("Mike", "NO"), "Mike: NO");
        await s.WaitForMessageAsync("Mike", "Got it");

        await s.CaptionAsync("Phil is told right away", "One text: how many are in, how many are still waiting, and how many more to invite.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Phil", "can't fill"), "Text to Phil", 4500);
    }

    private async Task InviteTwoMoreAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.CaptionAsync("The game page says so too", "And lists who else can be invited.");
        await s.SpotlightAsync(s.Page.Locator("p.shortfall"), null, 3000);

        await s.CaptionAsync("Phil invites two more: Ben and Nick", "One spot, two invitations: whoever says yes first gets it.");
        await s.ClickAsync(RosterButton("Ben", "Invite"), "Invite Ben");
        await s.ClickAsync(RosterButton("Nick", "Invite"), "Invite Nick");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Send invitations" }));
        await s.PauseAsync(1500);

        await s.GoInteractiveAsync("/dev/simulator");
        await s.CaptionAsync("Both get the same invitation", "Each with the one open spot and a link to their own page.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Ben", "Reply YES or NO"), "Ben's invitation", 3000);
        await s.SpotlightAsync(await s.WaitForMessageAsync("Nick", "Reply YES or NO"), "Nick's invitation", 3000);
    }

    private async Task GameOnAsync()
    {
        await s.CaptionAsync("Ben texts YES first", "That's the fourth player: the game is full.");
        await s.ClickAsync(s.PhoneButton("Ben", "YES"), "Ben: YES");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Ben", "Game on"), "Ben", 2500);

        await s.CaptionAsync("Nick is told it filled", "His invitation closes with a thank-you, so he isn't left waiting.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Nick", "filled up"), "Nick", 3000);

        await s.CaptionAsync("Game on!", "Everyone already in gets the final roster. Phil gets one organizer text, not two.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Hyo", "Game on"), "Hyo", 2500);
        await s.SpotlightAsync(await s.WaitForMessageAsync("Phil", "is full"), "Phil", 3000);
    }

    // ---------- 5. A drop and a refill ----------

    private async Task DropOutAsync()
    {
        await s.TitleCardAsync("Plans change", "Hyo can't make it after all.");
        await s.CaptionAsync("Hyo texts OUT", "He's out, and he's told the organizer knows.");
        await s.ClickAsync(s.PhoneButton("Hyo", "OUT"), "Hyo: OUT");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Hyo", "You're out"), "Hyo", 3000);

        await s.CaptionAsync("Phil gets one text", "Who dropped, the open spot, that nobody is waiting, and a link to the game page.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Phil", "dropped from"), "Text to Phil", 4500);
    }

    private async Task OrganizerOptionsAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.CaptionAsync("Phil's options", "The game is open again, with one spot.");
        await s.SpotlightAsync(s.Page.Locator("section.roster"), "Hyo is under \"Dropped out\"", 3500);
        await s.SpotlightAsync(s.Page.Locator("p.shortfall"), null, 2500);

        var reinvite = s.Page.Locator(".card", new() { Has = s.Page.GetByRole(AriaRole.Button, new() { Name = "Re-invite them" }) });
        await s.CaptionAsync("Option 1: re-invite with one tap", "Nick was told the game filled. One button asks him again.");
        await s.SpotlightAsync(reinvite, "Re-invite", 3500);
        await s.CaptionAsync("Option 2: invite someone new", "Anyone who has opted in and hasn't said no.");
        await s.SpotlightAsync(s.Page.Locator("table.list.compact"), "Invite more", 3000);

        await s.CaptionAsync("Phil does both", "He re-invites Nick, and invites Jed.");
        await s.ClickAsync(reinvite.GetByRole(AriaRole.Button, new() { Name = "Re-invite them" }), "Re-invite Nick");
        await s.PauseAsync(1500);
        await s.ClickAsync(RosterButton("Jed", "Invite"), "Invite Jed");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Send invitations" }));
        await s.PauseAsync(1500);
    }

    private async Task RefillAsync()
    {
        await s.GoInteractiveAsync("/dev/simulator");
        await s.CaptionAsync("Nick and Jed are invited", "Nick gets a fresh invitation for the open spot.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Nick", "Reply YES or NO", after: 1), "Nick", 2500);
        await s.SpotlightAsync(await s.WaitForMessageAsync("Jed", "Reply YES or NO"), "Jed", 2500);

        var gameOn = await s.CountMessagesAsync("Michael", "Game on");
        var full = await s.CountMessagesAsync("Phil", "is full");
        await s.CaptionAsync("Nick says YES", "Full again. All good.");
        await s.ClickAsync(s.PhoneButton("Nick", "YES"), "Nick: YES");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Nick", "Game on"), "Nick", 2500);
        await s.SpotlightAsync(await s.WaitForMessageAsync("Jed", "filled up"), "Jed", 2500);
        await s.CaptionAsync("Game on, again", "The others get the new roster, and Phil gets one text.");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Michael", "Game on", after: gameOn), "Michael", 2500);
        await s.SpotlightAsync(await s.WaitForMessageAsync("Phil", "is full", after: full), "Phil", 3000);
    }

    private async Task ReminderAsync()
    {
        await s.CaptionAsync("Reminders", "At 7pm the evening before, everyone playing gets a reminder. Timers run on real time; here we fire it now.");
        var reminder = s.Page.Locator("table.timers tr", new() { Has = s.Page.Locator("td", new() { HasTextRegex = new Regex("^Reminder$") }) });
        await s.SpotlightAsync(s.Page.Locator("table.timers"), "Timers", 2500);
        await s.ClickAsync(reminder.GetByRole(AriaRole.Button, new() { Name = "Fire now" }), "Fire the reminder");
        await s.SpotlightAsync(await s.WaitForMessageAsync("Michael", "Reminder"), "Michael's reminder", 3500);
    }

    private async Task WrapUpAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.CaptionAsync("Everything in one place", "Phil's game page: the final roster and every text about this game.");
        await s.SpotlightAsync(s.Page.Locator("section.roster"), "Roster", 3000);
        await s.Page.Mouse.WheelAsync(0, 900);
        await s.PauseAsync(3500);
        await s.TitleCardAsync("That's TennisSMS", "Invite by text, answer by text or link, and the organizer only hears what matters.", 4000);
    }

    // ---------- Helpers ----------

    /// <summary>The Invite / Playing / — button for a player in a roster picker.</summary>
    private ILocator RosterButton(string player, string choice) =>
        s.Page.Locator("table.list.compact tr", new() { Has = s.Page.Locator("td", new() { HasTextRegex = new Regex($"^{Regex.Escape(player)}$") }) })
            .GetByRole(AriaRole.Button, new() { Name = choice, Exact = true });
}
