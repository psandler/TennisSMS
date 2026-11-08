using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Web.Games;

namespace TennisSms.Web.Simulator;

/// <summary>One timed step of a scenario: a fake phone texting, or an organizer action.</summary>
/// <param name="Phone">The player name whose phone acts, or null for an organizer action.</param>
public sealed record ScenarioStep(TimeSpan At, string? Phone, string Label, Func<Task> Run);

public sealed record SimulatorScenario(string Name, string Description, Func<ScenarioContext, Task<List<ScenarioStep>>> Build);

/// <summary>What a scenario can do: set up games, then describe timed texts and organizer actions.</summary>
public sealed class ScenarioContext(SimulatorEngine engine, IReadOnlyList<Player> players)
{
    public IReadOnlyList<Guid> GameIds => _games;
    private readonly List<Guid> _games = [];

    public Guid PlayerId(string name) => players.Single(p => p.Name == name).Id;
    public string Phone(string name) => name == SimulatorPhones.Unknown.Name ? SimulatorPhones.Unknown.Number : SimulatorPhones.Player(name).Number;

    /// <summary>Schedules a game a few days out at the given local hour.</summary>
    public async Task<Guid> GameAsync(GameFormat format, int daysAhead, int hour, string location)
    {
        var start = engine.NowLocal.Date.AddDays(daysAhead).AddHours(hour);
        var id = Guid.NewGuid();
        await engine.InvokeAsync(new ScheduleGame(id, format, start, start.AddHours(1), location));
        _games.Add(id);
        return id;
    }

    public Task AddAndInviteAsync(Guid gameId, string[] add, string[] invite) =>
        engine.InvokeAsync(new AddAndInvite(gameId, add.Select(PlayerId).ToArray(), invite.Select(PlayerId).ToArray()));

    public ScenarioStep Text(double atSeconds, string name, string body) =>
        new(TimeSpan.FromSeconds(atSeconds), name, $"{name}: {body}", () => engine.SendAsync(Phone(name), body));

    public ScenarioStep PageTap(double atSeconds, string name, Guid gameId, GameReply reply) =>
        new(TimeSpan.FromSeconds(atSeconds), name, $"{name} taps {reply} on the game page",
            () => engine.InvokeAsync<GameReplyResult>(new ReplyToGame(gameId, PlayerId(name), reply, Channel.Page)));

    public ScenarioStep Organizer(double atSeconds, string label, object command) =>
        new(TimeSpan.FromSeconds(atSeconds), null, $"Organizer: {label}", () => engine.InvokeAsync(command));
}

/// <summary>The starting set of one-click scenarios (spec §11.3). Each one resets first.</summary>
public static class Scenarios
{
    public static readonly SimulatorScenario FillDoubles = new(
        "Fill a doubles game",
        "Nick is added; Hyo, Michael and Mike say YES, Jim says NO. The third YES fills it.",
        async c =>
        {
            var game = await c.GameAsync(GameFormat.Doubles, 3, 10, "Oak Park courts");
            await c.AddAndInviteAsync(game, ["Nick"], ["Hyo", "Jim", "Michael", "Mike"]);
            return [c.Text(2, "Hyo", "YES"), c.Text(4, "Jim", "NO"), c.Text(6, "Michael", "yes"), c.Text(8, "Mike", "Y")];
        });

    public static readonly SimulatorScenario RaceForLastSpot = new(
        "Race for the last spot",
        "Singles with Hyo in. Jim texts YES and Michael taps YES on his game page at the same instant. One wins.",
        async c =>
        {
            var game = await c.GameAsync(GameFormat.Singles, 3, 9, "Oak Park courts");
            await c.AddAndInviteAsync(game, ["Hyo"], ["Jim", "Michael"]);
            return [c.Text(3, "Jim", "YES"), c.PageTap(3, "Michael", game, GameReply.Yes)];
        });

    public static readonly SimulatorScenario DropAndRefill = new(
        "Drop and refill",
        "Game fills, Jim drops, the organizer re-invites, Ben takes the spot.",
        async c =>
        {
            var game = await c.GameAsync(GameFormat.Doubles, 3, 18, "Oak Park courts");
            await c.AddAndInviteAsync(game, ["Hyo", "Jim"], ["Michael", "Mike", "Ben"]);
            return
            [
                c.Text(2, "Michael", "YES"), c.Text(4, "Mike", "YES"),
                c.Text(7, "Jim", "OUT"),
                c.Organizer(10, "re-invite closed invitations", new ReinviteClosedPlayers(game)),
                c.Text(13, "Ben", "YES"),
            ];
        });

    public static readonly SimulatorScenario TwoInvitations = new(
        "Two invitations",
        "Jed is invited to two games and says YES: he's asked which one, and answers 2.",
        async c =>
        {
            var first = await c.GameAsync(GameFormat.Doubles, 3, 9, "Oak Park courts");
            var second = await c.GameAsync(GameFormat.Doubles, 4, 9, "Oak Park courts");
            await c.AddAndInviteAsync(first, [], ["Jed"]);
            await c.AddAndInviteAsync(second, [], ["Jed"]);
            return [c.Text(3, "Jed", "yes"), c.Text(7, "Jed", "2")];
        });

    public static readonly SimulatorScenario StopMidGame = new(
        "STOP mid-game",
        "Mike is confirmed in one game and invited to another, then texts STOP.",
        async c =>
        {
            var playing = await c.GameAsync(GameFormat.Doubles, 3, 10, "Oak Park courts");
            var invited = await c.GameAsync(GameFormat.Doubles, 4, 10, "Oak Park courts");
            await c.AddAndInviteAsync(playing, ["Mike", "Nick"], ["Jed"]);
            await c.AddAndInviteAsync(invited, [], ["Mike"]);
            return [c.Text(3, "Mike", "STOP")];
        });

    public static readonly SimulatorScenario Stranger = new(
        "Stranger",
        "The unknown phone sends JOIN twice and some junk. Only the first JOIN gets a reply.",
        c => Task.FromResult<List<ScenarioStep>>(
        [
            c.Text(2, SimulatorPhones.Unknown.Name, "JOIN"),
            c.Text(4, SimulatorPhones.Unknown.Name, "JOIN"),
            c.Text(6, SimulatorPhones.Unknown.Name, "hey who is this"),
            c.Text(8, SimulatorPhones.Unknown.Name, "yes"),
        ]));

    public static readonly IReadOnlyList<SimulatorScenario> All =
        [FillDoubles, RaceForLastSpot, DropAndRefill, TwoInvitations, StopMidGame, Stranger];
}
