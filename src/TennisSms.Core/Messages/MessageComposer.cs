using System.Globalization;
using TennisSms.Core.Games;

namespace TennisSms.Core.Messages;

/// <summary>What the composer needs to turn a game notice into words.</summary>
public sealed record GameMessageContext
{
    public required Game Game { get; init; }

    /// <summary>Display names (≤12 chars) for every player in the game.</summary>
    public required IReadOnlyDictionary<Guid, string> Names { get; init; }

    /// <summary>The player's own game page link.</summary>
    public required Func<Guid, string> PlayerLink { get; init; }

    public required string OrganizerLink { get; init; }
    public required string OrganizerName { get; init; }
    public required string Brand { get; init; }

    /// <summary>Local time free cancellation ends (for the not-full alert).</summary>
    public required DateTime CancellationCutoffLocal { get; init; }

    public int CancellationNoticeHours { get; init; } = 24;
}

/// <summary>A rendered text, ready for the outbox.</summary>
public sealed record OutboundText(Recipient To, string Body, NoticeKind Kind);

/// <summary>Message templates (spec §8.3). Every output must fit one GSM-7 segment.</summary>
public static class MessageComposer
{
    public static IReadOnlyList<OutboundText> Render(IEnumerable<Notice> notices, GameMessageContext ctx) =>
        notices.Select(n => new OutboundText(n.To, Render(n, ctx), n.Kind)).ToList();

    public static string Render(Notice notice, GameMessageContext ctx)
    {
        var g = ctx.Game;
        var when = TimeText.Range(g.StartsAtLocal, g.EndsAtLocal);
        var roster = Roster(ctx);
        var link = notice.To.PlayerId is { } id ? ctx.PlayerLink(id) : ctx.OrganizerLink;
        var spots = g.OpenSpots;

        return notice.Kind switch
        {
            NoticeKind.Invite => WithLocation(ctx,
                loc => $"{ctx.Brand}: {g.Format} {when}{loc}. {Spots(spots)} open. Reply YES or NO, or tap {link}"),
            NoticeKind.Accepted =>
                $"You're in for {when}. Playing: {roster}. {Spots(spots)} left. Reply OUT if you can't make it.",
            NoticeKind.AcceptedAndFilled =>
                $"You're in. Game on! {when}: {roster}. Reply OUT if you can't make it.",
            // With long names and a long link the roster can overflow; then the page has it.
            NoticeKind.AddedByOrganizer => FirstThatFits(
                $"{ctx.OrganizerName} added you to {when}. Playing: {roster}. Reply OUT if you can't make it. {link}",
                $"{ctx.OrganizerName} added you to {when}. Reply OUT if you can't make it. {link}"),
            NoticeKind.GameOn => $"Game on! {when}: {roster}.",
            NoticeKind.FilledThanksAnyway => $"{when} filled up. Thanks anyway!",
            NoticeKind.LateYes => $"Sorry, {when} is already full.",
            NoticeKind.NotInvited => $"You don't have an open invite for {when}.",
            NoticeKind.AlreadyIn => $"You're already in for {when}. Reply OUT if you can't make it.",
            NoticeKind.NotInGame => $"You're not in {when}.",
            NoticeKind.Declined => "Got it, thanks.",
            NoticeKind.Dropped => $"You're out of {when}. The organizer has been notified.",
            NoticeKind.Removed => $"You're no longer in {when}.",
            NoticeKind.GameChanged => WithLocation(ctx, loc => $"Change: tennis is now {when}{loc}. {link}"),
            NoticeKind.Cancelled or NoticeKind.AlreadyCancelled => $"{when} has been cancelled.",
            NoticeKind.Reminder => WithLocation(ctx, loc => $"Reminder: tennis tomorrow, {when}{loc}. Playing: {roster}."),
            NoticeKind.AlreadyStarted => $"{when} has already started.",
            NoticeKind.OrganizerPlayerDropped =>
                $"{NameOf(ctx, notice.AboutPlayerId)} dropped from {when}. {Spots(spots)} open." +
                (notice.WithinCancellationWindow ? $" Inside {ctx.CancellationNoticeHours}-hr window." : "") +
                $" {ctx.OrganizerLink}",
            NoticeKind.OrganizerGameFilled => $"{when} is full: {roster}.",
            NoticeKind.OrganizerNotFullAlert =>
                $"{when} still needs {spots}. Free cancellation ends {TimeText.DayAndTime(ctx.CancellationCutoffLocal)}. {ctx.OrganizerLink}",
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.Kind, null),
        };
    }

    /// <summary>Confirmed players' names, in the order they confirmed.</summary>
    public static string Roster(GameMessageContext ctx) => string.Join(", ",
        ctx.Game.WithStatus(ParticipationStatus.Confirmed)
            .OrderBy(p => p.ConfirmedAt)
            .Select(p => NameOf(ctx, p.PlayerId)));

    /// <summary>
    /// Builds the message with " at {Location}"; if the location can't be encoded or makes the
    /// text too long, drops it (the game page always has it).
    /// </summary>
    private static string WithLocation(GameMessageContext ctx, Func<string, string> build)
    {
        var location = Gsm7.Plain(ctx.Game.Location.Trim());
        if (location.Length > 0 && Gsm7.IsEncodable(location))
        {
            var withLocation = build($" at {location}");
            if (Gsm7.FitsOneSegment(withLocation)) return withLocation;
        }
        return build("");
    }

    private static string FirstThatFits(params string[] candidates) =>
        candidates.FirstOrDefault(Gsm7.FitsOneSegment) ?? candidates[^1];

    private static string Spots(int n) => n == 1 ? "1 spot" : $"{n} spots";

    private static string NameOf(GameMessageContext ctx, Guid? playerId) =>
        playerId is { } id && ctx.Names.TryGetValue(id, out var name) ? name : "Someone";
}

/// <summary>Messages not tied to one game: consent keywords, help, clarification.</summary>
public static class GeneralMessages
{
    // Join / HELP / STOP wording is a placeholder until the Twilio compliance package (spec §13).
    public static string JoinConfirmed(string brand) =>
        $"{brand}: You're signed up for game invites. Reply STOP to opt out, HELP for help. Msg&data rates may apply.";

    public static string StopConfirmed(string brand) =>
        $"{brand}: You're unsubscribed and won't get more texts. Reply JOIN to sign up again.";

    public static string Help(string brand) =>
        $"{brand}: Tennis game invites. Reply YES, NO, or OUT to an invite. Reply STOP to opt out.";

    public const string UnknownNumberJoin = "This number is for invited players only. Ask the organizer to add you.";

    public const string Unrecognized = "Sorry, didn't get that. Reply YES, NO, or OUT. Reply HELP for help.";

    public const string NoOpenInvites = "You don't have any open invites right now.";

    public const string NoUpcomingGames = "You're not in any upcoming games.";

    public const string NotSignedUp = "Reply JOIN to sign up for game invites.";

    /// <summary>To the organizer, once, when replies to unknown numbers hit the hourly cap (spec §6.4).</summary>
    public static string UnknownSendersPaused(string brand) =>
        $"{brand}: Lots of texts from unknown numbers. Replies to them are paused for an hour.";

    /// <summary>The organizer's one-time login code (spec §11.1).</summary>
    public static string LoginCode(string brand, string code, int minutes) =>
        $"{brand} login code: {code}. It expires in {minutes} minutes. If you didn't ask for it, ignore this text.";

    /// <summary>Most games a "Which game?" text can list and still fit one segment.</summary>
    public const int MaxChoices = 4;

    /// <summary>"Which game? 1) Fri 11/13 1pm 2) Sun 11/15 9am. Reply 1 or 2, or use the links."</summary>
    public static string WhichGame(IReadOnlyList<DateTime> gameStartsLocal)
    {
        var options = string.Join(" ", gameStartsLocal.Select((s, i) => $"{i + 1}) {TimeText.DayAndTime(s, withDate: true)}"));
        var choices = gameStartsLocal.Count == 2
            ? "1 or 2"
            : string.Join(", ", Enumerable.Range(1, gameStartsLocal.Count - 1)) + $" or {gameStartsLocal.Count}";
        return $"Which game? {options}. Reply {choices}, or use the links.";
    }
}

/// <summary>How times read in texts: "Fri 11/13 1-2pm", "Thu 1pm".</summary>
public static class TimeText
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Range(DateTime start, DateTime end)
    {
        var day = Day(start);
        var startAmPm = AmPm(start);
        var endAmPm = AmPm(end);
        var startText = startAmPm == endAmPm && start.Date == end.Date ? Clock(start) : Clock(start) + startAmPm;
        return $"{day} {startText}-{Clock(end)}{endAmPm}";
    }

    /// <summary>"Thu 1pm" or, with date, "Fri 11/13 1pm".</summary>
    public static string DayAndTime(DateTime t, bool withDate = false) =>
        (withDate ? Day(t) : t.ToString("ddd", Us)) + " " + Clock(t) + AmPm(t);

    private static string Day(DateTime t) => t.ToString("ddd M/d", Us);

    private static string Clock(DateTime t)
    {
        var hour = t.Hour % 12 == 0 ? 12 : t.Hour % 12;
        return t.Minute == 0 ? hour.ToString(Us) : $"{hour}:{t.Minute:00}";
    }

    private static string AmPm(DateTime t) => t.Hour < 12 ? "am" : "pm";
}
