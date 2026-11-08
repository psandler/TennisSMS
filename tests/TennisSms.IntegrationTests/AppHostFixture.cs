using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Players;
using TennisSms.Web.Infrastructure;
using TennisSms.Web.Players;
using TennisSms.Web.Sms;
using Wolverine;
using Wolverine.Tracking;

namespace TennisSms.IntegrationTests;

/// <summary>
/// Boots the app's services (Polecat, Wolverine, simulator gateway) against a real SQL Server
/// 2025 database. Override the connection with TENNISSMS_TEST_DB. Tests share one database and
/// stay independent by giving every player a fresh random number.
/// </summary>
public sealed class AppHostFixture : IAsyncLifetime
{
    public const string OrganizerPhone = "+15550100001";

    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TENNISSMS_TEST_DB")
        ?? "Server=localhost;Database=TennisSms_Tests;Integrated Security=true;TrustServerCertificate=True";

    private IHost? _host;
    public IHost Host => _host ?? throw new InvalidOperationException("Host failed to start.");

    /// <summary>Every text the simulator was handed, in order.</summary>
    public ConcurrentQueue<SimulatedText> Sent { get; } = new();

    public async Task InitializeAsync()
    {
        await Storage.EnsureDatabaseExistsAsync(ConnectionString);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TennisSms:BaseUrl"] = "https://tennis.example.com",
                ["TennisSms:Organizer:Name"] = "Phil",
                ["TennisSms:Organizer:PhoneE164"] = OrganizerPhone,
                ["TennisSms:UnknownReplyCapPerHour"] = "1000",
            })
            .Build();

        _host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices(services => services.AddTennisSms(config, ConnectionString))
            .UseWolverine(opts => opts.ConfigureTennisSmsWolverine())
            .StartAsync();

        Services.GetRequiredService<SimulatorGateway>().TextSent += Sent.Enqueue;
    }

    public IServiceProvider Services => Host.Services;
    public IDocumentStore Store => Services.GetRequiredService<IDocumentStore>();
    public SimulatorGateway Gateway => Services.GetRequiredService<SimulatorGateway>();

    public static string NewPhone() => "+1555" + Random.Shared.Next(2000000, 9999999);

    /// <summary>A player whose number is seeded, who has texted JOIN.</summary>
    public async Task<(Guid Id, string Phone)> JoinedPlayerAsync(string name)
    {
        var id = Guid.NewGuid();
        var phone = NewPhone();
        await Host.InvokeMessageAndWaitAsync(new RegisterPlayer(id, name, phone));
        await TextAsync(phone, "JOIN");
        return (id, phone);
    }

    /// <summary>A fake phone texts the service, through the gateway and the durable inbound queue.</summary>
    public Task<ITrackedSession> TextAsync(string fromPhone, string body) =>
        Host.ExecuteAndWaitAsync(_ => Gateway.ReceiveAsync(fromPhone, body));

    public List<string> TextsTo(string phone) => Sent.Where(t => t.To == phone).Select(t => t.Body).ToList();

    public async Task<T?> LoadAsync<T>(object id) where T : class
    {
        await using var session = Store.QuerySession();
        return id switch
        {
            Guid g => await session.LoadAsync<T>(g),
            string s => await session.LoadAsync<T>(s),
            _ => throw new ArgumentException("Unsupported id type", nameof(id)),
        };
    }

    public async Task<List<MessageLog>> LogFor(string phone)
    {
        await using var session = Store.QuerySession();
        var logs = await session.Query<MessageLog>().Where(m => m.Phone == phone).ToListAsync();
        return logs.OrderBy(m => m.At).ToList();
    }

    public async Task DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync();
        _host.Dispose();
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppHostFixture>
{
    // One host and one database for every integration test, run one test at a time.
    public const string Name = "app";
}
