using Microsoft.Extensions.DependencyInjection;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Web.Simulator;
using Wolverine.Tracking;

namespace TennisSms.IntegrationTests;

/// <summary>
/// The simulator's scenarios run headless as end-to-end tests (spec §14): each one resets the
/// database, joins the 8 players, and plays its steps through the real pipeline.
/// </summary>
[Collection(AppCollection.Name)]
public class SimulatorTests(AppHostFixture app)
{
    private SimulatorEngine Engine => app.Services.GetRequiredService<SimulatorEngine>();

    private Task RunAsync(SimulatorScenario scenario) =>
        Engine.RunScenarioAsync(scenario, runNow: step => app.Host.ExecuteAndWaitAsync(_ => step()));

    private async Task<List<Game>> GamesAsync()
    {
        await using var session = app.Store.QuerySession();
        return (await session.Query<Game>().ToListAsync()).OrderBy(g => g.StartsAtLocal).ToList();
    }

    private async Task<Dictionary<string, Player>> PlayersAsync()
    {
        await using var session = app.Store.QuerySession();
        return (await session.Query<Player>().ToListAsync()).ToDictionary(p => p.Name);
    }

    private static ParticipationStatus StatusOf(Game game, Player player) => game.Find(player.Id)!.Status;

    [Fact]
    public async Task Fill_a_doubles_game()
    {
        await RunAsync(Scenarios.FillDoubles);

        var game = Assert.Single(await GamesAsync());
        var p = await PlayersAsync();
        Assert.Equal(GameStatus.Full, game.Status);
        Assert.All(new[] { "Raj", "Mike", "Sam", "Lee" }, n => Assert.Equal(ParticipationStatus.Confirmed, StatusOf(game, p[n])));
        Assert.Equal(ParticipationStatus.Declined, StatusOf(game, p["Dave"]));
        Assert.StartsWith("You're in. Game on!", app.TextsTo(SimulatorPhones.Player("Lee").Number).Last());
    }

    [Fact]
    public async Task Race_for_the_last_spot()
    {
        await RunAsync(Scenarios.RaceForLastSpot);

        var game = Assert.Single(await GamesAsync());
        var p = await PlayersAsync();
        Assert.Equal(GameStatus.Full, game.Status);
        Assert.Equal(2, game.ConfirmedCount);
        Assert.Equal(
            [ParticipationStatus.Confirmed, ParticipationStatus.Closed],
            new[] { StatusOf(game, p["Dave"]), StatusOf(game, p["Sam"]) }.Order());
    }

    [Fact]
    public async Task Drop_and_refill()
    {
        await RunAsync(Scenarios.DropAndRefill);

        var game = Assert.Single(await GamesAsync());
        var p = await PlayersAsync();
        Assert.Equal(GameStatus.Full, game.Status);
        Assert.Equal(ParticipationStatus.Dropped, StatusOf(game, p["Dave"]));
        Assert.Equal(ParticipationStatus.Confirmed, StatusOf(game, p["Kim"]));
        Assert.StartsWith("You're in. Game on!", app.TextsTo(SimulatorPhones.Player("Kim").Number).Last());
    }

    [Fact]
    public async Task Two_invitations()
    {
        await RunAsync(Scenarios.TwoInvitations);

        var games = await GamesAsync();
        var ana = (await PlayersAsync())["Ana"];
        Assert.Equal(ParticipationStatus.Invited, StatusOf(games[0], ana));
        Assert.Equal(ParticipationStatus.Confirmed, StatusOf(games[1], ana));
        Assert.Contains(app.TextsTo(SimulatorPhones.Player("Ana").Number), t => t.StartsWith("Which game? 1) "));
    }

    [Fact]
    public async Task Stop_mid_game()
    {
        await RunAsync(Scenarios.StopMidGame);

        var games = await GamesAsync();
        var tom = (await PlayersAsync())["Tom"];
        Assert.Equal(ConsentStatus.OptedOut, tom.ConsentStatus);
        Assert.Equal(ParticipationStatus.Dropped, StatusOf(games[0], tom));
        Assert.Equal(ParticipationStatus.Closed, StatusOf(games[1], tom));
        Assert.Equal(GeneralMessages.StopConfirmed("TennisSMS"), app.TextsTo(SimulatorPhones.Player("Tom").Number).Last());
    }

    [Fact]
    public async Task Stranger()
    {
        var before = app.TextsTo(SimulatorPhones.Unknown.Number).Count;
        await RunAsync(Scenarios.Stranger);

        Assert.Equal([GeneralMessages.UnknownNumberJoin], app.TextsTo(SimulatorPhones.Unknown.Number).Skip(before));
    }

    [Fact]
    public async Task Auto_replies_go_through_the_queue_and_pause_holds_them_until_step()
    {
        var engine = Engine;
        await engine.ResetAsync();
        await app.Host.ExecuteAndWaitAsync(_ => engine.JoinAllAsync());
        Assert.True(await engine.WaitForAllJoinedAsync(TimeSpan.FromSeconds(30)));

        engine.MinDelay = engine.MaxDelay = TimeSpan.Zero;
        engine.SetMode(SimulatorPhones.Player("Mike").Number, AutoReplyMode.AlwaysYes);
        engine.SetMode(SimulatorPhones.Player("Dave").Number, AutoReplyMode.AlwaysNo);
        engine.SetPaused(true);
        try
        {
            await app.Host.ExecuteAndWaitAsync(_ => engine.QuickGameAsync());
            await Eventually(() => engine.Pending.Count == 2);
            await Task.Delay(500);
            Assert.Equal(2, engine.Pending.Count); // paused: the loop releases nothing

            await app.Host.ExecuteAndWaitAsync(_ => engine.StepAsync());
            await app.Host.ExecuteAndWaitAsync(_ => engine.StepAsync());
            Assert.Empty(engine.Pending);

            var game = Assert.Single(await GamesAsync());
            var p = await PlayersAsync();
            Assert.Equal(ParticipationStatus.Confirmed, StatusOf(game, p["Mike"]));
            Assert.Equal(ParticipationStatus.Declined, StatusOf(game, p["Dave"]));
            Assert.Equal(ParticipationStatus.Invited, StatusOf(game, p["Sam"])); // Manual: no reply
        }
        finally
        {
            engine.SetPaused(false);
            engine.MinDelay = TimeSpan.FromSeconds(2);
            engine.MaxDelay = TimeSpan.FromSeconds(8);
            await engine.ResetAsync();
        }
    }

    [Fact]
    public async Task The_page_snapshot_shows_threads_timers_and_the_event_timeline()
    {
        await RunAsync(Scenarios.FillDoubles);
        var snapshot = await app.Services.GetRequiredService<SimulatorView>().LoadAsync();

        Assert.Equal(10, snapshot.Phones.Count);
        var lee = snapshot.Phones.Single(x => x.Phone.Name == "Lee");
        Assert.Equal(ConsentStatus.OptedIn, lee.Consent);
        Assert.Contains(lee.Thread, m => m.Outcome == "AcceptedAndFilled");

        Assert.Equal(["Not-full alert", "Reminder", "Game start"], snapshot.Timers.Select(t => t.Timer));
        Assert.StartsWith("scheduled", snapshot.Timers[0].Status); // full, so it will skip
        Assert.Contains(snapshot.Timeline, e => e.Type == nameof(GameFilled));
        Assert.Contains(snapshot.Timeline, e => e.Type == nameof(InvitationDeclined) && e.Details.StartsWith("Dave"));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
        Assert.True(condition());
    }
}

public class SimulatorHelperTests
{
    [Theory]
    [InlineData("TennisSMS: Doubles Fri 11/13 1-2pm. 2 spots open. Reply YES or NO, or tap http://x/g/abc", AutoReplyMode.AlwaysYes, "YES")]
    [InlineData("TennisSMS: Doubles Fri 11/13 1-2pm. 2 spots open. Reply YES or NO, or tap http://x/g/abc", AutoReplyMode.AlwaysNo, "NO")]
    [InlineData("TennisSMS: Doubles Fri 11/13 1-2pm. 2 spots open. Reply YES or NO, or tap http://x/g/abc", AutoReplyMode.YesThenOut, "YES")]
    [InlineData("Which game? 1) Fri 11/13 1pm 2) Sun 11/15 9am. Reply 1 or 2, or use the links.", AutoReplyMode.AlwaysYes, "1")]
    [InlineData("Game on! Fri 11/13 1-2pm: Phil, Mike, Dave, Sam.", AutoReplyMode.AlwaysYes, null)]
    public void Auto_replies_answer_invitations_only(string body, AutoReplyMode mode, string? expected) =>
        Assert.Equal(expected, AutoReplies.ReplyTo(body, mode, new Random(1)));

    [Fact]
    public void Random_mode_mixes_yes_no_and_silence()
    {
        var random = new Random(42);
        var replies = Enumerable.Range(0, 400)
            .Select(_ => AutoReplies.ReplyTo("... Reply YES or NO, or tap x", AutoReplyMode.Random, random))
            .ToList();
        Assert.InRange(replies.Count(r => r == "YES"), 150, 250);
        Assert.InRange(replies.Count(r => r == "NO"), 60, 140);
        Assert.InRange(replies.Count(r => r is null), 60, 140);
    }

    [Fact]
    public void Links_in_texts_are_found()
    {
        var parts = SimulatorView.Linkify("Change: tennis is now Sat. http://localhost:5199/g/abc123").ToList();
        Assert.Equal([("Change: tennis is now Sat. ", false), ("http://localhost:5199/g/abc123", true)], parts);
    }
}
