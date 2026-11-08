using Microsoft.Extensions.Options;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Players;
using TennisSms.Web.Infrastructure;
using Wolverine;

namespace TennisSms.Web.Players;

/// <summary>Makes sure the configured organizer is a player: registered, opted in, with the organizer role.</summary>
public sealed record EnsureOrganizerPlayer;

public static class OrganizerSetupHandler
{
    /// <summary>Recorded as the opt-in keyword and the role's grantor, so the stream shows where they came from.</summary>
    public const string Source = "config";

    public static async Task Handle(
        EnsureOrganizerPlayer _, IDocumentSession session, IOptions<TennisSmsOptions> options, TimeProvider time,
        ILogger<EnsureOrganizerPlayer> log, CancellationToken ct)
    {
        var organizer = options.Value.Organizer;
        var now = time.GetUtcNow();
        var phone = PhoneNumbers.NormalizeUs(organizer.PhoneE164);
        var existing = await session.Query<Player>().FirstOrDefaultAsync(p => p.PhoneE164 == phone, ct);

        if (existing is null)
        {
            var id = Guid.NewGuid();
            var registered = PlayerRules.Register(id, organizer.Name, phone, now);
            var player = Player.Create(registered);
            session.Events.StartStream<Player>(id,
                registered,
                PlayerRules.OptIn(player, Source, null, now)!,
                PlayerRules.GrantOrganizer(player, Source, now)!);
            return;
        }

        var stream = await session.Events.FetchForWriting<Player>(existing.Id, ct);
        var current = stream.Aggregate!;

        // The organizer's own phone already gets login codes and alerts, so no JOIN is needed.
        // A STOP is still respected: they can text JOIN to come back.
        if (current.ConsentStatus == ConsentStatus.Pending)
            stream.AppendOne(PlayerRules.OptIn(current, Source, null, now)!);
        else if (current.ConsentStatus == ConsentStatus.OptedOut)
            log.LogWarning("The organizer ({Name}) has texted STOP. They can't be added to games until they text JOIN.", current.Name);

        if (PlayerRules.GrantOrganizer(current, Source, now) is { } granted)
            stream.AppendOne(granted);
    }
}

/// <summary>Runs <see cref="EnsureOrganizerPlayer"/> once the app (and Wolverine) has started.</summary>
public sealed class OrganizerSetup(IServiceScopeFactory scopes, ILogger<OrganizerSetup> log) : IHostedLifecycleService
{
    public async Task StartedAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(new EnsureOrganizerPlayer(), ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Couldn't set up the organizer as a player.");
        }
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;
}
