using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Web.Games;

namespace TennisSms.Web.Inbound;

public abstract record Resolution;
public sealed record ResolvedToGame(Guid GameId) : Resolution;
public sealed record NeedsChoice(IReadOnlyList<Game> Games) : Resolution;
public sealed record NothingToApply(string Reply) : Resolution;

/// <summary>
/// Which game a YES / NO / OUT applies to (spec §6.3). Its own step so other strategies
/// (e.g. "yes fri") can be added later without touching the pipeline.
/// </summary>
public static class ReplyResolver
{
    public static Resolution Resolve(GameReply reply, IReadOnlyList<PlayerGame> mine)
    {
        var live = mine.Where(x => !x.Game.IsClosedForChanges).ToList();
        List<PlayerGame> With(ParticipationStatus s) => live.Where(x => x.Status == s).ToList();

        var candidates = reply switch
        {
            GameReply.Out => With(ParticipationStatus.Confirmed) is { Count: > 0 } confirmed
                ? confirmed
                : With(ParticipationStatus.Invited), // OUT before saying yes means no
            _ => With(ParticipationStatus.Invited),
        };

        if (candidates.Count == 1) return new ResolvedToGame(candidates[0].Game.Id);
        if (candidates.Count > 1)
            return new NeedsChoice(candidates.Take(GeneralMessages.MaxChoices).Select(x => x.Game).ToList());

        // Nothing open to apply it to. If they're part of exactly one game, let the game rules
        // answer ("sorry, filled", "already started", "you're already in"...).
        if (mine.Count == 1) return new ResolvedToGame(mine[0].Game.Id);

        return new NothingToApply(reply == GameReply.Out ? GeneralMessages.NoUpcomingGames : GeneralMessages.NoOpenInvites);
    }
}
