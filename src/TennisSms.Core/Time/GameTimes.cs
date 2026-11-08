namespace TennisSms.Core.Time;

/// <summary>Court and reminder policy (spec §9). Configuration, not code.</summary>
public sealed record SchedulePolicy
{
    /// <summary>Free cancellation ends this long before the start, in local clock time.</summary>
    public TimeSpan CancellationNotice { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The not-full alert goes this long before the cancellation cutoff.</summary>
    public TimeSpan NotFullAlertLead { get; init; } = TimeSpan.FromHours(4);

    /// <summary>Local time of day, the evening before the game, for the reminder.</summary>
    public TimeOnly ReminderTimeOfDay { get; init; } = new(19, 0);

    public static SchedulePolicy Default { get; } = new();
}

/// <summary>The UTC instants that matter for one game.</summary>
public sealed record GameTimes(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset CancellationCutoff,
    DateTimeOffset NotFullAlertAt,
    DateTimeOffset ReminderAt);

/// <summary>
/// The one place local game times become UTC instants (spec §10). Relative times are
/// computed on the local clock first, so "24 hours before" and "7pm the evening before"
/// mean the same wall-clock time across a DST change.
/// </summary>
public sealed class GameClock(TimeZoneInfo zone, SchedulePolicy policy)
{
    public TimeZoneInfo Zone { get; } = zone;
    public SchedulePolicy Policy { get; } = policy;

    public static GameClock Chicago(SchedulePolicy? policy = null) =>
        new(TimeZoneInfo.FindSystemTimeZoneById("America/Chicago"), policy ?? SchedulePolicy.Default);

    public GameTimes For(DateTime startsAtLocal, DateTime endsAtLocal)
    {
        var cutoffLocal = startsAtLocal - Policy.CancellationNotice;
        var reminderLocal = DateOnly.FromDateTime(startsAtLocal).AddDays(-1).ToDateTime(Policy.ReminderTimeOfDay);

        return new GameTimes(
            StartsAt: ToUtc(startsAtLocal),
            EndsAt: ToUtc(endsAtLocal),
            CancellationCutoff: ToUtc(cutoffLocal),
            NotFullAlertAt: ToUtc(cutoffLocal - Policy.NotFullAlertLead),
            ReminderAt: ToUtc(reminderLocal));
    }

    public DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // Spring forward: the local time doesn't exist. Use the same time an hour later.
        if (Zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        // Fall back: the local time happens twice. Use the first (daylight) occurrence.
        var offset = Zone.IsAmbiguousTime(unspecified)
            ? Zone.GetAmbiguousTimeOffsets(unspecified).Max()
            : Zone.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    public DateTime ToLocal(DateTimeOffset instant) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, Zone).DateTime, DateTimeKind.Unspecified);
}
