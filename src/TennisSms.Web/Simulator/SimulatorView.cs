using System.Text.RegularExpressions;
using JasperFx.Events;
using Polecat;
using Polecat.Linq;
using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Core.Time;
using TennisSms.Web.Games;
using TennisSms.Web.Sms;

namespace TennisSms.Web.Simulator;

public sealed record PhoneView(SimPhone Phone, ConsentStatus? Consent, IReadOnlyList<MessageLog> Thread, IReadOnlyList<PendingStep> Typing, AutoReplyMode Mode);

public sealed record TimelineEntry(long Sequence, DateTimeOffset At, string Stream, string Type, string Details);

public sealed record TimerRow(string Game, string Timer, DateTime DueLocal, string Status, IGameTimer? FireNow);

public sealed record SimulatorSnapshot(
    IReadOnlyList<PhoneView> Phones, IReadOnlyList<TimelineEntry> Timeline, IReadOnlyList<TimerRow> Timers, int PendingCount);

/// <summary>Loads everything the simulator page shows, in one pass.</summary>
public sealed partial class SimulatorView(IDocumentStore store, SimulatorEngine engine, GameClock clock, TimeProvider time)
{
    public const int TimelineSize = 80;

    public async Task<SimulatorSnapshot> LoadAsync(CancellationToken ct = default)
    {
        await using var session = store.QuerySession();
        var players = (await session.Query<Player>().ToListAsync(ct)).ToList();
        var games = (await session.Query<Game>().ToListAsync(ct)).ToList();
        var logs = (await session.Query<MessageLog>().ToListAsync(ct)).OrderBy(m => m.At).ToList();
        var events = (await session.Events.QueryAllRawEvents()
            .OrderByDescending(e => e.Sequence).Take(TimelineSize).ToListAsync(ct)).ToList();

        var pending = engine.Pending;
        var phones = engine.Phones.Select(phone => new PhoneView(
            phone,
            players.FirstOrDefault(p => p.PhoneE164 == phone.Number)?.ConsentStatus,
            logs.Where(m => m.Phone == phone.Number).ToList(),
            pending.Where(p => p.Phone == phone.Number).ToList(),
            engine.ModeOf(phone.Number))).ToList();

        var names = players.ToDictionary(p => p.Id, p => p.Name);
        var gameNames = games.ToDictionary(g => g.Id, GameName);
        var timeline = events.Select(e => new TimelineEntry(
            e.Sequence, e.Timestamp,
            gameNames.TryGetValue(e.StreamId, out var g) ? $"Game {g}" : names.TryGetValue(e.StreamId, out var n) ? $"Player {n}" : e.StreamId.ToString()[..8],
            e.Data.GetType().Name,
            Describe(e.Data, names))).ToList();

        var nowLocal = clock.ToLocal(time.GetUtcNow());
        var timers = games.Where(x => x.EndsAtLocal > nowLocal).OrderBy(x => x.StartsAtLocal).SelectMany(TimersFor).ToList();

        return new SimulatorSnapshot(phones, timeline, timers, pending.Count);
    }

    public static string GameName(Game g) => $"{TimeText.DayAndTime(g.StartsAtLocal, withDate: true)} {g.Format}";

    /// <summary>The three timers for a game and where each one stands (spec §11.3).</summary>
    public IEnumerable<TimerRow> TimersFor(Game g)
    {
        var times = clock.For(g.StartsAtLocal, g.EndsAtLocal);
        var now = time.GetUtcNow();
        var name = GameName(g);
        var closed = g.Status switch { GameStatus.Cancelled => "cancelled", GameStatus.Started => "skipped (started)", _ => null };

        string Status(bool fired, DateTimeOffset due, string? notNeeded = null) =>
            fired ? "fired"
            : closed ?? (due <= now ? notNeeded ?? "past, not sent" : notNeeded is null ? "scheduled" : $"scheduled ({notNeeded})");

        yield return Row("Not-full alert", times.NotFullAlertAt,
            Status(g.NotFullAlertSent, times.NotFullAlertAt, g.Status == GameStatus.Full ? "not needed: full" : null),
            new NotFullAlertDue(g.Id, g.StartsAtLocal));
        yield return Row("Reminder", times.ReminderAt, Status(g.ReminderSent, times.ReminderAt), new ReminderDue(g.Id, g.StartsAtLocal));
        yield return Row("Game start", times.StartsAt,
            g.Status == GameStatus.Started ? "fired" : g.Status == GameStatus.Cancelled ? "cancelled" : "scheduled",
            new GameStartDue(g.Id, g.StartsAtLocal));

        TimerRow Row(string timer, DateTimeOffset due, string status, IGameTimer fire) =>
            new(name, timer, clock.ToLocal(due), status, status is "fired" or "cancelled" ? null : fire);
    }

    /// <summary>"PlayerId=Mike Channel=Sms" style summary of an event's data.</summary>
    public string Describe(object data, IReadOnlyDictionary<Guid, string> names)
    {
        var parts = new List<string>();
        foreach (var prop in data.GetType().GetProperties())
        {
            // The timeline already shows when each event happened.
            if (prop.Name is "GameId" or "PageToken" or "InboundMessageId" or "At") continue;
            var value = prop.GetValue(data);
            string? text = value switch
            {
                null => null,
                Guid id when prop.Name == "PlayerId" => names.GetValueOrDefault(id, id.ToString()[..8]),
                DateTimeOffset at => clock.ToLocal(at).ToString("ddd h:mm:ss tt"),
                DateTime local => local.ToString("ddd M/d h:mm tt"),
                bool b => b ? prop.Name : null,
                _ => value.ToString(),
            };
            if (text is null) continue;
            parts.Add(prop.Name is "PlayerId" || value is bool ? text : $"{prop.Name}={text}");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>Splits a text body into plain runs and links, so links can be clickable.</summary>
    public static IEnumerable<(string Text, bool IsLink)> Linkify(string body)
    {
        var last = 0;
        foreach (Match m in Url().Matches(body))
        {
            if (m.Index > last) yield return (body[last..m.Index], false);
            yield return (m.Value, true);
            last = m.Index + m.Length;
        }
        if (last < body.Length) yield return (body[last..], false);
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();
}
