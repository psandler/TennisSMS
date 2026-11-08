namespace TennisSms.Core.Games;

/// <summary>Who a text goes to: a player, or the organizer in their organizer role.</summary>
public readonly record struct Recipient(Guid? PlayerId)
{
    public static Recipient Organizer => new(null);
    public static Recipient Player(Guid id) => new(id);
    public bool IsOrganizer => PlayerId is null;
    public override string ToString() => IsOrganizer ? "Organizer" : $"Player {PlayerId}";
}

/// <summary>What kind of text to send. Rendered to words by <c>MessageComposer</c>.</summary>
public enum NoticeKind
{
    Invite,
    Accepted,
    AcceptedAndFilled,
    AddedByOrganizer,
    GameOn,
    FilledThanksAnyway,
    LateYes,
    NotInvited,
    AlreadyIn,
    NotInGame,
    Declined,
    Dropped,
    Removed,
    GameChanged,
    Cancelled,
    AlreadyCancelled,
    Reminder,
    AlreadyStarted,
    OrganizerPlayerDropped,
    OrganizerGameFilled,
    OrganizerNotFullAlert,
}

public sealed record Notice(NoticeKind Kind, Recipient To, Guid? AboutPlayerId = null, bool WithinCancellationWindow = false);

/// <summary>How a player's reply turned out. Recorded on the inbound MessageLog entry.</summary>
public enum ReplyOutcome
{
    None,
    Accepted,
    AcceptedAndFilled,
    Declined,
    Dropped,
    GameFull,
    NotInvited,
    AlreadyIn,
    NotInGame,
    GameCancelled,
    GameStarted,
}

/// <summary>The result of a decision: events to append and texts to send.</summary>
public sealed record Decision(IReadOnlyList<object> Events, IReadOnlyList<Notice> Notices, ReplyOutcome Outcome = ReplyOutcome.None)
{
    public static Decision Nothing { get; } = new([], []);
    public bool IsEmpty => Events.Count == 0 && Notices.Count == 0;
}

/// <summary>An organizer action that breaks a rule. The UI shows the message.</summary>
public sealed class GameRuleException(string message) : Exception(message);
