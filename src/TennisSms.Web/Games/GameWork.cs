using JasperFx.Events;
using Microsoft.Extensions.Options;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Infrastructure;
using TennisSms.Web.Sms;

namespace TennisSms.Web.Games;

/// <summary>
/// All players, loaded once per command. The roster is a few dozen people, so one small
/// query beats per-id lookups and gives names, phones, consent and the organizer's player id.
/// </summary>
public sealed class PlayerDirectory
{
    private readonly Dictionary<Guid, Player> _byId;

    private PlayerDirectory(IEnumerable<Player> players, string organizerPhone)
    {
        _byId = players.ToDictionary(p => p.Id);
        OrganizerPlayerId = _byId.Values.FirstOrDefault(p => p.PhoneE164 == organizerPhone)?.Id;
    }

    public static async Task<PlayerDirectory> LoadAsync(IQuerySession session, string organizerPhone, CancellationToken ct)
    {
        var players = await session.Query<Player>().ToListAsync(ct);
        return new PlayerDirectory(players, organizerPhone);
    }

    public Guid? OrganizerPlayerId { get; }
    public Player? Find(Guid id) => _byId.GetValueOrDefault(id);
    public IReadOnlyDictionary<Guid, string> Names => _byId.ToDictionary(kv => kv.Key, kv => kv.Value.Name);

    public IReadOnlySet<Guid> OptedIn =>
        _byId.Values.Where(p => p.ConsentStatus == ConsentStatus.OptedIn).Select(p => p.Id).ToHashSet();
}

/// <summary>A game loaded under the exclusive stream lock, ready for a decision.</summary>
public sealed class GameUnderLock(IEventStream<Game> stream, GameContext context, PlayerDirectory players)
{
    public IEventStream<Game> Stream { get; } = stream;
    public Game Game { get; } = stream.Aggregate!;
    public GameContext Context { get; } = context;
    public PlayerDirectory Players { get; } = players;
}

/// <summary>
/// The shared steps of every game command: load the game with the exclusive lock (spec §4.4),
/// apply a decision's events, and turn its notices into <see cref="SendSms"/> messages.
/// </summary>
public sealed class GameWork(GameClock clock, TimeProvider time, IOptions<TennisSmsOptions> options)
{
    private TennisSmsOptions Options => options.Value;

    public DateTimeOffset Now => time.GetUtcNow();
    public DateTime NowLocal => clock.ToLocal(Now);
    public GameClock Clock => clock;

    public async Task<GameUnderLock> LockAsync(IDocumentSession session, Guid gameId, CancellationToken ct) =>
        await TryLockAsync(session, gameId, ct) ?? throw new GameRuleException("That game doesn't exist.");

    /// <summary>Null when there's no such game (e.g. a timer for a game wiped by a simulator reset).</summary>
    public async Task<GameUnderLock?> TryLockAsync(IDocumentSession session, Guid gameId, CancellationToken ct)
    {
        // UPDLOCK, HOLDLOCK on the stream row: a second command for this game waits until
        // the first commits, then sees its result. This is what keeps two YESes from both
        // taking the last spot.
        var stream = await session.Events.FetchForExclusiveWriting<Game>(gameId, ct);
        if (stream.Aggregate is null) return null;

        var players = await PlayerDirectory.LoadAsync(session, Options.Organizer.PhoneE164, ct);
        var game = stream.Aggregate;
        var context = new GameContext(Now, clock.For(game.StartsAtLocal, game.EndsAtLocal), players.OrganizerPlayerId);
        return new GameUnderLock(stream, context, players);
    }

    /// <summary>Appends the decision's events and returns its texts, rendered against the new state.</summary>
    public List<SendSms> Apply(GameUnderLock locked, Decision decision)
    {
        if (decision.Events.Count > 0)
            locked.Stream.AppendMany(decision.Events.ToArray());

        var after = locked.Game.Clone();
        foreach (var e in decision.Events) GameEvolver.Apply(after, e);
        return Render(after, decision.Notices, locked.Players);
    }

    public List<SendSms> Render(Game game, IEnumerable<Notice> notices, PlayerDirectory players)
    {
        var context = new GameMessageContext
        {
            Game = game,
            Names = players.Names,
            PlayerLink = id => PlayerLink(game.Find(id)?.PageToken),
            OrganizerLink = OrganizerLink(game.Id),
            OrganizerName = Options.Organizer.Name,
            Brand = Options.Brand,
            CancellationCutoffLocal = game.StartsAtLocal - clock.Policy.CancellationNotice,
            CancellationNoticeHours = (int)clock.Policy.CancellationNotice.TotalHours,
        };

        var texts = new List<SendSms>();
        foreach (var notice in notices)
        {
            var body = MessageComposer.Render(notice, context);
            if (notice.To.PlayerId is { } playerId)
            {
                // Consent is checked again here: nothing goes to a player who isn't opted in.
                var player = players.Find(playerId);
                if (player is null || player.ConsentStatus != ConsentStatus.OptedIn) continue;
                texts.Add(SendSms.Create(player.PhoneE164, body, notice.Kind.ToString(), playerId, game.Id));
            }
            else
            {
                texts.Add(SendSms.Create(Options.Organizer.PhoneE164, body, notice.Kind.ToString(), players.OrganizerPlayerId, game.Id));
            }
        }
        return texts;
    }

    public string PlayerLink(string? pageToken) => $"{Options.BaseUrl}/g/{pageToken}";
    public string OrganizerLink(Guid gameId) => $"{Options.BaseUrl}/games/{gameId}";
}
