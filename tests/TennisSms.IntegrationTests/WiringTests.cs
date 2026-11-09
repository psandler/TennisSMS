using Microsoft.Extensions.DependencyInjection;
using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Inbound;
using TennisSms.Web.Players;
using Wolverine;
using Wolverine.Tracking;
using static TennisSms.IntegrationTests.AppHostFixture;

namespace TennisSms.IntegrationTests;

/// <summary>
/// End to end through the real wiring: simulator gateway → durable inbound queue → handlers →
/// Polecat → outbox → SendSms → simulator. Texts are asserted as the fake phones received them.
/// </summary>
[Collection(AppCollection.Name)]
public class WiringTests(AppHostFixture app)
{
    private const string Brand = "TennisSMS";

    private DateTime DaysAhead(int days) =>
        app.Services.GetRequiredService<GameClock>().ToLocal(DateTimeOffset.UtcNow).Date.AddDays(days).AddHours(13);

    private async Task<Guid> ScheduleAsync(GameFormat format, int daysAhead = 5)
    {
        var id = Guid.NewGuid();
        var start = DaysAhead(daysAhead);
        await app.Host.InvokeMessageAndWaitAsync(new ScheduleGame(id, format, start, start.AddHours(1), "Oak Park courts"));
        return id;
    }

    private Task AddAndInviteAsync(Guid gameId, Guid[] add, Guid[] invite) =>
        app.Host.InvokeMessageAndWaitAsync(new AddAndInvite(gameId, add, invite));

    private async Task<Game> GameAsync(Guid id) => (await app.LoadAsync<Game>(id))!;

    private List<string> OrganizerTextsSince(int count) =>
        app.Sent.Where(t => t.To == OrganizerPhone).Skip(count).Select(t => t.Body).ToList();

    private int OrganizerTextCount => app.Sent.Count(t => t.To == OrganizerPhone);

    [Fact]
    public async Task Join_help_and_an_unrecognized_reply()
    {
        var (id, phone) = await app.JoinedPlayerAsync("Mike");
        await app.TextAsync(phone, "help");
        await app.TextAsync(phone, "maybe?");

        Assert.Equal(ConsentStatus.OptedIn, (await app.LoadAsync<Player>(id))!.ConsentStatus);
        Assert.Equal([GeneralMessages.JoinConfirmed(Brand), GeneralMessages.Help(Brand), GeneralMessages.Unrecognized], app.TextsTo(phone));

        var log = await app.LogFor(phone);
        Assert.Equal(["OptedIn", "Help", "Unrecognized"], log.Where(m => m.Direction == Web.Sms.MessageDirection.Inbound).Select(m => m.Outcome));
        Assert.Equal(3, log.Count(m => m.Direction == Web.Sms.MessageDirection.Outbound && m.Accepted == true));
    }

    [Fact]
    public async Task A_yes_by_text_fills_the_game_and_everyone_hears_once()
    {
        var (mike, mikePhone) = await app.JoinedPlayerAsync("Mike");
        var (dave, davePhone) = await app.JoinedPlayerAsync("Dave");
        var (sam, samPhone) = await app.JoinedPlayerAsync("Sam");
        var gameId = await ScheduleAsync(GameFormat.Singles);
        var organizerBefore = OrganizerTextCount;

        await AddAndInviteAsync(gameId, [mike], [dave, sam]);

        var game = await GameAsync(gameId);
        var daveToken = game.Find(dave)!.PageToken;
        Assert.StartsWith("Phil added you to", app.TextsTo(mikePhone).Last());
        Assert.StartsWith($"{Brand}: Singles", app.TextsTo(davePhone).Last());
        Assert.EndsWith($"https://tennis.example.com/g/{daveToken}", app.TextsTo(davePhone).Last());

        var link = await app.LoadAsync<PageLink>(daveToken);
        Assert.Equal((gameId, dave), (link!.GameId, link.PlayerId));

        await app.TextAsync(davePhone, "yes!");

        game = await GameAsync(gameId);
        Assert.Equal(GameStatus.Full, game.Status);
        Assert.Equal(ParticipationStatus.Confirmed, game.Find(dave)!.Status);
        Assert.Equal(ParticipationStatus.Closed, game.Find(sam)!.Status);

        Assert.StartsWith("You're in. Game on!", app.TextsTo(davePhone).Last());
        Assert.StartsWith("Game on!", app.TextsTo(mikePhone).Last());
        Assert.EndsWith("filled up. Thanks anyway!", app.TextsTo(samPhone).Last());
        Assert.Single(OrganizerTextsSince(organizerBefore), t => t.EndsWith("is full: Mike, Dave."));

        var inbound = (await app.LogFor(davePhone)).Last(m => m.Direction == Web.Sms.MessageDirection.Inbound);
        Assert.Equal((gameId, "AcceptedAndFilled"), (inbound.GameId!.Value, inbound.Outcome));
    }

    [Fact]
    public async Task Two_yeses_for_the_last_spot_cannot_both_win()
    {
        var (mike, _) = await app.JoinedPlayerAsync("Mike");
        var (dave, _) = await app.JoinedPlayerAsync("Dave");
        var (sam, _) = await app.JoinedPlayerAsync("Sam");
        var gameId = await ScheduleAsync(GameFormat.Singles);
        await AddAndInviteAsync(gameId, [mike], [dave, sam]);

        async Task<ReplyOutcome> Yes(Guid player)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
            var result = await bus.InvokeAsync<GameReplyResult>(new ReplyToGame(gameId, player, GameReply.Yes, Channel.Page));
            return result.Outcome;
        }

        var outcomes = await Task.WhenAll(Yes(dave), Yes(sam));

        Assert.Equal([ReplyOutcome.AcceptedAndFilled, ReplyOutcome.GameFull], outcomes.Order());
        var game = await GameAsync(gameId);
        Assert.Equal(2, game.ConfirmedCount);
        Assert.Equal(GameStatus.Full, game.Status);
    }

    [Fact]
    public async Task Yes_with_two_open_invites_asks_which_game_and_a_number_answers_it()
    {
        var (lee, leePhone) = await app.JoinedPlayerAsync("Lee");
        var first = await ScheduleAsync(GameFormat.Doubles, daysAhead: 5);
        var second = await ScheduleAsync(GameFormat.Doubles, daysAhead: 6);
        await AddAndInviteAsync(first, [], [lee]);
        await AddAndInviteAsync(second, [], [lee]);

        await app.TextAsync(leePhone, "Y");

        Assert.StartsWith("Which game? 1) ", app.TextsTo(leePhone).Last());
        var pending = await app.LoadAsync<PendingClarification>(lee);
        Assert.Equal([first, second], pending!.GameIds);

        await app.TextAsync(leePhone, "2");

        Assert.Equal(ParticipationStatus.Invited, (await GameAsync(first)).Find(lee)!.Status);
        Assert.Equal(ParticipationStatus.Confirmed, (await GameAsync(second)).Find(lee)!.Status);
        Assert.StartsWith("You're in for", app.TextsTo(leePhone).Last());
        Assert.Null(await app.LoadAsync<PendingClarification>(lee));

        // The question was answered, so another number means nothing.
        await app.TextAsync(leePhone, "2");
        Assert.Equal(GeneralMessages.Unrecognized, app.TextsTo(leePhone).Last());
    }

    [Fact]
    public async Task Stop_leaves_every_game_in_one_go_and_silences_the_player()
    {
        var (kim, kimPhone) = await app.JoinedPlayerAsync("Kimberly");
        var playing = await ScheduleAsync(GameFormat.Doubles, daysAhead: 5);
        var invited = await ScheduleAsync(GameFormat.Doubles, daysAhead: 6);
        await AddAndInviteAsync(playing, [kim], []);
        await AddAndInviteAsync(invited, [], [kim]);
        var textsBefore = app.TextsTo(kimPhone).Count;
        var organizerBefore = OrganizerTextCount;

        await app.TextAsync(kimPhone, "STOP");

        Assert.Equal(ConsentStatus.OptedOut, (await app.LoadAsync<Player>(kim))!.ConsentStatus);
        Assert.Equal(ParticipationStatus.Dropped, (await GameAsync(playing)).Find(kim)!.Status);
        var closed = (await GameAsync(invited)).Find(kim)!;
        Assert.Equal((ParticipationStatus.Closed, CloseReason.OptedOut), (closed.Status, closed.CloseReason!.Value));

        Assert.Equal([GeneralMessages.StopConfirmed(Brand)], app.TextsTo(kimPhone).Skip(textsBefore));
        Assert.Single(OrganizerTextsSince(organizerBefore), t => t.StartsWith("Kimberly dropped from"));

        // After STOP, a YES gets no reply at all.
        await app.TextAsync(kimPhone, "yes");
        Assert.Equal(textsBefore + 1, app.TextsTo(kimPhone).Count);
        Assert.Equal("IgnoredOptedOut", (await app.LogFor(kimPhone)).Last().Outcome);

        // JOIN brings them back.
        await app.TextAsync(kimPhone, "start");
        Assert.Equal(ConsentStatus.OptedIn, (await app.LoadAsync<Player>(kim))!.ConsentStatus);
    }

    [Fact]
    public async Task Unknown_numbers_get_at_most_one_reply_per_keyword()
    {
        var stranger = NewPhone();
        foreach (var body in new[] { "JOIN", "join", "hello?", "HELP", "HELP", "yes" })
            await app.TextAsync(stranger, body);

        Assert.Equal([GeneralMessages.UnknownNumberJoin, GeneralMessages.UnknownNumberHelp(Brand)], app.TextsTo(stranger));
        var sender = await app.LoadAsync<UnknownSender>(stranger);
        Assert.Equal(6, sender!.MessageCount);
        Assert.True(sender.JoinReplied);
        Assert.All((await app.LogFor(stranger)).Where(m => m.Direction == Web.Sms.MessageDirection.Inbound),
            m => Assert.StartsWith("UnknownSender", m.Outcome));
    }

    [Fact]
    public async Task Over_the_hourly_cap_unknown_numbers_get_nothing_and_the_organizer_hears_once()
    {
        async Task SetBudget(int count)
        {
            await using var session = app.Store.LightweightSession();
            session.Store(new UnknownReplyBudget { WindowStart = DateTimeOffset.UtcNow, Count = count });
            await session.SaveChangesAsync();
        }

        await SetBudget(1000);
        try
        {
            var organizerBefore = OrganizerTextCount;
            var (one, two) = (NewPhone(), NewPhone());
            await app.TextAsync(one, "JOIN");
            await app.TextAsync(two, "JOIN");

            Assert.Empty(app.TextsTo(one));
            Assert.Empty(app.TextsTo(two));
            Assert.Equal([GeneralMessages.UnknownSendersPaused(Brand)], OrganizerTextsSince(organizerBefore));
            Assert.Equal("UnknownSenderCapped", (await app.LogFor(two)).Single().Outcome);
        }
        finally
        {
            await SetBudget(0);
        }
    }

    [Fact]
    public async Task Scheduling_sets_three_timers_and_each_one_does_its_job_once()
    {
        var (mike, mikePhone) = await app.JoinedPlayerAsync("Mike");
        var (dave, davePhone) = await app.JoinedPlayerAsync("Dave");
        var id = Guid.NewGuid();
        var start = DaysAhead(5);

        var tracked = await app.Host.InvokeMessageAndWaitAsync(new ScheduleGame(id, GameFormat.Doubles, start, start.AddHours(1), "Oak Park"));
        var timers = tracked.Scheduled.MessagesOf<IGameTimer>().ToList();
        Assert.Equal(3, timers.Count);
        Assert.All(timers, t => Assert.Equal((id, start), (t.GameId, t.StartsAtLocal)));

        await AddAndInviteAsync(id, [mike, dave], []);
        var organizerBefore = OrganizerTextCount;

        // A stale timer (computed for another start time) does nothing.
        await app.Host.InvokeMessageAndWaitAsync(new ReminderDue(id, start.AddDays(-1)));
        Assert.False((await GameAsync(id)).ReminderSent);

        await app.Host.InvokeMessageAndWaitAsync(new ReminderDue(id, start));
        await app.Host.InvokeMessageAndWaitAsync(new ReminderDue(id, start)); // fires twice, texts once
        Assert.Single(app.TextsTo(mikePhone), t => t.StartsWith("Reminder: tennis tomorrow"));
        Assert.Single(app.TextsTo(davePhone), t => t.StartsWith("Reminder: tennis tomorrow"));

        await app.Host.InvokeMessageAndWaitAsync(new NotFullAlertDue(id, start));
        Assert.Single(OrganizerTextsSince(organizerBefore), t => t.Contains("still needs 2. Free cancellation ends"));

        await app.Host.InvokeMessageAndWaitAsync(new GameStartDue(id, start));
        Assert.Equal(GameStatus.Started, (await GameAsync(id)).Status);

        await app.TextAsync(mikePhone, "OUT");
        Assert.EndsWith("has already started.", app.TextsTo(mikePhone).Last());
    }

    [Fact]
    public async Task Changing_the_time_texts_players_and_reschedules_the_timers()
    {
        var (mike, mikePhone) = await app.JoinedPlayerAsync("Mike");
        var (dave, davePhone) = await app.JoinedPlayerAsync("Dave");
        var id = await ScheduleAsync(GameFormat.Doubles);
        await AddAndInviteAsync(id, [mike], [dave]);
        var newStart = DaysAhead(6).AddHours(1);

        var tracked = await app.Host.InvokeMessageAndWaitAsync(new ChangeGameDetails(id, newStart, newStart.AddHours(1), "North courts"));

        Assert.Equal(3, tracked.Scheduled.MessagesOf<IGameTimer>().Count(t => t.StartsAtLocal == newStart));
        Assert.StartsWith("Change: tennis is now", app.TextsTo(mikePhone).Last());
        Assert.StartsWith("Change: tennis is now", app.TextsTo(davePhone).Last());
        Assert.Contains("at North courts", app.TextsTo(davePhone).Last());
    }

    [Fact]
    public async Task A_number_can_only_belong_to_one_player()
    {
        var (_, phone) = await app.JoinedPlayerAsync("Mike");
        await using var scope = app.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var ex = await Assert.ThrowsAsync<PlayerRuleException>(() => bus.InvokeAsync(new RegisterPlayer(Guid.NewGuid(), "Other", phone)));
        Assert.Equal("Mike already has that number.", ex.Message);
    }
}
