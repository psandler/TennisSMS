namespace TennisSms.Core.Games;

/// <summary>
/// Current state of one game, built from its event stream. Polecat stores this as an
/// inline snapshot document. It holds state only; decisions live in <see cref="GameDecider"/>.
/// </summary>
public sealed partial class Game
{
    public Guid Id { get; set; }
    public GameFormat Format { get; set; }
    public int Capacity { get; set; }
    public DateTime StartsAtLocal { get; set; }
    public DateTime EndsAtLocal { get; set; }
    public string Location { get; set; } = "";
    public GameStatus Status { get; set; }
    public List<Participant> Participants { get; set; } = [];
    public DateTimeOffset? FilledAt { get; set; }
    public bool ReminderSent { get; set; }
    public bool NotFullAlertSent { get; set; }

    public int ConfirmedCount => Participants.Count(p => p.Status == ParticipationStatus.Confirmed);
    public int OpenSpots => Capacity - ConfirmedCount;
    public bool IsClosedForChanges => Status is GameStatus.Cancelled or GameStatus.Started;

    public Participant? Find(Guid playerId) => Participants.FirstOrDefault(p => p.PlayerId == playerId);

    public IEnumerable<Participant> WithStatus(ParticipationStatus status) =>
        Participants.Where(p => p.Status == status);

    /// <summary>Deep copy, so a decision can apply its own events to a working state.</summary>
    public Game Clone()
    {
        var copy = (Game)MemberwiseClone();
        copy.Participants = Participants.Select(p => p.Clone()).ToList();
        return copy;
    }

    public static Game Create(GameScheduled e) => new()
    {
        Id = e.GameId,
        Format = e.Format,
        Capacity = e.Capacity,
        StartsAtLocal = e.StartsAtLocal,
        EndsAtLocal = e.EndsAtLocal,
        Location = e.Location,
        Status = GameStatus.Open,
    };

    public void Apply(GameDetailsChanged e)
    {
        StartsAtLocal = e.StartsAtLocal;
        EndsAtLocal = e.EndsAtLocal;
        Location = e.Location;
    }

    public void Apply(PlayerAddedByOrganizer e)
    {
        var p = GetOrAdd(e.PlayerId, e.PageToken);
        p.Status = ParticipationStatus.Confirmed;
        p.CloseReason = null;
        p.ConfirmedAt = e.At;
    }

    public void Apply(PlayerInvited e)
    {
        var p = GetOrAdd(e.PlayerId, e.PageToken);
        p.Status = ParticipationStatus.Invited;
        p.CloseReason = null;
    }

    public void Apply(InvitationAccepted e)
    {
        var p = Find(e.PlayerId)!;
        p.Status = ParticipationStatus.Confirmed;
        p.ConfirmedAt = e.At;
    }

    public void Apply(InvitationDeclined e) => Find(e.PlayerId)!.Status = ParticipationStatus.Declined;

    public void Apply(GameFilled e)
    {
        Status = GameStatus.Full;
        FilledAt = e.At;
    }

    public void Apply(InvitationClosed e)
    {
        var p = Find(e.PlayerId)!;
        p.Status = ParticipationStatus.Closed;
        p.CloseReason = e.Reason;
    }

    public void Apply(PlayerDropped e) => Find(e.PlayerId)!.Status = ParticipationStatus.Dropped;

    public void Apply(PlayerRemovedByOrganizer e) => Find(e.PlayerId)!.Status = ParticipationStatus.Removed;

    public void Apply(SpotReopened e)
    {
        Status = GameStatus.Open;
        FilledAt = null;
    }

    public void Apply(GameCancelled e) => Status = GameStatus.Cancelled;

    public void Apply(GameStarted e) => Status = GameStatus.Started;

    public void Apply(ReminderIssued e) => ReminderSent = true;

    public void Apply(CancellationWindowAlertIssued e) => NotFullAlertSent = true;

    private Participant GetOrAdd(Guid playerId, string pageToken)
    {
        var p = Find(playerId);
        if (p is null)
        {
            p = new Participant { PlayerId = playerId, PageToken = pageToken };
            Participants.Add(p);
        }
        return p;
    }
}

public sealed class Participant
{
    public Guid PlayerId { get; set; }
    public ParticipationStatus Status { get; set; }
    public string PageToken { get; set; } = "";
    public CloseReason? CloseReason { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }

    public Participant Clone() => (Participant)MemberwiseClone();
}
