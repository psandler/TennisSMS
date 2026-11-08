using Polecat;
using TennisSms.Core.Games;
using TennisSms.Core.Time;
using Wolverine;

namespace TennisSms.Web.Games;

// Timers are Wolverine scheduled messages (spec §9), persisted with the transaction that
// scheduled them. Each carries the start time it was computed from; if the game has moved
// since, the timer is stale and does nothing (a newer one exists).

public interface IGameTimer
{
    Guid GameId { get; }
    DateTime StartsAtLocal { get; }
}

public sealed record NotFullAlertDue(Guid GameId, DateTime StartsAtLocal) : IGameTimer;
public sealed record ReminderDue(Guid GameId, DateTime StartsAtLocal) : IGameTimer;
public sealed record GameStartDue(Guid GameId, DateTime StartsAtLocal) : IGameTimer;

public static class GameTimers
{
    /// <summary>The timers for a game starting at <paramref name="startsAtLocal"/>. Times already past are skipped, not sent late.</summary>
    public static IEnumerable<(IGameTimer Timer, DateTimeOffset At)> For(Guid gameId, DateTime startsAtLocal, GameTimes times, DateTimeOffset now)
    {
        if (times.NotFullAlertAt > now) yield return (new NotFullAlertDue(gameId, startsAtLocal), times.NotFullAlertAt);
        if (times.ReminderAt > now) yield return (new ReminderDue(gameId, startsAtLocal), times.ReminderAt);
        yield return (new GameStartDue(gameId, startsAtLocal), times.StartsAt);
    }

    public static void ScheduleAll(this OutgoingMessages messages, Guid gameId, DateTime startsAtLocal, GameTimes times, DateTimeOffset now)
    {
        foreach (var (timer, at) in For(gameId, startsAtLocal, times, now))
            messages.Schedule(timer, at);
    }
}

public static class GameTimerHandler
{
    public static Task<OutgoingMessages> Handle(NotFullAlertDue timer, IDocumentSession session, GameWork work, CancellationToken ct) =>
        Fire(timer, session, work, GameDecider.AlertNotFull, ct);

    public static Task<OutgoingMessages> Handle(ReminderDue timer, IDocumentSession session, GameWork work, CancellationToken ct) =>
        Fire(timer, session, work, GameDecider.Remind, ct);

    public static Task<OutgoingMessages> Handle(GameStartDue timer, IDocumentSession session, GameWork work, CancellationToken ct) =>
        Fire(timer, session, work, GameDecider.Start, ct);

    private static async Task<OutgoingMessages> Fire(
        IGameTimer timer, IDocumentSession session, GameWork work, Func<Game, GameContext, Decision> decide, CancellationToken ct)
    {
        var locked = await work.TryLockAsync(session, timer.GameId, ct);
        if (locked is null || locked.Game.StartsAtLocal != timer.StartsAtLocal) return [];
        return [.. work.Apply(locked, decide(locked.Game, locked.Context))];
    }
}
