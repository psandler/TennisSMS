using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.SqlClient;
using Polecat;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Web.Games;
using TennisSms.Web.Sms;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Polecat;

namespace TennisSms.Web.Infrastructure;

/// <summary>
/// Polecat + Wolverine setup (spec §3.1). Shared by the app and the integration tests so
/// both run exactly the same configuration.
/// </summary>
public static class Storage
{
    public static IServiceCollection AddTennisSmsStorage(this IServiceCollection services, string connectionString)
    {
        services.AddPolecat(opts =>
            {
                opts.Connection(connectionString);

                // Aggregates are snapshotted inline: the state document is written in the
                // same transaction as the events.
                opts.Projections.Snapshot<Game>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<Player>(SnapshotLifecycle.Inline);

                opts.Schema.For<Player>().UniqueIndex(x => x.PhoneE164);

                // Read models (spec §4.5), written in the same transaction as the events.
                opts.Projections.Add<PageLinkProjection>(ProjectionLifecycle.Inline);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        return services;
    }

    public static void ConfigureTennisSmsWolverine(this WolverineOptions opts)
    {
        // One app instance (spec §3): no leader election.
        opts.Durability.Mode = DurabilityMode.Solo;

        // Each handler's Polecat changes and outgoing messages commit in one transaction.
        opts.Policies.AutoApplyTransactions();

        opts.ApplicationAssembly = typeof(Storage).Assembly;

        // Every local queue is backed by the database: cascaded texts, scheduled timers and
        // inbound texts survive a restart (spec §6.1, §8.2, §9).
        opts.Policies.UseDurableLocalQueues();

        // Inbound texts and outgoing texts are each handled one at a time, in order.
        opts.LocalQueueFor<InboundSms>().Sequential();
        opts.LocalQueueFor<SendSms>().Sequential();

        // Two commands for the same game: the second waits on the stream lock, and if it
        // still loses a race it's re-run from the top against the new state (spec §4.4).
        opts.OnException<JasperFx.Events.StreamLockedException>()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
        opts.OnException<JasperFx.ConcurrencyException>()
            .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Creates the database if it doesn't exist. Development and tests only.</summary>
    public static async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        builder.InitialCatalog = "master";

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID(@name) IS NULL CREATE DATABASE [{name.Replace("]", "]]")}]";
        cmd.Parameters.AddWithValue("@name", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
