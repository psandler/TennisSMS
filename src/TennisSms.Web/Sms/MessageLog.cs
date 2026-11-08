namespace TennisSms.Web.Sms;

public enum MessageDirection { Inbound, Outbound }

/// <summary>
/// Every text in or out (spec §8.2), stored as a plain Polecat document. Backs the simulator
/// and the organizer's per-game message history. Inbound entries carry how the reply was
/// understood and what came of it.
/// </summary>
public sealed class MessageLog
{
    /// <summary>Inbound: <see cref="InboundSms.MessageId"/>. Outbound: <see cref="SendSms.MessageId"/>.</summary>
    public Guid Id { get; set; }

    public MessageDirection Direction { get; set; }

    /// <summary>The other party's number: sender for inbound, recipient for outbound.</summary>
    public string Phone { get; set; } = "";

    public Guid? PlayerId { get; set; }
    public Guid? GameId { get; set; }
    public string Body { get; set; } = "";
    public DateTimeOffset At { get; set; }

    /// <summary>Outbound: which template (e.g. "Invite", "StopConfirmed").</summary>
    public string? Kind { get; set; }

    /// <summary>Inbound: the parsed intent (e.g. "Accept").</summary>
    public string? Intent { get; set; }

    /// <summary>Inbound: what happened (e.g. "Accepted", "GameFull", "AskedWhichGame", "UnknownSender").</summary>
    public string? Outcome { get; set; }

    public string? ProviderMessageId { get; set; }

    /// <summary>Outbound: whether the provider accepted it.</summary>
    public bool? Accepted { get; set; }

    public string? Error { get; set; }
}
