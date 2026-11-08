namespace TennisSms.Web.Sms;

// The provider-neutral boundary (spec §8.1). Nothing outside an adapter knows which
// provider is in use; the simulator and Twilio both live behind these types.

/// <summary>A text to send. <paramref name="MessageId"/> is ours and makes sending idempotent.</summary>
public sealed record OutboundSms(Guid MessageId, string To, string Body);

/// <summary>What the provider said when we handed it a text.</summary>
public sealed record SendResult(bool Accepted, string? ProviderMessageId, string? Error = null)
{
    public static SendResult Ok(string providerMessageId) => new(true, providerMessageId);
    public static SendResult Failed(string error) => new(false, null, error);
}

public interface ISmsSender
{
    Task<SendResult> SendAsync(OutboundSms sms, CancellationToken ct = default);
}

/// <summary>
/// A text that arrived, as every adapter must produce it. Adapters publish it to the durable
/// inbound queue (spec §6.1); the core only ever sees this type.
/// </summary>
public sealed record InboundSms(
    Guid MessageId, string From, string To, string Body, string? ProviderMessageId, DateTimeOffset ReceivedAt);
