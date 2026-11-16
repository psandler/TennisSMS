using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Inbound;
using TennisSms.Web.Sms;

namespace TennisSms.Web.Organizer;

public sealed record GameDetail(Game Game, IReadOnlyDictionary<Guid, Player> Players, IReadOnlyList<MessageLog> History)
{
    public string NameOf(Guid playerId) => Players.TryGetValue(playerId, out var p) ? p.Name : "Unknown";

    public IEnumerable<Participant> With(ParticipationStatus status) =>
        Game.WithStatus(status).OrderBy(p => p.ConfirmedAt).ThenBy(p => NameOf(p.PlayerId));

    /// <summary>
    /// Opted-in players who can still be invited or added: not in, not invited, not declined.
    /// Players who dropped out are here too, but only to be added back as playing (see <see cref="AddOnly"/>).
    /// </summary>
    public IEnumerable<Player> Eligible => Players.Values
        .Where(p => p.ConsentStatus == ConsentStatus.OptedIn)
        .Where(p => Game.Find(p.Id)?.Status is null or ParticipationStatus.Closed or ParticipationStatus.Dropped or ParticipationStatus.Removed)
        .OrderBy(p => p.Name);

    /// <summary>Players who dropped out: the organizer can add them back as playing, but not invite them again.</summary>
    public IReadOnlySet<Guid> AddOnly => Game.WithStatus(ParticipationStatus.Dropped).Select(p => p.PlayerId).ToHashSet();
}

/// <summary>What the player's game page (spec §7) shows.</summary>
public sealed record PlayerGamePage(
    Game Game, string Token, Guid PlayerId, string PlayerName, ParticipationStatus Status,
    IReadOnlyList<string> Confirmed, IReadOnlyList<string> Invited, bool CanAct, string? ReadOnlyReason);

/// <summary>Reads for the organizer pages and the player game page.</summary>
public sealed class OrganizerQueries(IDocumentStore store, GameClock clock, TimeProvider time)
{
    /// <summary>How long after a game ends its page links keep working (spec §7).</summary>
    public static readonly TimeSpan PageLinkGrace = TimeSpan.FromDays(7);

    public DateTime NowLocal => clock.ToLocal(time.GetUtcNow());
    public DateTime ToLocal(DateTimeOffset instant) => clock.ToLocal(instant);

    public async Task<List<Game>> UpcomingGamesAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return await GameQueries.NotOverAsync(session, NowLocal, ct);
    }

    public async Task<GameDetail?> GameAsync(Guid id, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var game = await session.LoadAsync<Game>(id, ct);
        if (game is null) return null;

        var players = (await session.Query<Player>().ToListAsync(ct)).ToDictionary(p => p.Id);
        var history = (await session.Query<MessageLog>().Where(m => m.GameId == id).ToListAsync(ct)).OrderBy(m => m.At).ToList();
        return new GameDetail(game, players, history);
    }

    public async Task<List<Player>> PlayersAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        return (await session.Query<Player>().ToListAsync(ct)).OrderBy(p => p.Name).ToList();
    }

    public async Task<List<UnknownSender>> UnknownSendersAsync(int take = 20, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var known = (await session.Query<Player>().ToListAsync(ct)).Select(p => p.PhoneE164).ToHashSet();
        return (await session.Query<UnknownSender>().ToListAsync(ct))
            .Where(u => !known.Contains(u.Id)) // added since
            .OrderByDescending(u => u.LastSeenAt).Take(take).ToList();
    }

    /// <summary>Null when the token is unknown or the game ended more than a week ago (404).</summary>
    public async Task<PlayerGamePage?> PlayerPageAsync(string token, CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var link = await session.LoadAsync<PageLink>(token, ct);
        if (link is null) return null;
        var game = await session.LoadAsync<Game>(link.GameId, ct);
        var me = game?.Find(link.PlayerId);
        if (game is null || me is null) return null;

        var now = NowLocal;
        if (now > game.EndsAtLocal + PageLinkGrace) return null;

        var players = (await session.Query<Player>().ToListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
        string Name(Participant p) => players.GetValueOrDefault(p.PlayerId, "Someone");

        var readOnly = game.Status switch
        {
            GameStatus.Cancelled => "This game was cancelled.",
            GameStatus.Started when now >= game.EndsAtLocal => "This game is over.",
            GameStatus.Started => "This game has started.",
            _ when now >= game.EndsAtLocal => "This game is over.",
            _ => null,
        };

        return new PlayerGamePage(
            game, token, link.PlayerId, players.GetValueOrDefault(link.PlayerId, "Player"), me.Status,
            game.WithStatus(ParticipationStatus.Confirmed).OrderBy(p => p.ConfirmedAt).Select(Name).ToList(),
            game.WithStatus(ParticipationStatus.Invited).Select(Name).OrderBy(n => n).ToList(),
            CanAct: readOnly is null,
            readOnly);
    }
}

public enum RosterChoice { None, Invite, Add }

public static class RosterChoices
{
    public static Guid[] Add(this Dictionary<Guid, RosterChoice> choices) =>
        choices.Where(c => c.Value == RosterChoice.Add).Select(c => c.Key).ToArray();

    public static Guid[] Invite(this Dictionary<Guid, RosterChoice> choices) =>
        choices.Where(c => c.Value == RosterChoice.Invite).Select(c => c.Key).ToArray();
}
