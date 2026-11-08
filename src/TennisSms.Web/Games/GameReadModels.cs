using JasperFx.Events;
using Polecat;
using Polecat.Linq;
using Polecat.Projections;
using TennisSms.Core.Games;

namespace TennisSms.Web.Games;

/// <summary>Maps a game page token back to its player and game (spec §7). Id = token.</summary>
public sealed class PageLink
{
    public string Id { get; set; } = "";
    public Guid GameId { get; set; }
    public Guid PlayerId { get; set; }
}

/// <summary>Inline multi-stream projection: one document per token, written with the game events.</summary>
public sealed partial class PageLinkProjection : MultiStreamProjection<PageLink, string>
{
    public PageLinkProjection()
    {
        Identity<IEvent<PlayerInvited>>(e => e.Data.PageToken);
        Identity<IEvent<PlayerAddedByOrganizer>>(e => e.Data.PageToken);
    }

    public PageLink Create(IEvent<PlayerInvited> e) => new() { Id = e.Data.PageToken, GameId = e.StreamId, PlayerId = e.Data.PlayerId };
    public PageLink Create(IEvent<PlayerAddedByOrganizer> e) => new() { Id = e.Data.PageToken, GameId = e.StreamId, PlayerId = e.Data.PlayerId };

    // A re-invite or an add after an invite reuses the token; the mapping doesn't change.
    public void Apply(IEvent<PlayerInvited> e, PageLink link) { }
    public void Apply(IEvent<PlayerAddedByOrganizer> e, PageLink link) { }
}

/// <summary>What a player is in or invited to, for the reply resolver (spec §6.3) and STOP (§4.2).</summary>
public sealed record PlayerGame(Game Game, ParticipationStatus Status);

/// <summary>
/// Queries over the Game snapshot documents. These stand in for the spec's PlayerOpenItems
/// and UpcomingGames read models: there are only ever a handful of games that haven't
/// ended, so loading them and filtering in memory is one small query, and it can't drift
/// from the game state. Swap in projections if that stops being true.
/// </summary>
public static class GameQueries
{
    /// <summary>Games that haven't ended yet, soonest first (any status, including cancelled).</summary>
    public static async Task<List<Game>> NotOverAsync(IQuerySession session, DateTime nowLocal, CancellationToken ct)
    {
        var games = await session.Query<Game>().Where(g => g.EndsAtLocal > nowLocal).ToListAsync(ct);
        return games.OrderBy(g => g.StartsAtLocal).ToList();
    }

    public static async Task<List<PlayerGame>> ForPlayerAsync(IQuerySession session, Guid playerId, DateTime nowLocal, CancellationToken ct)
    {
        var games = await NotOverAsync(session, nowLocal, ct);
        return games
            .Select(g => (Game: g, Me: g.Find(playerId)))
            .Where(x => x.Me is not null)
            .Select(x => new PlayerGame(x.Game, x.Me!.Status))
            .ToList();
    }
}
