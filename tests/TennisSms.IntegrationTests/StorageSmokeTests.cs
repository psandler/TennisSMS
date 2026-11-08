using Microsoft.Extensions.DependencyInjection;
using Polecat;
using TennisSms.Core.Games;
using TennisSms.Core.Players;
using TennisSms.Web.Games;
using Wolverine.Tracking;

namespace TennisSms.IntegrationTests;

[Collection(AppCollection.Name)]
public class StorageSmokeTests(AppHostFixture fixture)
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
    public async Task Exclusive_fetch_makes_a_second_writer_wait_for_the_first_to_commit()
    {
        var id = Guid.NewGuid();
        var start = DateTime.Today.AddDays(5).AddHours(13);
        await fixture.Host.InvokeMessageAndWaitAsync(new ScheduleGame(id, GameFormat.Doubles, start, start.AddHours(1), "the club"));
        var store = fixture.Services.GetRequiredService<IDocumentStore>();

        await using var first = store.LightweightSession();
        var stream = await first.Events.FetchForExclusiveWriting<Game>(id);

        var second = Task.Run(async () =>
        {
            await using var s = store.LightweightSession();
            var other = await s.Events.FetchForExclusiveWriting<Game>(id);
            return other.Aggregate!.ReminderSent;
        });

        await Task.Delay(750);
        Assert.False(second.IsCompleted, "The second writer should be waiting on the lock.");

        stream.AppendOne(new ReminderIssued());
        await first.SaveChangesAsync();

        Assert.True(await second, "The second writer should see the first writer's event.");
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
