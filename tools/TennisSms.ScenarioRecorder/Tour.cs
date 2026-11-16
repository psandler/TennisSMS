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
        await OpeningAsync();
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

    // ---------- 1. The simulator, and players signing up ----------

    /// <summary>From a blank screen to every player signed up. Also recorded on its own as "opening".</summary>
    public async Task OpeningAsync()
    {
        await s.ResetOffCameraAsync(); // empty phones, before the camera is on anything
        await s.BlankAsync();
        await s.BlankTitleAsync("TennisSMS", "Filling a tennis game by text message", "Version 0.1 · pre-MVP · demo");

        await s.GoInteractiveAsync("/dev/simulator");
        await s.BadgeAsync("TennisSMS v0.1 · pre-MVP demo");
        await s.HideCaptionAsync();
        await s.PauseAsync(1200);
        await ExplainSimulatorAsync();
        await PlayersJoinAsync();
    }

    private async Task ExplainSimulatorAsync()
    {
        await s.NoteAsync(null, "This is a demo",
            "There are no real phones here. This page, the phone simulator, stands in for them. It only exists in development.");

        await s.NoteAsync(s.Page.Locator("section.phones"), "Each box is a cell phone",
            "One for each player, plus the organizer. Each one texts back and forth with the app's phone number, as a real phone would.");

        await s.NoteAsync(s.Phone("Phil"), "Phil is the organizer",
            "He sets up games and gets the organizer texts: alerts about his games, and his sign-in codes.");

        await s.NoteAsync(s.Phone("Hyo").Locator(".phone-head"), "A player: Hyo",
            "His name and number, and his status. Phil has already added him, but he's \"Pending\": he gets nothing until he agrees by texting JOIN.");

        await s.NoteAsync(s.Phone("Hyo").Locator(".thread"), "The conversation",
            "Texts between Hyo and the app show up here. Empty for now. Newest at the bottom.");

        await s.NoteAsync(s.Phone("Unknown number"), "An unknown number",
            "Someone nobody has added. It shows what a stranger gets if they text the app.");
    }

    private async Task PlayersJoinAsync()
    {
        await s.NoteAsync(s.Phone("Hyo").Locator("form.free"), "Texting from a phone",
            "Type here and press Send, just like texting. Hyo signs up by texting the word JOIN.");
        await s.TypeAsync(s.Phone("Hyo").Locator("form.free input"), "join", 220);
        await s.ClickAsync(s.Phone("Hyo").Locator("form.free").GetByRole(AriaRole.Button, new() { Name = "Send" }), "Send");
        await s.WaitForMessageAsync("Hyo", "signed up");
        await s.PauseAsync(800);
        await s.NoteAsync(s.Phone("Hyo").Locator(".thread"), "The app answers",
            "Hyo's text is on the right, the app's reply on the left. He's signed up, and the reply tells him how to stop.");
        await s.NoteAsync(s.Phone("Hyo").Locator(".phone-head"), "Hyo is in",
            "His status is now \"OptedIn\". From now on he can be invited to games.");

        await s.NoteAsync(s.Phone("Jim").Locator(".quick"), "Shortcuts",
            "These buttons send that word as a text in one click, so you don't have to type it. They're the words players use: YES, NO, OUT, JOIN, STOP, HELP.");
        await s.ClickAsync(s.PhoneButton("Jim", "JOIN"), "Jim texts JOIN");
        await s.WaitForMessageAsync("Jim", "signed up");
        await s.PauseAsync(800);
        await s.NoteAsync(s.Phone("Jim"), "Same result for Jim",
            "Same JOIN text, same reply. The button only saves typing.");

        var resetAndJoin = s.Page.Locator(".sim-bar button").Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^(Reset|JOIN all)$") });
        await s.NoteAsync(resetAndJoin, "Shortcuts for the whole simulator",
            "Reset wipes everything and starts over, with the players added but Pending. JOIN all has every player text JOIN at once.");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Reset", Exact = true }), "Reset");
        await s.WaitUntilQuietAsync();
        await s.NoteAsync(s.Page.Locator("section.phones"), "A clean slate",
            "Hyo's and Jim's texts are gone, and everyone is Pending again.");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "JOIN all", Exact = true }), "JOIN all");
        await s.WaitUntilQuietAsync();
        await s.WaitForMessageAsync("Jed", "signed up");
        await s.NoteAsync(s.Page.Locator("section.phones"), "Everyone has signed up",
            "Seven players texted JOIN, and all seven are OptedIn. Now Phil can set up a game.");
    }

    // ---------- 2. Organizer ----------

    private async Task SignInAsync()
    {
        await s.TitleCardAsync("The organizer", "Phil signs in with a code texted to his phone. No passwords.");
        await s.GoAsync("/login");
        var phone = s.Page.Locator("input[name=PhoneInput]");
        await s.NoteAsync(phone, "Organizer sign-in",
            "These are Phil's pages for setting up and running games. He signs in with his mobile number.");
        await s.TypeAsync(phone, "555-010-0001");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Text me a code" }));
        var codeInput = s.Page.Locator("input[name=CodeInput]");
        await codeInput.WaitForAsync();

        var code = await s.ReadLoginCodeAsync();
        await s.NotificationAsync("Text on Phil's phone", $"TennisSMS login code: {code}. It expires in 10 minutes.", 2500);
        await s.NoteAsync(codeInput, "The code arrives by text",
            "A six-digit code, sent to Phil's phone. It's good for 10 minutes and works once.");
        await s.TypeAsync(codeInput, code, 160);
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Log in" }));
        await s.Page.WaitForURLAsync(u => !u.Contains("/login"));
        await s.PauseAsync(1200);
    }

    private async Task CreateGameAsync()
    {
        await s.GoInteractiveAsync("/games/new");
        await s.NoteAsync(s.Page.Locator("section.card").First, "Create a game",
            "Singles or doubles, the date and time, and where. This one is doubles, two days from now, at Oak Park.");
        var date = DateTime.Today.AddDays(2).ToString("yyyy-MM-dd");
        await s.Page.Locator("input[type=date]").FillAsync(date);
        await s.TypeAsync(s.Page.Locator("input.wide"), "Oak Park courts");

        await s.NoteAsync(s.Page.Locator("table.list.compact"), "Who's playing",
            "Every player who has signed up. Phil marks himself as playing, and invites four players for the other three spots.");
        await s.ClickAsync(RosterButton("Phil", "Playing"), "Phil is playing");
        foreach (var name in new[] { "Hyo", "Jim", "Michael", "Mike" })
            await s.ClickAsync(RosterButton(name, "Invite"));
        await s.PauseAsync(600);

        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Create game and send texts" }));
        await s.Page.WaitForURLAsync(new Regex("/games/[0-9a-f-]{36}$"));
        _gamePath = new Uri(s.Page.Url).AbsolutePath;
        await s.GoInteractiveAsync(_gamePath);
        await s.NoteAsync(s.Page.Locator("section.roster"), "The game page",
            "Phil's view of this game. Playing: Phil. Invited: four players, and the invitations have just gone out by text.");
    }

    // ---------- 3. Players reply ----------

    private async Task InvitationsArriveAsync()
    {
        await s.TitleCardAsync("The players", "Each invited player gets one text. They answer YES or NO, or tap their link.");
        await s.GoInteractiveAsync("/dev/simulator");
        await TextNoteAsync("Hyo", "Reply YES or NO", "The invitation",
            "Date, time and place, how many spots are open, and a link to Hyo's own page for this game. Jim, Michael and Mike got the same.");
    }

    private async Task RepliesByTextAsync()
    {
        await s.NoteAsync(s.PhoneButton("Hyo", "YES"), "Hyo texts YES", "The YES button sends the text \"YES\", as if he typed it.");
        await s.ClickAsync(s.PhoneButton("Hyo", "YES"), "Hyo: YES");
        await TextNoteAsync("Hyo", "You're in", "He's in",
            "The reply confirms it, lists who's playing, and says how many spots are left.");

        await s.ClickAsync(s.PhoneButton("Jim", "NO"), "Jim: NO");
        await TextNoteAsync("Jim", "Got it", "Jim texts NO",
            "A short thank-you. He won't be asked about this game again.");
    }

    private async Task ReplyByLinkAsync()
    {
        var link = (await s.WaitForMessageAsync("Michael", "Reply YES or NO")).Locator("a[href*='/g/']");
        await s.NoteAsync(link, "Michael taps the link instead",
            "Every invitation has a link to the player's own page for the game. No app to install and no login.");
        await link.EvaluateAsync("a => a.removeAttribute('target')"); // keep it in this tab, so it's in the video
        await s.ClickAsync(link, "Michael's link");
        await s.Page.WaitForURLAsync(new Regex("/g/"));
        await s.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await s.NoteAsync(s.Page.Locator(".player-page"), "Michael's game page",
            "What it is, when and where, who's playing, who's invited, and his choices. On his phone, this is a normal web page.");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Yes, I'm in" }), "Yes, I'm in");
        await s.Page.Locator("p.result").WaitForAsync();
        await s.NoteAsync(s.Page.Locator("p.result"), "Done",
            "The page answers right away. Tapping the button works exactly like texting YES.");
    }

    // ---------- 4. Organizer alerts ----------

    private async Task CantFillAsync()
    {
        await s.TitleCardAsync("When a game can't fill", "Phil hears about it as soon as it happens, not on game day.");
        await s.GoInteractiveAsync("/dev/simulator");
        await s.ClickAsync(s.PhoneButton("Mike", "NO"), "Mike: NO");
        await TextNoteAsync("Mike", "Got it", "Mike says NO",
            "Three are in (Phil, Hyo, Michael), and nobody invited is left to answer. Even if everyone said yes, it can't fill.");
        await TextNoteAsync("Phil", "can't fill", "Phil is told right away",
            "One text: how many are in, how many are still waiting, how many more to invite, and a link to the game page.");
    }

    private async Task InviteTwoMoreAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.NoteAsync(s.Page.Locator("p.shortfall"), "The game page says so too",
            "And below it, everyone else Phil could invite.");

        await s.NoteAsync(s.Page.Locator("table.list.compact"), "Phil invites two more",
            "Ben and Nick. There's one spot and two invitations: whoever says yes first gets it.");
        await s.ClickAsync(RosterButton("Ben", "Invite"), "Invite Ben");
        await s.ClickAsync(RosterButton("Nick", "Invite"), "Invite Nick");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Send invitations" }));
        await s.PauseAsync(1200);

        await s.GoInteractiveAsync("/dev/simulator");
        await s.WaitForMessageAsync("Nick", "Reply YES or NO");
        await s.NoteAsync(Messages(("Ben", "Reply YES or NO"), ("Nick", "Reply YES or NO")), "Both get the same invitation",
            "Ben's and Nick's: the one open spot, and a link to each one's own page.");
    }

    private async Task GameOnAsync()
    {
        await s.ClickAsync(s.PhoneButton("Ben", "YES"), "Ben: YES");
        await TextNoteAsync("Ben", "Game on", "Ben texts YES first",
            "That's the fourth player. The game is full, and Ben gets \"Game on\" with the final roster.");
        await TextNoteAsync("Nick", "filled up", "Nick is told it filled",
            "His invitation closes with a thank-you, so he isn't left waiting for an answer.");

        await s.WaitForMessageAsync("Phil", "is full");
        await s.NoteAsync(Messages(("Hyo", "Game on"), ("Michael", "Game on")), "Game on for everyone",
            "Hyo and Michael, already in, get the same \"Game on\" text with the final roster.");
        await TextNoteAsync("Phil", "is full", "And one text for Phil",
            "His game is full. As a player he'd also get \"Game on\", but he gets one text, not two.");
    }

    // ---------- 5. A drop and a refill ----------

    private async Task DropOutAsync()
    {
        await s.TitleCardAsync("Plans change", "Hyo can't make it after all.");
        await s.NoteAsync(s.PhoneButton("Hyo", "OUT"), "Hyo texts OUT", "OUT is how a player drops out of a game he said yes to.");
        await s.ClickAsync(s.PhoneButton("Hyo", "OUT"), "Hyo: OUT");
        await TextNoteAsync("Hyo", "You're out", "Hyo is out",
            "And he's told the organizer knows, so he doesn't need to text Phil separately.");
        await TextNoteAsync("Phil", "dropped from", "Phil gets one text",
            "Who dropped, that a spot is open, that nobody is waiting to answer, and a link to the game page.");
    }

    private async Task OrganizerOptionsAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.NoteAsync(s.Page.Locator("section.roster"), "Phil's options",
            "The game is open again, with one spot. Hyo is listed under \"Dropped out\".");

        var reinvite = s.Page.Locator(".card", new() { Has = s.Page.GetByRole(AriaRole.Button, new() { Name = "Re-invite them" }) });
        await s.NoteAsync(reinvite, "Option 1: re-invite with one tap",
            "Nick was told the game filled. One button asks him again.");
        await s.NoteAsync(s.Page.Locator("table.list.compact"), "Option 2: invite someone new",
            "Anyone who has signed up and hasn't already said no to this game.");

        await s.NoteAsync(null, "Phil does both", "He re-invites Nick, and invites Jed.");
        await s.ClickAsync(reinvite.GetByRole(AriaRole.Button, new() { Name = "Re-invite them" }), "Re-invite Nick");
        await s.PauseAsync(1200);
        await s.ClickAsync(RosterButton("Jed", "Invite"), "Invite Jed");
        await s.ClickAsync(s.Page.GetByRole(AriaRole.Button, new() { Name = "Send invitations" }));
        await s.PauseAsync(1200);
    }

    private async Task RefillAsync()
    {
        await s.GoInteractiveAsync("/dev/simulator");
        await s.WaitForMessageAsync("Nick", "Reply YES or NO", after: 1);
        await s.WaitForMessageAsync("Jed", "Reply YES or NO");
        await s.NoteAsync(Messages(("Nick", "Reply YES or NO"), ("Jed", "Reply YES or NO")), "Nick and Jed are invited",
            "Nick gets a fresh invitation for the open spot, and Jed gets his first.");

        var gameOn = await s.CountMessagesAsync("Michael", "Game on");
        var full = await s.CountMessagesAsync("Phil", "is full");
        await s.ClickAsync(s.PhoneButton("Nick", "YES"), "Nick: YES");
        await s.WaitForMessageAsync("Jed", "filled up");
        await s.NoteAsync(Messages(("Nick", "Game on"), ("Jed", "filled up")), "Nick says YES",
            "He takes the spot and gets \"Game on\". Jed is told it filled.");
        await s.WaitForMessageAsync("Michael", "Game on", after: gameOn);
        await TextNoteAsync("Phil", "is full", "Full again", "The others get the new roster, and Phil gets one text. All good.", after: full);
    }

    private async Task ReminderAsync()
    {
        await s.TitleCardAsync("The night before", "A reminder goes to everyone playing.");
        var timers = s.Page.Locator("table.timers");
        await s.NoteAsync(timers, "Timers",
            "Each game has timers: a reminder at 7pm the evening before, and more. They run on real time. This demo can't wait two days, so it fires the reminder now.");
        var reminder = s.Page.Locator("table.timers tr", new() { Has = s.Page.Locator("td", new() { HasTextRegex = new Regex("^Reminder$") }) });
        await s.ClickAsync(reminder.GetByRole(AriaRole.Button, new() { Name = "Fire now" }), "Fire the reminder");
        await TextNoteAsync("Michael", "Reminder", "The reminder",
            "Who's playing, when and where. Everyone playing gets one.");
    }

    private async Task WrapUpAsync()
    {
        await s.GoInteractiveAsync(_gamePath);
        await s.NoteAsync(s.Page.Locator("section.roster"), "Everything in one place",
            "Phil's game page: who's playing, who said no, and who dropped.");
        await s.NoteAsync(s.Page.Locator("table.history"), "Every text about this game",
            "Each one in and out, with what it did. Useful when someone says \"I never got the text\".");
        await s.BlankTitleAsync("That's TennisSMS", "Invite by text, answer by text or link, and the organizer only hears what matters.",
            "Version 0.1 · pre-MVP · real texting comes later", 6000);
        await s.BlankAsync(800);
    }

    // ---------- Helpers ----------

    /// <summary>Waits for a text on a phone, then stops and explains it.</summary>
    private async Task TextNoteAsync(string phone, string containing, string title, string body, int after = 0) =>
        await s.NoteAsync(await s.WaitForMessageAsync(phone, containing, after), title, body);

    /// <summary>The newest matching text on each of several phones, as one locator (one note rings them all).</summary>
    private ILocator Messages(params (string Phone, string Containing)[] texts) =>
        texts.Select(t => s.Message(t.Phone, t.Containing)).Aggregate((a, b) => a.Or(b));

    /// <summary>The Invite / Playing / — button for a player in a roster picker.</summary>
    private ILocator RosterButton(string player, string choice) =>
        s.Page.Locator("table.list.compact tr", new() { Has = s.Page.Locator("td", new() { HasTextRegex = new Regex($"^{Regex.Escape(player)}$") }) })
            .GetByRole(AriaRole.Button, new() { Name = choice, Exact = true });
}
