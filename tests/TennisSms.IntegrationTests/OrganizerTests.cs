using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using TennisSms.Core.Games;
using TennisSms.Core.Time;
using TennisSms.Web.Auth;
using TennisSms.Web.Games;
using TennisSms.Web.Organizer;
using Wolverine;
using Wolverine.Tracking;
using static TennisSms.IntegrationTests.AppHostFixture;

namespace TennisSms.IntegrationTests;

[Collection(AppCollection.Name)]
public class LoginCodeTests(AppHostFixture app)
{
    private async Task ClearCodeAsync()
    {
        await using var session = app.Store.LightweightSession();
        session.Delete<LoginCode>(OrganizerPhone);
        await session.SaveChangesAsync();
    }

    private async Task<string?> RequestAsync(string phone)
    {
        var before = app.TextsTo(OrganizerPhone).Count;
        await app.Host.InvokeMessageAndWaitAsync(new RequestLoginCode(phone));
        var text = app.TextsTo(OrganizerPhone).Skip(before).SingleOrDefault();
        return text is null ? null : Regex.Match(text, @"login code: (\d{6})\.").Groups[1].Value;
    }

    private async Task<LoginCodeResult> VerifyAsync(string code)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<LoginCodeResult>(new VerifyLoginCode("(555) 010-0001", code));
    }

    [Fact]
    public async Task The_organizer_gets_a_single_use_code_by_text()
    {
        await ClearCodeAsync();
        var code = await RequestAsync("555.010.0001");
        Assert.NotNull(code);

        Assert.Equal(new LoginCodeResult(false, "That code isn't right."), await VerifyAsync(code == "000000" ? "111111" : "000000"));
        Assert.True((await VerifyAsync(code)).Ok);
        Assert.False((await VerifyAsync(code)).Ok); // used up
    }

    [Fact]
    public async Task Other_numbers_get_nothing_and_codes_are_rate_limited()
    {
        await ClearCodeAsync();
        var stranger = NewPhone();
        await app.Host.InvokeMessageAndWaitAsync(new RequestLoginCode(stranger));
        Assert.Empty(app.TextsTo(stranger));

        Assert.NotNull(await RequestAsync(OrganizerPhone));
        Assert.Null(await RequestAsync(OrganizerPhone)); // within the resend cooldown
    }

    [Fact]
    public async Task Five_wrong_codes_burn_the_code()
    {
        await ClearCodeAsync();
        var code = await RequestAsync(OrganizerPhone);
        var wrong = code == "999999" ? "888888" : "999999";

        for (var i = 1; i < LoginCodeHandler.MaxAttempts; i++)
            Assert.Equal("That code isn't right.", (await VerifyAsync(wrong)).Error);
        Assert.Equal("Too many wrong codes. Ask for a new one.", (await VerifyAsync(wrong)).Error);
        Assert.False((await VerifyAsync(code!)).Ok);
    }
}

[Collection(AppCollection.Name)]
public class OrganizerTests(AppHostFixture app)
{
    private OrganizerQueries Queries => app.Services.GetRequiredService<OrganizerQueries>();

    private DateTime DaysAhead(int days) =>
        app.Services.GetRequiredService<GameClock>().ToLocal(DateTimeOffset.UtcNow).Date.AddDays(days).AddHours(18);

    [Fact]
    public async Task Creating_a_game_already_full_sends_only_game_on_and_schedules_timers()
    {
        var (mike, mikePhone) = await app.JoinedPlayerAsync("Mike");
        var (dave, davePhone) = await app.JoinedPlayerAsync("Dave");
        var id = Guid.NewGuid();
        var start = DaysAhead(4);

        var tracked = await app.Host.InvokeMessageAndWaitAsync(
            new CreateGame(id, GameFormat.Singles, start, start.AddHours(1), "Oak Park", [mike, dave], []));

        var game = (await app.LoadAsync<Game>(id))!;
        Assert.Equal(GameStatus.Full, game.Status);
        Assert.StartsWith("Game on!", app.TextsTo(mikePhone).Last());
        Assert.StartsWith("Game on!", app.TextsTo(davePhone).Last());
        Assert.Equal(3, tracked.Scheduled.MessagesOf<IGameTimer>().Count());
    }

    [Fact]
    public async Task Creating_a_game_with_a_bad_roster_saves_nothing()
    {
        var (mike, _) = await app.JoinedPlayerAsync("Mike");
        var id = Guid.NewGuid();
        var start = DaysAhead(4);

        await using var scope = app.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await Assert.ThrowsAsync<GameRuleException>(() =>
            bus.InvokeAsync(new CreateGame(id, GameFormat.Singles, start, start.AddHours(1), "Oak Park", [mike], [mike])));

        Assert.Null(await app.LoadAsync<Game>(id));
    }

    [Fact]
    public async Task The_player_page_shows_the_roster_and_what_the_player_can_do()
    {
        var (mike, _) = await app.JoinedPlayerAsync("Mike");
        var (dave, _) = await app.JoinedPlayerAsync("Dave");
        var (sam, _) = await app.JoinedPlayerAsync("Sam");
        var id = Guid.NewGuid();
        var start = DaysAhead(4);
        await app.Host.InvokeMessageAndWaitAsync(
            new CreateGame(id, GameFormat.Doubles, start, start.AddHours(1), "Oak Park", [mike], [dave, sam]));
        var game = (await app.LoadAsync<Game>(id))!;

        var page = (await Queries.PlayerPageAsync(game.Find(dave)!.PageToken))!;
        Assert.Equal(("Dave", ParticipationStatus.Invited, true), (page.PlayerName, page.Status, page.CanAct));
        Assert.Equal(["Mike"], page.Confirmed);
        Assert.Equal(["Dave", "Sam"], page.Invited);

        await app.Host.InvokeMessageAndWaitAsync(new CancelGame(id));
        page = (await Queries.PlayerPageAsync(game.Find(dave)!.PageToken))!;
        Assert.False(page.CanAct);
        Assert.Equal("This game was cancelled.", page.ReadOnlyReason);

        Assert.Null(await Queries.PlayerPageAsync("no-such-token"));
    }

    [Fact]
    public async Task The_game_detail_lists_who_can_still_be_invited_and_the_message_history()
    {
        var (mike, _) = await app.JoinedPlayerAsync("Mike");
        var (dave, davePhone) = await app.JoinedPlayerAsync("Dave");
        var (sam, _) = await app.JoinedPlayerAsync("Sam");
        var id = Guid.NewGuid();
        var start = DaysAhead(4);
        await app.Host.InvokeMessageAndWaitAsync(
            new CreateGame(id, GameFormat.Doubles, start, start.AddHours(1), "Oak Park", [mike], [dave]));
        await app.TextAsync(davePhone, "no");

        var detail = (await Queries.GameAsync(id))!;
        var eligible = detail.Eligible.Select(p => p.Id).ToList();
        Assert.Contains(sam, eligible);
        Assert.DoesNotContain(mike, eligible);  // playing
        Assert.DoesNotContain(dave, eligible);  // declined

        Assert.Contains(detail.History, m => m.Body == "no" && m.Outcome == "Declined");
        Assert.Contains(detail.History, m => m.Body.StartsWith("Phil added you to"));
    }
}
