using Microsoft.Extensions.Options;
using TennisSms.Web.Infrastructure;
using Wolverine;

namespace TennisSms.Web.Sms;

/// <summary>A text the simulator saw go out (to a fake phone).</summary>
public sealed record SimulatedText(Guid MessageId, string To, string Body, string ProviderMessageId, DateTimeOffset At);

/// <summary>
/// Development gateway (spec §8.1, §11.3). Sending hands the text to the simulator's fake
/// phones; a fake phone "texting" us goes through <see cref="ReceiveAsync"/>, which produces
/// <see cref="InboundSms"/> and publishes it to the durable inbound queue exactly as the
/// Twilio webhook will.
/// </summary>
public sealed class SimulatorGateway(
    IServiceScopeFactory scopes, IOptions<TennisSmsOptions> options, TimeProvider time) : ISmsSender
{
    /// <summary>Raised for every text sent to a fake phone. The simulator page and auto-responders listen.</summary>
    public event Action<SimulatedText>? TextSent;

    public Task<SendResult> SendAsync(OutboundSms sms, CancellationToken ct = default)
    {
        var providerId = "SIM" + sms.MessageId.ToString("N")[..12];
        TextSent?.Invoke(new SimulatedText(sms.MessageId, sms.To, sms.Body, providerId, time.GetUtcNow()));
        return Task.FromResult(SendResult.Ok(providerId));
    }

    /// <summary>A fake phone sends us a text.</summary>
    public async Task<InboundSms> ReceiveAsync(string fromE164, string body, CancellationToken ct = default)
    {
        var sms = new InboundSms(
            Guid.NewGuid(), fromE164, options.Value.ServiceNumber, body,
            ProviderMessageId: "SIMIN" + Guid.NewGuid().ToString("N")[..12],
            ReceivedAt: time.GetUtcNow());

        await using var scope = scopes.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.PublishAsync(sms);
        return sms;
    }
}
