using TennisSms.Core.Time;

namespace TennisSms.Core.Games;

/// <summary>Everything a game decision needs besides the game itself.</summary>
/// <param name="Now">Current instant.</param>
/// <param name="Times">The game's UTC instants (cutoff, reminder...), from <see cref="GameClock"/>.</param>
/// <param name="OrganizerPlayerId">The organizer's own player id, if they have one.</param>
public sealed record GameContext(DateTimeOffset Now, GameTimes Times, Guid? OrganizerPlayerId = null);

/// <summary>
/// Game rules (spec §4.3–§5). Each method takes the current state and returns the events to
/// append and the texts to send. Pure: no I/O, no clock, no randomness except injected.
/// </summary>
public static class GameDecider
{
    // ---------- Organizer actions ----------

    public static Decision Schedule(
        Guid gameId, GameFormat format, DateTime startsAtLocal, DateTime endsAtLocal, string location,
        DateTimeOffset now, GameTimes times)
    {
        ValidateDetails(startsAtLocal, endsAtLocal, location, now, times);
        return new Decision(
            [new GameScheduled(gameId, format, GameFormats.CapacityOf(format), startsAtLocal, endsAtLocal, location.Trim())],
            []);
    }

    /// <summary>
    /// Organizer saves roster changes: players added straight in as confirmed, and players invited.
    /// One command, so a game that ends up full sends only "game on" to the added players.
    /// </summary>
    public static Decision AddAndInvite(
        Game game, GameContext ctx,
        IReadOnlyCollection<Guid> add, IReadOnlyCollection<Guid> invite,
        IReadOnlySet<Guid> optedIn, Func<string> newPageToken)
    {
        RequireOpenForChanges(game);
        if (add.Intersect(invite).Any())
            throw new GameRuleException("A player can't be both added and invited.");

        foreach (var id in add.Concat(invite))
            if (!optedIn.Contains(id))
                throw new GameRuleException("Only players who have opted in can be added or invited.");

        var toAdd = add.Distinct().Where(id => game.Find(id)?.Status != ParticipationStatus.Confirmed).ToList();
        if (game.ConfirmedCount + toAdd.Count > game.Capacity)
            throw new GameRuleException($"That would put {game.ConfirmedCount + toAdd.Count} players in a game for {game.Capacity}.");

        foreach (var id in invite.Distinct())
        {
            var status = game.Find(id)?.Status;
            if (status == ParticipationStatus.Declined)
                throw new GameRuleException("A player who declined can't be re-invited to the same game.");
            if (status is ParticipationStatus.Confirmed or ParticipationStatus.Invited)
                throw new GameRuleException("That player is already in or already invited.");
        }

        var draft = new Draft(game);
        var previouslyConfirmed = draft.State.WithStatus(ParticipationStatus.Confirmed).Select(p => p.PlayerId).ToList();

        foreach (var id in toAdd)
            draft.Emit(new PlayerAddedByOrganizer(id, game.Find(id)?.PageToken ?? newPageToken(), ctx.Now));

        var closed = FillIfComplete(draft, ctx);
        var filled = draft.State.Status == GameStatus.Full && game.Status != GameStatus.Full;

        if (draft.State.Status == GameStatus.Full && invite.Count > 0)
            throw new GameRuleException("The game is full, so no one else can be invited.");

        foreach (var id in invite.Distinct())
            draft.Emit(new PlayerInvited(id, game.Find(id)?.PageToken ?? newPageToken()));

        if (filled)
        {
            foreach (var id in toAdd.Concat(previouslyConfirmed))
                draft.Notify(NoticeKind.GameOn, Recipient.Player(id));
            foreach (var id in closed)
                draft.Notify(NoticeKind.FilledThanksAnyway, Recipient.Player(id));
        }
        else
        {
            foreach (var id in toAdd)
                draft.Notify(NoticeKind.AddedByOrganizer, Recipient.Player(id));
        }

        foreach (var id in invite.Distinct())
            draft.Notify(NoticeKind.Invite, Recipient.Player(id));

        return draft.Done(ctx, organizerAction: true);
    }

    /// <summary>One-tap re-invite of everyone whose invitation was closed. Never players who declined.</summary>
    public static Decision ReinviteClosed(Game game, GameContext ctx, IReadOnlySet<Guid> optedIn)
    {
        RequireOpenForChanges(game);
        if (game.Status != GameStatus.Open)
            throw new GameRuleException("The game is full.");

        var draft = new Draft(game);
        foreach (var p in game.WithStatus(ParticipationStatus.Closed).Where(p => optedIn.Contains(p.PlayerId)))
        {
            draft.Emit(new PlayerInvited(p.PlayerId, p.PageToken));
            draft.Notify(NoticeKind.Invite, Recipient.Player(p.PlayerId));
        }
        return draft.Done(ctx, organizerAction: true);
    }

    public static Decision Remove(Game game, GameContext ctx, Guid playerId)
    {
        RequireOpenForChanges(game);
        var p = game.Find(playerId);
        if (p is null || p.Status is not (ParticipationStatus.Confirmed or ParticipationStatus.Invited))
            throw new GameRuleException("That player isn't in or invited to this game.");

        var draft = new Draft(game);
        draft.Emit(new PlayerRemovedByOrganizer(playerId));
        if (game.Status == GameStatus.Full && p.Status == ParticipationStatus.Confirmed)
            draft.Emit(new SpotReopened());
        draft.Notify(NoticeKind.Removed, Recipient.Player(playerId));
        return draft.Done(ctx, organizerAction: true);
    }

    public static Decision ChangeDetails(
        Game game, GameContext ctx, DateTime startsAtLocal, DateTime endsAtLocal, string location, GameTimes newTimes)
    {
        RequireOpenForChanges(game);
        ValidateDetails(startsAtLocal, endsAtLocal, location, ctx.Now, newTimes);
        location = location.Trim();

        if (startsAtLocal == game.StartsAtLocal && endsAtLocal == game.EndsAtLocal && location == game.Location)
            return Decision.Nothing;

        var draft = new Draft(game);
        draft.Emit(new GameDetailsChanged(startsAtLocal, endsAtLocal, location));
        foreach (var p in game.Participants.Where(p => p.Status is ParticipationStatus.Confirmed or ParticipationStatus.Invited))
            draft.Notify(NoticeKind.GameChanged, Recipient.Player(p.PlayerId));
        return draft.Done(ctx, organizerAction: true);
    }

    public static Decision Cancel(Game game, GameContext ctx)
    {
        RequireOpenForChanges(game);
        var draft = new Draft(game);
        var affected = game.Participants
            .Where(p => p.Status is ParticipationStatus.Confirmed or ParticipationStatus.Invited)
            .Select(p => p.PlayerId).ToList();

        draft.Emit(new GameCancelled());
        foreach (var p in game.WithStatus(ParticipationStatus.Invited))
            draft.Emit(new InvitationClosed(p.PlayerId, CloseReason.GameCancelled));
        foreach (var id in affected)
            draft.Notify(NoticeKind.Cancelled, Recipient.Player(id));
        return draft.Done(ctx, organizerAction: true);
    }

    // ---------- Player replies ----------

    public static Decision Accept(Game game, GameContext ctx, Guid playerId, Channel channel, Guid? inboundMessageId = null)
    {
        if (RejectIfClosed(game, playerId) is { } closedReply) return closedReply;

        var p = game.Find(playerId);
        var me = Recipient.Player(playerId);

        if (p?.Status == ParticipationStatus.Confirmed)
            return Reply(NoticeKind.AlreadyIn, me, ReplyOutcome.AlreadyIn);
        if (p?.Status != ParticipationStatus.Invited || game.OpenSpots <= 0)
            return game.Status == GameStatus.Full || game.OpenSpots <= 0
                ? Reply(NoticeKind.LateYes, me, ReplyOutcome.GameFull)
                : Reply(NoticeKind.NotInvited, me, ReplyOutcome.NotInvited);

        var draft = new Draft(game);
        var previouslyConfirmed = game.WithStatus(ParticipationStatus.Confirmed).Select(x => x.PlayerId).ToList();

        draft.Emit(new InvitationAccepted(playerId, channel, inboundMessageId, ctx.Now));
        var closed = FillIfComplete(draft, ctx);

        if (draft.State.Status != GameStatus.Full)
        {
            draft.Notify(NoticeKind.Accepted, me);
            return draft.Done(ctx, outcome: ReplyOutcome.Accepted);
        }

        draft.Notify(NoticeKind.AcceptedAndFilled, me);
        foreach (var id in previouslyConfirmed)
            draft.Notify(NoticeKind.GameOn, Recipient.Player(id));
        foreach (var id in closed)
            draft.Notify(NoticeKind.FilledThanksAnyway, Recipient.Player(id));
        draft.Notify(NoticeKind.OrganizerGameFilled, Recipient.Organizer);
        return draft.Done(ctx, outcome: ReplyOutcome.AcceptedAndFilled);
    }

    public static Decision Decline(Game game, GameContext ctx, Guid playerId, Channel channel, Guid? inboundMessageId = null)
    {
        if (RejectIfClosed(game, playerId) is { } closedReply) return closedReply;

        var p = game.Find(playerId);
        var me = Recipient.Player(playerId);

        switch (p?.Status)
        {
            case ParticipationStatus.Confirmed:
                return Reply(NoticeKind.AlreadyIn, me, ReplyOutcome.AlreadyIn);
            case ParticipationStatus.Invited:
                var draft = new Draft(game);
                draft.Emit(new InvitationDeclined(playerId, channel, inboundMessageId));
                draft.Notify(NoticeKind.Declined, me);
                return draft.Done(ctx, outcome: ReplyOutcome.Declined, checkShortfall: true);
            default:
                // Nothing to decline. A plain acknowledgment is harmless.
                return Reply(NoticeKind.Declined, me, ReplyOutcome.NotInvited);
        }
    }

    public static Decision Drop(Game game, GameContext ctx, Guid playerId, Channel channel, Guid? inboundMessageId = null)
    {
        if (RejectIfClosed(game, playerId) is { } closedReply) return closedReply;

        var p = game.Find(playerId);
        if (p?.Status == ParticipationStatus.Invited)
            return Decline(game, ctx, playerId, channel, inboundMessageId); // OUT before saying yes means no.
        if (p?.Status != ParticipationStatus.Confirmed)
            return Reply(NoticeKind.NotInGame, Recipient.Player(playerId), ReplyOutcome.NotInGame);

        var draft = new Draft(game);
        var within = DropConfirmed(draft, game, ctx, playerId, channel, DropReason.PlayerRequest);
        draft.Notify(NoticeKind.Dropped, Recipient.Player(playerId));
        draft.Notify(NoticeKind.OrganizerPlayerDropped, Recipient.Organizer, playerId, within);
        return draft.Done(ctx, outcome: ReplyOutcome.Dropped, checkShortfall: true);
    }

    /// <summary>
    /// The player texted STOP (spec §4.2). Close their invitation or drop them, alert the
    /// organizer if they were playing, and text the player nothing (the STOP confirmation is separate).
    /// </summary>
    public static Decision PlayerOptedOut(Game game, GameContext ctx, Guid playerId)
    {
        if (game.IsClosedForChanges) return Decision.Nothing;

        var p = game.Find(playerId);
        var draft = new Draft(game);
        switch (p?.Status)
        {
            case ParticipationStatus.Invited:
                draft.Emit(new InvitationClosed(playerId, CloseReason.OptedOut));
                break;
            case ParticipationStatus.Confirmed:
                var within = DropConfirmed(draft, game, ctx, playerId, Channel.Sms, DropReason.OptedOut);
                draft.Notify(NoticeKind.OrganizerPlayerDropped, Recipient.Organizer, playerId, within);
                break;
            default:
                return Decision.Nothing;
        }
        return draft.Done(ctx, checkShortfall: true);
    }

    // ---------- Timers ----------

    public static Decision Start(Game game, GameContext ctx)
    {
        if (game.IsClosedForChanges) return Decision.Nothing;
        var draft = new Draft(game);
        draft.Emit(new GameStarted());
        foreach (var p in game.WithStatus(ParticipationStatus.Invited))
            draft.Emit(new InvitationClosed(p.PlayerId, CloseReason.GameStarted));
        return draft.Done(ctx);
    }

    /// <summary>
    /// Evening-before reminder. Skips anyone who became confirmed, or got the "game on" text,
    /// after the reminder time: they just heard about the game.
    /// </summary>
    public static Decision Remind(Game game, GameContext ctx)
    {
        if (game.IsClosedForChanges || game.ReminderSent) return Decision.Nothing;

        var reminderAt = ctx.Times.ReminderAt;
        var justGotGameOn = game.FilledAt > reminderAt;

        var draft = new Draft(game);
        draft.Emit(new ReminderIssued());
        if (!justGotGameOn)
            foreach (var p in game.WithStatus(ParticipationStatus.Confirmed).Where(p => p.ConfirmedAt <= reminderAt))
                draft.Notify(NoticeKind.Reminder, Recipient.Player(p.PlayerId));
        return draft.Done(ctx);
    }

    public static Decision AlertNotFull(Game game, GameContext ctx)
    {
        if (game.IsClosedForChanges || game.NotFullAlertSent || game.Status == GameStatus.Full)
            return Decision.Nothing;
        var draft = new Draft(game);
        draft.Emit(new CancellationWindowAlertIssued());
        draft.Notify(NoticeKind.OrganizerNotFullAlert, Recipient.Organizer);
        return draft.Done(ctx);
    }

    // ---------- Helpers ----------

    private static bool DropConfirmed(Draft draft, Game game, GameContext ctx, Guid playerId, Channel channel, DropReason reason)
    {
        var within = ctx.Now >= ctx.Times.CancellationCutoff;
        draft.Emit(new PlayerDropped(playerId, channel, within, reason));
        if (game.Status == GameStatus.Full)
            draft.Emit(new SpotReopened());
        return within;
    }

    /// <summary>If the last spot was just taken, mark the game full and close the other invitations.</summary>
    private static List<Guid> FillIfComplete(Draft draft, GameContext ctx)
    {
        if (draft.State.Status != GameStatus.Open || draft.State.OpenSpots > 0) return [];

        draft.Emit(new GameFilled(ctx.Now));
        var closed = draft.State.WithStatus(ParticipationStatus.Invited).Select(p => p.PlayerId).ToList();
        foreach (var id in closed)
            draft.Emit(new InvitationClosed(id, CloseReason.GameFilled));
        return closed;
    }

    private static Decision? RejectIfClosed(Game game, Guid playerId) => game.Status switch
    {
        GameStatus.Cancelled => Reply(NoticeKind.AlreadyCancelled, Recipient.Player(playerId), ReplyOutcome.GameCancelled),
        GameStatus.Started => Reply(NoticeKind.AlreadyStarted, Recipient.Player(playerId), ReplyOutcome.GameStarted),
        _ => null,
    };

    private static Decision Reply(NoticeKind kind, Recipient to, ReplyOutcome outcome) =>
        new([], [new Notice(kind, to)], outcome);

    private static void RequireOpenForChanges(Game game)
    {
        if (game.Status == GameStatus.Cancelled) throw new GameRuleException("The game has been cancelled.");
        if (game.Status == GameStatus.Started) throw new GameRuleException("The game has already started.");
    }

    private static void ValidateDetails(DateTime startsAtLocal, DateTime endsAtLocal, string location, DateTimeOffset now, GameTimes times)
    {
        if (endsAtLocal <= startsAtLocal) throw new GameRuleException("The game must end after it starts.");
        if (string.IsNullOrWhiteSpace(location)) throw new GameRuleException("Location is required.");
        if (times.StartsAt <= now) throw new GameRuleException("The game must start in the future.");
    }

    /// <summary>Accumulates events (applied to a working copy of the state) and notices.</summary>
    private sealed class Draft(Game game)
    {
        private readonly List<object> _events = [];
        private readonly List<Notice> _notices = [];
        public Game State { get; } = game.Clone();

        public void Emit(object e)
        {
            _events.Add(e);
            GameEvolver.Apply(State, e);
        }

        public void Notify(NoticeKind kind, Recipient to, Guid? about = null, bool within = false) =>
            _notices.Add(new Notice(kind, to, about, within));

        /// <summary>
        /// Can't-fill alert (enhancements E-6): when the people still waiting can't fill the open
        /// spots, tell the organizer once. A player's reply or STOP triggers it, not the organizer's own
        /// actions (the game page shows the shortfall). If the organizer is already getting a drop
        /// alert, the shortfall goes into that text, so they get one text, not two.
        /// </summary>
        private void AlertShortfall()
        {
            var shortfall = State.Shortfall;
            if (shortfall == 0 || State.ShortfallAlertSent) return;

            Emit(new ShortfallAlertIssued(shortfall));
            var drop = _notices.FindIndex(n => n.Kind == NoticeKind.OrganizerPlayerDropped);
            if (drop >= 0)
                _notices[drop] = _notices[drop] with { Shortfall = shortfall };
            else
                _notices.Add(new Notice(NoticeKind.OrganizerShortfall, Recipient.Organizer, Shortfall: shortfall));
        }

        /// <summary>
        /// Applies the "one text per number per command" rule for the organizer (spec §5):
        /// no player texts to the organizer about their own actions, and when the organizer
        /// gets an organizer text they don't also get the player version.
        /// </summary>
        public Decision Done(
            GameContext ctx, bool organizerAction = false, ReplyOutcome outcome = ReplyOutcome.None, bool checkShortfall = false)
        {
            if (checkShortfall) AlertShortfall();

            IEnumerable<Notice> notices = _notices;
            if (ctx.OrganizerPlayerId is { } organizerPlayer &&
                (organizerAction || _notices.Any(n => n.To.IsOrganizer)))
            {
                notices = notices.Where(n => n.To.PlayerId != organizerPlayer);
            }
            return new Decision(_events, notices.ToList(), outcome);
        }
    }
}
