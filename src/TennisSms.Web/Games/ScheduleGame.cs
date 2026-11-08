using TennisSms.Core.Games;
using TennisSms.Core.Time;
using Wolverine.Polecat;

namespace TennisSms.Web.Games;

public sealed record ScheduleGame(Guid GameId, GameFormat Format, DateTime StartsAtLocal, DateTime EndsAtLocal, string Location);

/// <summary>Starts a game stream. Timers and invitations come in the wiring step.</summary>
public static class ScheduleGameHandler
{
    public static IStartStream Handle(ScheduleGame cmd, GameClock clock, TimeProvider time)
    {
        var decision = GameDecider.Schedule(
            cmd.GameId, cmd.Format, cmd.StartsAtLocal, cmd.EndsAtLocal, cmd.Location,
            time.GetUtcNow(), clock.For(cmd.StartsAtLocal, cmd.EndsAtLocal));

        return PolecatOps.StartStream<Game>(cmd.GameId, decision.Events.ToArray());
    }
}
