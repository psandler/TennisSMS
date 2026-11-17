using System.Text.RegularExpressions;
using Microsoft.Playwright;

/// <summary>
/// Short samples of presentation ideas for the tour, to judge before using them there: a clean simulator
/// layout, close-ups of texts, a camera zoom, the organizer's page side by side with the phones, and a
/// voiceover. Starts from a game already set up off camera (Quick game: doubles, everyone invited).
/// </summary>
sealed class Samples(Stage s)
{
    private const string Intro = "These are short samples of ways to make the demo easier to follow.";
    private const string CleanLine = "First, a cleaner simulator. In recordings, it shows only the phones, so the texts are bigger.";
    private const string CloseUpLine = "A close-up. Hyo's invitation, enlarged next to her phone, so you can read it.";
    private const string ZoomLine = "Or the camera zooms in on one phone. Jim says no, and the app thanks him.";
    private const string SplitLine = "Side by side. Phil's game page is live on the right. Watch it when Hyo says yes.";
    private const string AfterYesLine = "Her yes arrives, and Phil's page moves her to Playing at the same moment.";

    public async Task RunAsync()
    {
        await s.BlankAsync(0);
        if (s.Voice is not null)
            await s.Voice.PrepareAsync([Intro, CleanLine, CloseUpLine, ZoomLine, SplitLine, AfterYesLine]);
        var gamePath = await SetUpOffCameraAsync();

        await s.BlankAsync();
        await s.BlankTitleAsync("Demo samples", "Clean layout · close-ups · camera zoom · side by side · voiceover", "Samples · not the tour", 3500);
        await s.SayAsync(Intro);

        // 1. Clean layout
        await s.GoInteractiveAsync("/dev/simulator");
        await s.CleanLayoutAsync();
        await s.BadgeAsync("TennisSMS v0.1 · pre-MVP demo");
        await s.HideCaptionAsync();
        await s.PauseAsync(1500);
        await s.NoteAsync(s.Page.Locator("section.phones"), "Sample 1: a cleaner simulator",
            "Only the phones. No scenario buttons, timers or timeline, and the texts are larger.", say: CleanLine);

        // 2. Close-up of a text
        await s.NoteAsync(s.Message("Hyo", "Reply YES or NO"), "Sample 2: a close-up",
            "The text itself, enlarged in the note.", closeUp: true, say: CloseUpLine);

        // 3. Camera zoom
        await s.ZoomAsync(s.Phone("Jim"), 1.8);
        await s.ClickAsync(s.PhoneButton("Jim", "NO"), "Jim: NO");
        await s.WaitForMessageAsync("Jim", "Got it");
        await s.PauseAsync(600);
        await s.NoteAsync(s.Message("Jim", "Got it"), "Sample 3: camera zoom",
            "The page zooms in on one phone, and back out after.", say: ZoomLine);
        await s.ZoomOutAsync();

        // 4. Side by side
        await s.SplitAsync(gamePath, "Phil", "Hyo", "Michael", "Mike");
        await s.NoteAsync(s.SplitPane, "Sample 4: side by side",
            "Phil's game page, live, next to the phones.", say: SplitLine);
        await s.ClickAsync(s.PhoneButton("Hyo", "YES"), "Hyo: YES");
        await s.WaitForMessageAsync("Hyo", "You're in");
        await s.PauseAsync(2500); // the game page refreshes on its own
        await s.NoteAsync(s.Message("Hyo", "You're in").Or(s.SplitPane), "Both at once",
            "Hyo's reply, and Phil's roster with her under Playing.", say: AfterYesLine);

        await s.SplitAsync(null);
        await s.CleanLayoutAsync(false);
        await s.BlankTitleAsync("End of samples", "Pick what to use in the tour", "Samples · not the tour", 3000);
        await s.BlankAsync(600);
    }

    /// <summary>A fresh database with everyone signed up and a game everyone is invited to. Returns its page.</summary>
    private async Task<string> SetUpOffCameraAsync()
    {
        await s.SignInOffCameraAsync("555-010-0001");
        return await s.OffCameraAsync(async p =>
        {
            await p.GotoAsync(s.Url("/dev/simulator"));
            await p.WaitForFunctionAsync("() => window.Blazor !== undefined");
            await p.WaitForTimeoutAsync(1500);
            foreach (var name in new[] { "Reset", "JOIN all", "Quick game" })
            {
                var button = p.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });
                await button.ClickAsync();
                await p.WaitForTimeoutAsync(500);
                for (var i = 0; i < 120 && !await button.IsEnabledAsync(); i++) await p.WaitForTimeoutAsync(500);
            }
            await p.WaitForTimeoutAsync(3000); // invitations go out through the outbox

            await p.GotoAsync(s.Url("/"));
            var href = await p.Locator("a[href^='/games/']").Filter(new() { HasNotText = "New game" })
                .Filter(new() { HasTextRegex = new Regex(@"\d") }).First.GetAttributeAsync("href");
            return href ?? throw new InvalidOperationException("Quick game didn't show up on the organizer's home page.");
        });
    }
}
