using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Infrastructure;
using Wolverine;
using Wolverine.Tracking;

namespace TennisSms.IntegrationTests;

/// <summary>
/// Boots Polecat + Wolverine against a real SQL Server 2025 database, with the app's own
/// configuration. Override the connection with TENNISSMS_TEST_DB.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TENNISSMS_TEST_DB")
        ?? "Server=localhost;Database=TennisSms_Tests;Integrated Security=true;TrustServerCertificate=True";

    private IHost? _host;
    public IHost Host => _host ?? throw new InvalidOperationException("Host failed to start.");

    public async Task InitializeAsync()
    {
        await Storage.EnsureDatabaseExistsAsync(ConnectionString);

        _host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton(GameClock.Chicago());
                services.AddTennisSmsStorage(ConnectionString);
            })
            .UseWolverine(opts => opts.ConfigureTennisSmsWolverine())
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync();
        _host.Dispose();
    }
}

public class StorageSmokeTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
{
    [Fact]
    public async Task Scheduling_a_game_through_Wolverine_writes_the_event_and_inline_snapshot()
    {
        var id = Guid.NewGuid();
        var start = DateTime.Today.AddDays(5).AddHours(13);

        await fixture.Host.InvokeMessageAndWaitAsync(
            new ScheduleGame(id, GameFormat.Doubles, start, start.AddHours(1), "the club"));

        await using var session = fixture.Host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var game = await session.LoadAsync<Game>(id);

        Assert.NotNull(game);
        Assert.Equal(4, game.Capacity);
        Assert.Equal(start, game.StartsAtLocal);
        Assert.Equal(GameStatus.Open, game.Status);

        var events = await session.Events.FetchStreamAsync(id);
        Assert.IsType<GameScheduled>(Assert.Single(events).Data);
    }

    [Fact]
    public async Task Player_phone_numbers_are_unique()
    {
        var store = fixture.Host.Services.GetRequiredService<IDocumentStore>();
        var phone = "+1555" + Random.Shared.Next(1000000, 9999999);
        var now = DateTimeOffset.UtcNow;

        await using (var s = store.LightweightSession())
        {
            var id = Guid.NewGuid();
            s.Events.StartStream<Player>(id, PlayerRules.Register(id, "Mike", phone, now));
            await s.SaveChangesAsync();
        }

        await using var dup = store.LightweightSession();
        var dupId = Guid.NewGuid();
        dup.Events.StartStream<Player>(dupId, PlayerRules.Register(dupId, "Other", phone, now));
        await Assert.ThrowsAnyAsync<Exception>(() => dup.SaveChangesAsync());
    }
}
