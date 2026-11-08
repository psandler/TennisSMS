using System.Security.Cryptography;
using Polecat;
using TennisSms.Core.Games;
using TennisSms.Web.Sms;
using Wolverine;

namespace TennisSms.Web.Games;

// Organizer commands. The Blazor pages send these in-process with IMessageBus.InvokeAsync;
// a GameRuleException comes back to the page as the message to show.
public sealed record ScheduleGame(Guid GameId, GameFormat Format, DateTime StartsAtLocal, DateTime EndsAtLocal, string Location);
public sealed record AddAndInvite(Guid GameId, Guid[] Add, Guid[] Invite);
public sealed record ReinviteClosedPlayers(Guid GameId);
public sealed record RemovePlayerFromGame(Guid GameId, Guid PlayerId);
public sealed record ChangeGameDetails(Guid GameId, DateTime StartsAtLocal, DateTime EndsAtLocal, string Location);
public sealed record CancelGame(Guid GameId);

// Player replies. SMS and the game page both end up here, with a different Channel.
public enum GameReply { Yes, No, Out }
public sealed record ReplyToGame(Guid GameId, Guid PlayerId, GameReply Reply, Channel Channel, Guid? InboundMessageId = null);
public sealed record GameReplyResult(ReplyOutcome Outcome);

public static class GameCommandHandler
{
    public static OutgoingMessages Handle(ScheduleGame cmd, IDocumentSession session, GameWork work)
    {
        var times = work.Clock.For(cmd.StartsAtLocal, cmd.EndsAtLocal);
        var decision = GameDecider.Schedule(cmd.GameId, cmd.Format, cmd.StartsAtLocal, cmd.EndsAtLocal, cmd.Location, work.Now, times);
        session.Events.StartStream<Game>(cmd.GameId, decision.Events.ToArray());

        var messages = new OutgoingMessages();
        messages.ScheduleAll(cmd.GameId, cmd.StartsAtLocal, times, work.Now);
        return messages;
    }

    public static async Task<OutgoingMessages> Handle(AddAndInvite cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        var decision = GameDecider.AddAndInvite(
            locked.Game, locked.Context, cmd.Add, cmd.Invite, locked.Players.OptedIn, PageTokens.New);
        return [.. work.Apply(locked, decision)];
    }

    public static async Task<OutgoingMessages> Handle(ReinviteClosedPlayers cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        return [.. work.Apply(locked, GameDecider.ReinviteClosed(locked.Game, locked.Context, locked.Players.OptedIn))];
    }

    public static async Task<OutgoingMessages> Handle(RemovePlayerFromGame cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        return [.. work.Apply(locked, GameDecider.Remove(locked.Game, locked.Context, cmd.PlayerId))];
    }

    public static async Task<OutgoingMessages> Handle(ChangeGameDetails cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        var newTimes = work.Clock.For(cmd.StartsAtLocal, cmd.EndsAtLocal);
        var decision = GameDecider.ChangeDetails(locked.Game, locked.Context, cmd.StartsAtLocal, cmd.EndsAtLocal, cmd.Location, newTimes);

        OutgoingMessages messages = [.. work.Apply(locked, decision)];
        // Old timers notice the start moved and do nothing; these replace them.
        if (decision.Events.Count > 0 && cmd.StartsAtLocal != locked.Game.StartsAtLocal)
            messages.ScheduleAll(cmd.GameId, cmd.StartsAtLocal, newTimes, work.Now);
        return messages;
    }

    public static async Task<OutgoingMessages> Handle(CancelGame cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        return [.. work.Apply(locked, GameDecider.Cancel(locked.Game, locked.Context))];
    }

    public static async Task<(GameReplyResult, OutgoingMessages)> Handle(ReplyToGame cmd, IDocumentSession session, GameWork work, CancellationToken ct)
    {
        var (outcome, texts) = await GameReplies.ApplyAsync(session, work, cmd, ct);
        return (new GameReplyResult(outcome), [.. texts]);
    }
}

/// <summary>A player's YES / NO / OUT applied to one game. Shared by the SMS pipeline and the game page.</summary>
public static class GameReplies
{
    public static async Task<(ReplyOutcome Outcome, List<SendSms> Texts)> ApplyAsync(
        IDocumentSession session, GameWork work, ReplyToGame cmd, CancellationToken ct)
    {
        var locked = await work.LockAsync(session, cmd.GameId, ct);
        var (game, ctx) = (locked.Game, locked.Context);
        var decision = cmd.Reply switch
        {
            GameReply.Yes => GameDecider.Accept(game, ctx, cmd.PlayerId, cmd.Channel, cmd.InboundMessageId),
            GameReply.No => GameDecider.Decline(game, ctx, cmd.PlayerId, cmd.Channel, cmd.InboundMessageId),
            GameReply.Out => GameDecider.Drop(game, ctx, cmd.PlayerId, cmd.Channel, cmd.InboundMessageId),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd)),
        };
        return (decision.Outcome, work.Apply(locked, decision));
    }
}

/// <summary>Game page tokens (spec §7): short, random, URL-safe, no look-alike characters.</summary>
public static class PageTokens
{
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    public const int Length = 9;

    public static string New() => RandomNumberGenerator.GetString(Alphabet, Length);
}
