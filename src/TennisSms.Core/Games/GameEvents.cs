namespace TennisSms.Core.Games;

public enum GameFormat { Singles, Doubles }

public enum GameStatus { Open, Full, Cancelled, Started }

public enum ParticipationStatus { Invited, Confirmed, Declined, Closed, Dropped, Removed }

/// <summary>How a player's action reached us.</summary>
public enum Channel { Sms, Page }

public enum CloseReason { GameFilled, GameStarted, GameCancelled, OptedOut }

public enum DropReason { PlayerRequest, OptedOut }

public static class GameFormats
{
    public static int CapacityOf(GameFormat format) => format switch
    {
        GameFormat.Singles => 2,
        GameFormat.Doubles => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}

// Events on a game stream (spec §4.3). Times that later rules depend on
// (who was confirmed when) are carried in the event so the domain stays independent of
// the event store's metadata.

public sealed record GameScheduled(
    Guid GameId,
    GameFormat Format,
    int Capacity,
    DateTime StartsAtLocal,
    DateTime EndsAtLocal,
    string Location);

public sealed record GameDetailsChanged(DateTime StartsAtLocal, DateTime EndsAtLocal, string Location);

public sealed record PlayerAddedByOrganizer(Guid PlayerId, string PageToken, DateTimeOffset At);

public sealed record PlayerInvited(Guid PlayerId, string PageToken);

public sealed record InvitationAccepted(Guid PlayerId, Channel Channel, Guid? InboundMessageId, DateTimeOffset At);

public sealed record InvitationDeclined(Guid PlayerId, Channel Channel, Guid? InboundMessageId);

public sealed record GameFilled(DateTimeOffset At);

public sealed record InvitationClosed(Guid PlayerId, CloseReason Reason);

public sealed record PlayerDropped(Guid PlayerId, Channel Channel, bool WithinCancellationWindow, DropReason Reason);

public sealed record PlayerRemovedByOrganizer(Guid PlayerId);

public sealed record SpotReopened;

public sealed record GameCancelled;

public sealed record GameStarted;

public sealed record ReminderIssued;

public sealed record CancellationWindowAlertIssued;

/// <summary>Idempotency marker: the organizer was told the game can't fill with the people asked (E-6).</summary>
public sealed record ShortfallAlertIssued(int Shortfall);
