using Polecat;
using Polecat.Linq;
using TennisSms.Core.Players;

namespace TennisSms.Web.Players;

// The organizer seeds players by name and number. Consent only ever comes from the player
// texting JOIN (spec §4.2), so there's no organizer command to opt someone in.
public sealed record RegisterPlayer(Guid PlayerId, string Name, string Phone);
public sealed record ChangePlayerDetails(Guid PlayerId, string Name, string Phone);

public static class PlayerCommandHandler
{
    public static async Task Handle(RegisterPlayer cmd, IDocumentSession session, TimeProvider time, CancellationToken ct)
    {
        var e = PlayerRules.Register(cmd.PlayerId, cmd.Name, cmd.Phone, time.GetUtcNow());
        await RequirePhoneFree(session, e.PhoneE164, cmd.PlayerId, ct);
        session.Events.StartStream<Player>(cmd.PlayerId, e);
    }

    public static async Task Handle(ChangePlayerDetails cmd, IDocumentSession session, TimeProvider time, CancellationToken ct)
    {
        var stream = await session.Events.FetchForWriting<Player>(cmd.PlayerId, ct);
        var player = stream.Aggregate ?? throw new PlayerRuleException("That player doesn't exist.");

        var e = PlayerRules.ChangeDetails(player, cmd.Name, cmd.Phone, time.GetUtcNow());
        if (e is null) return;
        await RequirePhoneFree(session, e.PhoneE164, cmd.PlayerId, ct);
        stream.AppendOne(e);
    }

    /// <summary>A friendly check; the unique index on PhoneE164 is the real guarantee.</summary>
    private static async Task RequirePhoneFree(IQuerySession session, string phone, Guid playerId, CancellationToken ct)
    {
        var existing = await session.Query<Player>().FirstOrDefaultAsync(p => p.PhoneE164 == phone, ct);
        if (existing is not null && existing.Id != playerId)
            throw new PlayerRuleException($"{existing.Name} already has that number.");
    }
}
