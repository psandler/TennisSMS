using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Inbound;
using TennisSms.Web.Sms;

namespace TennisSms.Web.Infrastructure;

public static class AppServices
{
    /// <summary>Everything the app needs except the web pipeline. The integration tests use this too.</summary>
    public static IServiceCollection AddTennisSms(this IServiceCollection services, IConfiguration configuration, string connectionString)
    {
        services.Configure<TennisSmsOptions>(configuration.GetSection(TennisSmsOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(GameClock.Chicago());
        services.AddSingleton<GameWork>();
        services.AddSingleton<InboundProcessor>();

        var gateway = configuration.GetSection(TennisSmsOptions.Section)[nameof(TennisSmsOptions.Gateway)] ?? "Simulator";
        switch (gateway)
        {
            case "Simulator":
                services.AddSingleton<SimulatorGateway>();
                services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<SimulatorGateway>());
                break;
            default:
                throw new InvalidOperationException($"Unknown SMS gateway '{gateway}'. v0.1 only has 'Simulator'.");
        }

        services.AddTennisSmsStorage(connectionString);
        return services;
    }
}
