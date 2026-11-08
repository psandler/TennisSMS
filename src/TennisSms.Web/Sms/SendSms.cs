using Polecat;

namespace TennisSms.Web.Sms;

/// <summary>
/// A text to send, returned by command handlers and delivered through Wolverine's outbox
/// after the handler's transaction commits (spec §8.2). Handlers never call the gateway.
/// </summary>
public sealed record SendSms(Guid MessageId, string To, string Body, string Kind, Guid? PlayerId = null, Guid? GameId = null)
{
    public static SendSms Create(string to, string body, string kind, Guid? playerId = null, Guid? gameId = null) =>
        new(Guid.NewGuid(), to, body, kind, playerId, gameId);
}

public static class SendSmsHandler
{
    public static async Task Handle(SendSms msg, IDocumentSession session, ISmsSender sender, TimeProvider time, CancellationToken ct)
    {
        // At-least-once delivery: a redelivered message that was already sent is skipped.
        if (await session.LoadAsync<MessageLog>(msg.MessageId, ct) is not null) return;

        SendResult result;
        try
        {
            result = await sender.SendAsync(new OutboundSms(msg.MessageId, msg.To, msg.Body), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = SendResult.Failed(ex.Message);
        }

        // A failed send is logged, not retried, in v0.1.
        session.Store(new MessageLog
        {
            Id = msg.MessageId,
            Direction = MessageDirection.Outbound,
            Phone = msg.To,
            PlayerId = msg.PlayerId,
            GameId = msg.GameId,
            Body = msg.Body,
            At = time.GetUtcNow(),
            Kind = msg.Kind,
            ProviderMessageId = result.ProviderMessageId,
            Accepted = result.Accepted,
            Error = result.Error,
        });
    }
}
