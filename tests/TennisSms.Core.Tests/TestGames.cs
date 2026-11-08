using TennisSms.Core.Games;
using TennisSms.Core.Time;

namespace TennisSms.Core.Tests;

/// <summary>Builders for "given these events" game tests.</summary>
internal static class TestGames
{
    public static readonly GameClock Clock = GameClock.Chicago();
    public static readonly Guid GameId = Guid.Parse("00000000-0000-0000-0000-0000000000aa");

    // Friday 11/13/2026, 1-2pm Chicago.
    public static readonly DateTime Start = new(2026, 11, 13, 13, 0, 0);
    public static readonly DateTime End = Start.AddHours(1);
    public static readonly GameTimes Times = Clock.For(Start, End);

    /// <summary>Tuesday before the game: well before the cancellation cutoff.</summary>
    public static readonly DateTimeOffset Tuesday = Clock.ToUtc(new DateTime(2026, 11, 10, 12, 0, 0));

    /// <summary>Thursday 3pm: inside the 24-hour window.</summary>
    public static readonly DateTimeOffset InsideWindow = Clock.ToUtc(new DateTime(2026, 11, 12, 15, 0, 0));

    public static readonly Guid Phil = Id(1), Mike = Id(2), Dave = Id(3), Sam = Id(4), Lee = Id(5), Kim = Id(6);

    public static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

    public static GameScheduled Scheduled(GameFormat format = GameFormat.Doubles) =>
        new(GameId, format, GameFormats.CapacityOf(format), Start, End, "the club");

    public static PlayerInvited Invited(Guid id) => new(id, "tok" + id.ToString()[^4..]);

    public static PlayerAddedByOrganizer Added(Guid id, DateTimeOffset? at = null) =>
        new(id, "tok" + id.ToString()[^4..], at ?? Tuesday.AddDays(-1));

    public static Game Doubles(params object[] events) => GameEvolver.From([Scheduled(), .. events]);

    public static GameContext Ctx(DateTimeOffset? now = null, Guid? organizer = null) =>
        new(now ?? Tuesday, Times, organizer);

    public static Game After(Game game, Decision decision) =>
        decision.Events.Aggregate(game.Clone(), GameEvolver.Apply);

    public static readonly IReadOnlySet<Guid> AllOptedIn = new HashSet<Guid>(Enumerable.Range(1, 12).Select(Id));

    public static string NewToken() => Guid.NewGuid().ToString("N")[..10];
}
