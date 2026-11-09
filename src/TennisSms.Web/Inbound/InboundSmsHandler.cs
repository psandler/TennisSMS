using Microsoft.Extensions.Options;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Core.Replies;
using TennisSms.Web.Games;
using TennisSms.Web.Infrastructure;
using TennisSms.Web.Sms;
using Wolverine;

namespace TennisSms.Web.Inbound;

/// <summary>Runs on the durable, sequential inbound queue (spec §6.1).</summary>
public static class InboundSmsHandler
{
    public static Task<OutgoingMessages> Handle(InboundSms sms, IDocumentSession session, InboundProcessor processor, CancellationToken ct) =>
        processor.ProcessAsync(sms, session, ct);
}

/// <summary>
/// The inbound pipeline (spec §6.1): log the text, find the sender, classify it, work out
/// which game it's about, and apply it. Everything commits in the handler's one transaction,
/// together with the replies.
/// </summary>
public sealed class InboundProcessor(GameWork work, IOptions<TennisSmsOptions> options)
{
    private TennisSmsOptions Options => options.Value;

    public async Task<OutgoingMessages> ProcessAsync(InboundSms sms, IDocumentSession session, CancellationToken ct)
    {
        // The durable inbox may redeliver after a crash; a text already logged was handled.
        if (await session.LoadAsync<MessageLog>(sms.MessageId, ct) is not null) return [];

        var parsed = ReplyParser.Parse(sms.Body);
        var run = new Run(sms, parsed, session, ct, new MessageLog
        {
            Id = sms.MessageId,
            Direction = MessageDirection.Inbound,
            Phone = sms.From,
            Body = sms.Body,
            At = sms.ReceivedAt,
            ProviderMessageId = sms.ProviderMessageId,
            Intent = parsed.Intent.ToString(),
        });

        var player = await session.Query<Player>().FirstOrDefaultAsync(p => p.PhoneE164 == sms.From, ct);
        if (player is null)
            await FromUnknownNumber(run);
        else
            await FromPlayer(run, player);

        session.Store(run.Log);
        return run.Outgoing;
    }

    /// <summary>One inbound text being processed.</summary>
    private sealed record Run(InboundSms Sms, ParsedReply Parsed, IDocumentSession Session, CancellationToken Ct, MessageLog Log)
    {
        public OutgoingMessages Outgoing { get; } = [];
        public string Outcome { set => Log.Outcome = value; }
    }

    // ---------- Known players ----------

    private async Task FromPlayer(Run run, Player player)
    {
        run.Log.PlayerId = player.Id;
        void Reply(string body, string kind) => run.Outgoing.Add(SendSms.Create(player.PhoneE164, body, kind, player.Id));

        switch (run.Parsed.Intent)
        {
            case ReplyIntent.Join:
                var optIn = PlayerRules.OptIn(player, run.Parsed.Keyword ?? "JOIN", run.Sms.MessageId, work.Now);
                if (optIn is not null) run.Session.Events.Append(player.Id, optIn);
                Reply(GeneralMessages.JoinConfirmed(Options.Brand), "JoinConfirmed");
                run.Outcome = optIn is null ? "AlreadyOptedIn" : "OptedIn";
                return;

            case ReplyIntent.Stop:
                await OptOut(run, player);
                Reply(GeneralMessages.StopConfirmed(Options.Brand), "StopConfirmed");
                return;

            case ReplyIntent.Help:
                Reply(GeneralMessages.Help(Options.Brand), "Help");
                run.Outcome = "Help";
                return;
        }

        // Past this point the player must be opted in. After STOP, nothing goes back except
        // to JOIN / HELP / STOP (spec §4.2); a seeded player who hasn't joined gets one nudge.
        switch (player.ConsentStatus)
        {
            case ConsentStatus.OptedOut:
                run.Outcome = "IgnoredOptedOut";
                return;
            case ConsentStatus.Pending:
                Reply(GeneralMessages.NotSignedUp, "NotSignedUp");
                run.Outcome = "NotSignedUp";
                return;
        }

        switch (run.Parsed.Intent)
        {
            case ReplyIntent.Accept: await GameReplyAsync(run, player, GameReply.Yes); return;
            case ReplyIntent.Decline: await GameReplyAsync(run, player, GameReply.No); return;
            case ReplyIntent.Drop: await GameReplyAsync(run, player, GameReply.Out); return;
            case ReplyIntent.Choice: await ChoiceAsync(run, player); return;
            default:
                Reply(GeneralMessages.Unrecognized, "Unrecognized");
                run.Outcome = "Unrecognized";
                return;
        }
    }

    /// <summary>STOP (spec §4.2): opt out and, in the same transaction, leave every game they're in or invited to.</summary>
    private async Task OptOut(Run run, Player player)
    {
        var optOut = PlayerRules.OptOut(player, OptOutSource.StopKeyword, run.Sms.MessageId, work.Now);
        if (optOut is null)
        {
            run.Outcome = "AlreadyOptedOut";
            return;
        }

        run.Session.Events.Append(player.Id, optOut);
        var mine = await GameQueries.ForPlayerAsync(run.Session, player.Id, work.NowLocal, run.Ct);
        foreach (var pg in mine.Where(x => !x.Game.IsClosedForChanges &&
                                           x.Status is ParticipationStatus.Invited or ParticipationStatus.Confirmed))
        {
            var locked = await work.LockAsync(run.Session, pg.Game.Id, run.Ct);
            run.Outgoing.AddRange(work.Apply(locked, GameDecider.PlayerOptedOut(locked.Game, locked.Context, player.Id)));
        }
        run.Outcome = "OptedOut";
    }

    private async Task GameReplyAsync(Run run, Player player, GameReply reply)
    {
        // A new YES / NO / OUT replaces any open "Which game?" question.
        run.Session.Delete<PendingClarification>(player.Id);

        var mine = await GameQueries.ForPlayerAsync(run.Session, player.Id, work.NowLocal, run.Ct);
        switch (ReplyResolver.Resolve(reply, mine))
        {
            case ResolvedToGame resolved:
                await ApplyToGameAsync(run, player, resolved.GameId, reply);
                break;

            case NeedsChoice choice:
                run.Session.Store(new PendingClarification
                {
                    Id = player.Id,
                    Reply = reply,
                    GameIds = choice.Games.Select(g => g.Id).ToList(),
                    ExpiresAt = work.Now + Options.ClarificationExpiry,
                });
                var question = GeneralMessages.WhichGame(choice.Games.Select(g => g.StartsAtLocal).ToList());
                run.Outgoing.Add(SendSms.Create(player.PhoneE164, question, "WhichGame", player.Id));
                run.Outcome = "AskedWhichGame";
                break;

            case NothingToApply nothing:
                run.Outgoing.Add(SendSms.Create(player.PhoneE164, nothing.Reply, "NothingToApply", player.Id));
                run.Outcome = "NothingToApply";
                break;
        }
    }

    private async Task ChoiceAsync(Run run, Player player)
    {
        var pending = await run.Session.LoadAsync<PendingClarification>(player.Id, run.Ct);
        var choice = run.Parsed.Choice ?? 0;

        if (pending is null || pending.ExpiresAt <= work.Now || choice < 1 || choice > pending.GameIds.Count)
        {
            run.Outgoing.Add(SendSms.Create(player.PhoneE164, GeneralMessages.Unrecognized, "Unrecognized", player.Id));
            run.Outcome = "UnmatchedChoice";
            return;
        }

        run.Session.Delete<PendingClarification>(player.Id);
        await ApplyToGameAsync(run, player, pending.GameIds[choice - 1], pending.Reply);
    }

    private async Task ApplyToGameAsync(Run run, Player player, Guid gameId, GameReply reply)
    {
        var (outcome, texts) = await GameReplies.ApplyAsync(
            run.Session, work, new ReplyToGame(gameId, player.Id, reply, Channel.Sms, run.Sms.MessageId), run.Ct);
        run.Log.GameId = gameId;
        run.Outcome = outcome.ToString();
        run.Outgoing.AddRange(texts);
    }

    // ---------- Unknown numbers (spec §6.4) ----------

    private async Task FromUnknownNumber(Run run)
    {
        var now = work.Now;
        var sender = await run.Session.LoadAsync<UnknownSender>(run.Sms.From, run.Ct)
                     ?? new UnknownSender { Id = run.Sms.From, FirstSeenAt = now };
        sender.LastSeenAt = now;
        sender.MessageCount++;
        sender.LastBody = run.Sms.Body;
        run.Session.Store(sender);

        bool DueToday(DateTimeOffset? last) => last is null || now - last.Value >= TimeSpan.FromDays(1);

        (string Body, string Kind, Action MarkSent)? reply = run.Parsed.Intent switch
        {
            ReplyIntent.Join when !sender.JoinReplied =>
                (GeneralMessages.UnknownNumberJoin, "UnknownNumberJoin", () => sender.JoinReplied = true),
            ReplyIntent.Stop when DueToday(sender.LastStopReplyAt) =>
                (GeneralMessages.StopConfirmed(Options.Brand), "StopConfirmed", () => sender.LastStopReplyAt = now),
            ReplyIntent.Help when DueToday(sender.LastHelpReplyAt) =>
                (GeneralMessages.UnknownNumberHelp(Options.Brand), "UnknownNumberHelp", () => sender.LastHelpReplyAt = now),
            _ => null,
        };

        if (reply is null)
        {
            run.Outcome = "UnknownSenderIgnored";
            return;
        }

        if (!await TakeUnknownReplyBudget(run))
        {
            run.Outcome = "UnknownSenderCapped";
            return;
        }

        reply.Value.MarkSent();
        run.Outgoing.Add(SendSms.Create(run.Sms.From, reply.Value.Body, reply.Value.Kind));
        run.Outcome = "UnknownSenderReplied";
    }

    /// <summary>The global hourly cap. Over it, stop replying and tell the organizer once.</summary>
    private async Task<bool> TakeUnknownReplyBudget(Run run)
    {
        var now = work.Now;
        var budget = await run.Session.LoadAsync<UnknownReplyBudget>(UnknownReplyBudget.SingletonId, run.Ct)
                     ?? new UnknownReplyBudget { WindowStart = now };

        if (now - budget.WindowStart >= TimeSpan.FromHours(1))
        {
            budget.WindowStart = now;
            budget.Count = 0;
            budget.OrganizerAlerted = false;
        }

        var allowed = budget.Count < Options.UnknownReplyCapPerHour;
        if (allowed)
        {
            budget.Count++;
        }
        else if (!budget.OrganizerAlerted)
        {
            budget.OrganizerAlerted = true;
            run.Outgoing.Add(SendSms.Create(
                Options.Organizer.PhoneE164, GeneralMessages.UnknownSendersPaused(Options.Brand), "UnknownSendersPaused"));
        }

        run.Session.Store(budget);
        return allowed;
    }
}
