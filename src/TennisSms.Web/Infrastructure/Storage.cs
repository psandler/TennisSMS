using JasperFx;
using JasperFx.Events.Projections;
using Microsoft.Data.SqlClient;
using Polecat;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using Wolverine;
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
