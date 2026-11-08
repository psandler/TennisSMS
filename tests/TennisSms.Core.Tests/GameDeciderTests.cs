using TennisSms.Core.Games;
using static TennisSms.Core.Tests.TestGames;

namespace TennisSms.Core.Tests;

public class AcceptTests
{
    [Fact]
    public void Accept_with_open_spot_confirms_and_replies_once()
    {
        var game = Doubles(Added(Phil), Invited(Mike), Invited(Dave));

        var d = GameDecider.Accept(game, Ctx(), Mike, Channel.Sms);

        var e = Assert.IsType<InvitationAccepted>(Assert.Single(d.Events));
        Assert.Equal(Mike, e.PlayerId);
        var n = Assert.Single(d.Notices);
        Assert.Equal(new Notice(NoticeKind.Accepted, Recipient.Player(Mike)), n);
        Assert.Equal(ReplyOutcome.Accepted, d.Outcome);
    }

    [Fact]
    public void Accept_that_fills_the_game_closes_invites_and_sends_one_text_each()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam), Invited(Lee), Invited(Kim));

        var d = GameDecider.Accept(game, Ctx(), Sam, Channel.Sms);

        Assert.Collection(d.Events,
            e => Assert.IsType<InvitationAccepted>(e),
            e => Assert.IsType<GameFilled>(e),
            e => Assert.Equal(new InvitationClosed(Lee, CloseReason.GameFilled), e),
            e => Assert.Equal(new InvitationClosed(Kim, CloseReason.GameFilled), e));

        AssertOneTextPerRecipient(d);
        Assert.Equal(NoticeKind.AcceptedAndFilled, KindFor(d, Sam));
        Assert.Equal(NoticeKind.GameOn, KindFor(d, Phil));
        Assert.Equal(NoticeKind.GameOn, KindFor(d, Mike));
        Assert.Equal(NoticeKind.FilledThanksAnyway, KindFor(d, Lee));
        Assert.Contains(d.Notices, n => n.To.IsOrganizer && n.Kind == NoticeKind.OrganizerGameFilled);
        Assert.Equal(GameStatus.Full, After(game, d).Status);
    }

    [Fact]
    public void Organizer_who_is_playing_gets_only_the_organizer_text_when_the_game_fills()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam));

        var d = GameDecider.Accept(game, Ctx(organizer: Phil), Sam, Channel.Sms);

        Assert.DoesNotContain(d.Notices, n => n.To == Recipient.Player(Phil));
        Assert.Contains(d.Notices, n => n.To.IsOrganizer);
    }

    [Fact]
    public void Two_yeses_for_the_last_spot_the_second_is_told_it_filled()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam), Invited(Lee));

        var first = GameDecider.Accept(game, Ctx(), Sam, Channel.Sms);
        var second = GameDecider.Accept(After(game, first), Ctx(), Lee, Channel.Sms);

        Assert.Empty(second.Events);
        Assert.Equal(NoticeKind.LateYes, Assert.Single(second.Notices).Kind);
        Assert.Equal(ReplyOutcome.GameFull, second.Outcome);
        Assert.Equal(4, After(After(game, first), second).ConfirmedCount);
    }

    [Fact]
    public void Player_told_filled_cannot_claim_a_reopened_spot_by_replying()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam), Invited(Lee),
            new InvitationAccepted(Sam, Channel.Sms, null, Tuesday), new GameFilled(Tuesday),
            new InvitationClosed(Lee, CloseReason.GameFilled),
            new PlayerDropped(Mike, Channel.Sms, false, DropReason.PlayerRequest), new SpotReopened());

        var d = GameDecider.Accept(game, Ctx(), Lee, Channel.Sms);

        Assert.Empty(d.Events);
        Assert.Equal(NoticeKind.NotInvited, Assert.Single(d.Notices).Kind);
    }

    [Fact]
    public void Confirmed_player_saying_yes_again_is_told_they_are_already_in()
    {
        var d = GameDecider.Accept(Doubles(Added(Mike)), Ctx(), Mike, Channel.Sms);
        Assert.Empty(d.Events);
        Assert.Equal(NoticeKind.AlreadyIn, Assert.Single(d.Notices).Kind);
    }

    [Fact]
    public void Declined_player_cannot_accept()
    {
        var game = Doubles(Invited(Mike), new InvitationDeclined(Mike, Channel.Sms, null));
        var d = GameDecider.Accept(game, Ctx(), Mike, Channel.Sms);
        Assert.Empty(d.Events);
        Assert.Equal(ReplyOutcome.NotInvited, d.Outcome);
    }

    [Fact]
    public void Replies_after_start_or_cancel_change_nothing()
    {
        var started = GameDecider.Accept(Doubles(Invited(Mike), new GameStarted()), Ctx(), Mike, Channel.Sms);
        Assert.Empty(started.Events);
        Assert.Equal(NoticeKind.AlreadyStarted, Assert.Single(started.Notices).Kind);

        var cancelled = GameDecider.Drop(Doubles(Added(Mike), new GameCancelled()), Ctx(), Mike, Channel.Sms);
        Assert.Empty(cancelled.Events);
        Assert.Equal(NoticeKind.AlreadyCancelled, Assert.Single(cancelled.Notices).Kind);
    }

    internal static void AssertOneTextPerRecipient(Decision d) =>
        Assert.Equal(d.Notices.Count, d.Notices.Select(n => n.To).Distinct().Count());

    internal static NoticeKind KindFor(Decision d, Guid player) =>
        d.Notices.Single(n => n.To == Recipient.Player(player)).Kind;
}

public class DeclineAndDropTests
{
    [Fact]
    public void Decline_records_and_acknowledges()
    {
        // Enough people still asked to fill it, so no can't-fill alert.
        var game = Doubles(Added(Phil), Added(Dave), Invited(Mike), Invited(Sam), Invited(Lee));
        var d = GameDecider.Decline(game, Ctx(), Mike, Channel.Page);
        Assert.Equal(new InvitationDeclined(Mike, Channel.Page, null), Assert.Single(d.Events));
        Assert.Equal(NoticeKind.Declined, Assert.Single(d.Notices).Kind);
    }

    [Fact]
    public void Drop_from_full_game_reopens_spot_and_alerts_organizer()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(Tuesday));

        var d = GameDecider.Drop(game, Ctx(InsideWindow), Mike, Channel.Sms);

        Assert.Equal(new PlayerDropped(Mike, Channel.Sms, true, DropReason.PlayerRequest), d.Events[0]);
        Assert.IsType<SpotReopened>(d.Events[1]);
        Assert.Equal(NoticeKind.Dropped, AcceptTests.KindFor(d, Mike));
        var alert = d.Notices.Single(n => n.To.IsOrganizer);
        Assert.Equal(NoticeKind.OrganizerPlayerDropped, alert.Kind);
        Assert.Equal(Mike, alert.AboutPlayerId);
        Assert.True(alert.WithinCancellationWindow);
        Assert.Equal(GameStatus.Open, After(game, d).Status);
    }

    [Fact]
    public void Drop_before_the_cutoff_is_not_inside_the_window()
    {
        var d = GameDecider.Drop(Doubles(Added(Mike)), Ctx(Tuesday), Mike, Channel.Sms);
        Assert.False(((PlayerDropped)d.Events[0]).WithinCancellationWindow);
        Assert.DoesNotContain(d.Events, e => e is SpotReopened); // not full
    }

    [Fact]
    public void Out_from_an_invited_player_counts_as_no()
    {
        var d = GameDecider.Drop(Doubles(Invited(Mike)), Ctx(), Mike, Channel.Sms);
        Assert.IsType<InvitationDeclined>(d.Events[0]);
    }

    [Fact]
    public void Out_from_someone_not_in_the_game()
    {
        var d = GameDecider.Drop(Doubles(), Ctx(), Mike, Channel.Sms);
        Assert.Empty(d.Events);
        Assert.Equal(NoticeKind.NotInGame, Assert.Single(d.Notices).Kind);
    }
}

public class OrganizerActionTests
{
    [Fact]
    public void Adding_and_inviting_texts_each_player_once()
    {
        var d = GameDecider.AddAndInvite(Doubles(), Ctx(organizer: Phil),
            add: [Phil, Mike], invite: [Dave, Sam], AllOptedIn, NewToken);

        Assert.Equal(4, d.Events.Count);
        AcceptTests.AssertOneTextPerRecipient(d);
        Assert.Equal(NoticeKind.AddedByOrganizer, AcceptTests.KindFor(d, Mike));
        Assert.Equal(NoticeKind.Invite, AcceptTests.KindFor(d, Dave));
        Assert.DoesNotContain(d.Notices, n => n.To == Recipient.Player(Phil)); // organizer doesn't text themself
    }

    [Fact]
    public void Game_created_already_full_sends_only_game_on()
    {
        var d = GameDecider.AddAndInvite(Doubles(), Ctx(organizer: Phil),
            add: [Phil, Mike, Dave, Sam], invite: [], AllOptedIn, NewToken);

        Assert.IsType<GameFilled>(d.Events[^1]);
        Assert.All(d.Notices, n => Assert.Equal(NoticeKind.GameOn, n.Kind));
        Assert.Equal(3, d.Notices.Count);
    }

    [Fact]
    public void Adding_the_last_player_closes_open_invitations()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam));
        var d = GameDecider.AddAndInvite(game, Ctx(), add: [Lee], invite: [], AllOptedIn, NewToken);

        Assert.Contains(new InvitationClosed(Sam, CloseReason.GameFilled), d.Events);
        Assert.Equal(NoticeKind.FilledThanksAnyway, AcceptTests.KindFor(d, Sam));
        Assert.Equal(NoticeKind.GameOn, AcceptTests.KindFor(d, Lee));
        Assert.Equal(NoticeKind.GameOn, AcceptTests.KindFor(d, Mike));
    }

    [Fact]
    public void Rule_violations_are_rejected()
    {
        var game = Doubles(Added(Phil), Invited(Mike), new InvitationDeclined(Mike, Channel.Sms, null));

        Assert.Throws<GameRuleException>(() =>
            GameDecider.AddAndInvite(game, Ctx(), [], [Mike], AllOptedIn, NewToken)); // declined
        Assert.Throws<GameRuleException>(() =>
            GameDecider.AddAndInvite(game, Ctx(), [], [Dave], new HashSet<Guid>(), NewToken)); // not opted in
        Assert.Throws<GameRuleException>(() =>
            GameDecider.AddAndInvite(game, Ctx(), [Dave, Sam, Lee, Kim], [], AllOptedIn, NewToken)); // over capacity
        Assert.Throws<GameRuleException>(() =>
            GameDecider.AddAndInvite(Doubles(new GameCancelled()), Ctx(), [], [Dave], AllOptedIn, NewToken));
    }

    [Fact]
    public void Reinvite_goes_to_closed_invitations_but_never_decliners()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam), Invited(Lee), Invited(Kim),
            new InvitationDeclined(Kim, Channel.Sms, null),
            new InvitationAccepted(Sam, Channel.Sms, null, Tuesday), new GameFilled(Tuesday),
            new InvitationClosed(Lee, CloseReason.GameFilled),
            new PlayerDropped(Sam, Channel.Sms, false, DropReason.PlayerRequest), new SpotReopened());

        var d = GameDecider.ReinviteClosed(game, Ctx(), AllOptedIn);

        var invited = Assert.IsType<PlayerInvited>(Assert.Single(d.Events));
        Assert.Equal(Lee, invited.PlayerId);
        Assert.Equal(Invited(Lee).PageToken, invited.PageToken); // same link as before
    }

    [Fact]
    public void Removing_a_confirmed_player_from_a_full_game_reopens_it()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(Tuesday));
        var d = GameDecider.Remove(game, Ctx(), Sam);
        Assert.Equal([new PlayerRemovedByOrganizer(Sam), new SpotReopened()], d.Events);
        Assert.Equal(NoticeKind.Removed, Assert.Single(d.Notices).Kind);
    }

    [Fact]
    public void Changing_details_texts_confirmed_and_invited_players()
    {
        var game = Doubles(Added(Phil), Added(Mike), Invited(Dave), Invited(Sam), new InvitationDeclined(Sam, Channel.Sms, null));
        var newStart = Start.AddDays(1);
        var d = GameDecider.ChangeDetails(game, Ctx(organizer: Phil), newStart, newStart.AddHours(1), "Elm Park",
            Clock.For(newStart, newStart.AddHours(1)));

        Assert.Equal(new GameDetailsChanged(newStart, newStart.AddHours(1), "Elm Park"), Assert.Single(d.Events));
        Assert.Equal([Mike, Dave], d.Notices.Select(n => n.To.PlayerId!.Value));
        Assert.All(d.Notices, n => Assert.Equal(NoticeKind.GameChanged, n.Kind));
    }

    [Fact]
    public void Changing_nothing_is_a_no_op()
    {
        var d = GameDecider.ChangeDetails(Doubles(), Ctx(), Start, End, "the club", Times);
        Assert.True(d.IsEmpty);
    }

    [Fact]
    public void Cancel_closes_invitations_and_texts_everyone_involved()
    {
        var game = Doubles(Added(Mike), Invited(Dave), Invited(Sam), new InvitationDeclined(Sam, Channel.Sms, null));
        var d = GameDecider.Cancel(game, Ctx());

        Assert.Equal([new GameCancelled(), new InvitationClosed(Dave, CloseReason.GameCancelled)], d.Events);
        Assert.Equal([Mike, Dave], d.Notices.Select(n => n.To.PlayerId!.Value));
    }
}

public class TimerAndConsentTests
{
    [Fact]
    public void Start_closes_open_invitations_and_is_idempotent()
    {
        var game = Doubles(Added(Mike), Invited(Dave));
        var d = GameDecider.Start(game, Ctx());
        Assert.Equal([new GameStarted(), new InvitationClosed(Dave, CloseReason.GameStarted)], d.Events);
        Assert.Empty(d.Notices);
        Assert.True(GameDecider.Start(After(game, d), Ctx()).IsEmpty);
    }

    [Fact]
    public void Reminder_goes_to_confirmed_players_and_skips_late_confirmations()
    {
        var late = Times.ReminderAt.AddMinutes(10);
        var game = Doubles(Added(Phil), Added(Mike), Invited(Dave),
            new InvitationAccepted(Dave, Channel.Sms, null, late));

        var d = GameDecider.Remind(game, Ctx(late.AddMinutes(1)));

        Assert.IsType<ReminderIssued>(Assert.Single(d.Events));
        Assert.Equal([Phil, Mike], d.Notices.Select(n => n.To.PlayerId!.Value));
        Assert.True(GameDecider.Remind(After(game, d), Ctx()).IsEmpty); // fires twice safely
    }

    [Fact]
    public void Moving_the_start_time_rearms_the_reminder_and_alert_but_a_location_change_does_not()
    {
        var game = Doubles(Added(Mike), new ReminderIssued(), new CancellationWindowAlertIssued());

        var sameStart = GameEvolver.From([Scheduled(), Added(Mike), new ReminderIssued(), new CancellationWindowAlertIssued(),
            new GameDetailsChanged(Start, End, "other courts")]);
        Assert.True(sameStart.ReminderSent);
        Assert.True(sameStart.NotFullAlertSent);

        GameEvolver.Apply(game, new GameDetailsChanged(Start.AddDays(1), End.AddDays(1), game.Location));
        Assert.False(game.ReminderSent);
        Assert.False(game.NotFullAlertSent);
    }

    [Fact]
    public void Reminder_skips_everyone_who_just_got_game_on()
    {
        var late = Times.ReminderAt.AddMinutes(5);
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(late));
        var d = GameDecider.Remind(game, Ctx(late));
        Assert.IsType<ReminderIssued>(Assert.Single(d.Events));
        Assert.Empty(d.Notices);
    }

    [Fact]
    public void Not_full_alert_only_when_not_full_and_only_once()
    {
        var game = Doubles(Added(Phil), Invited(Mike));
        var d = GameDecider.AlertNotFull(game, Ctx());
        Assert.IsType<CancellationWindowAlertIssued>(Assert.Single(d.Events));
        Assert.True(Assert.Single(d.Notices).To.IsOrganizer);
        Assert.True(GameDecider.AlertNotFull(After(game, d), Ctx()).IsEmpty);

        var full = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(Tuesday));
        Assert.True(GameDecider.AlertNotFull(full, Ctx()).IsEmpty);
    }

    [Fact]
    public void Stop_closes_an_invitation_quietly()
    {
        // Enough others still invited, so no can't-fill alert either.
        var d = GameDecider.PlayerOptedOut(Doubles(Invited(Mike), Invited(Dave), Invited(Sam), Invited(Lee), Invited(Kim)), Ctx(), Mike);
        Assert.Equal(new InvitationClosed(Mike, CloseReason.OptedOut), Assert.Single(d.Events));
        Assert.Empty(d.Notices);
    }

    [Fact]
    public void Stop_from_a_confirmed_player_drops_them_and_alerts_the_organizer()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(Tuesday));
        var d = GameDecider.PlayerOptedOut(game, Ctx(), Mike);
        Assert.Equal(new PlayerDropped(Mike, Channel.Sms, false, DropReason.OptedOut), d.Events[0]);
        Assert.IsType<SpotReopened>(d.Events[1]);
        Assert.True(Assert.Single(d.Notices).To.IsOrganizer); // nothing to the player
    }
}

/// <summary>Can't-fill alert (enhancements E-6).</summary>
public class ShortfallTests
{
    private static InvitationAccepted Yes(Guid id) => new(id, Channel.Sms, null, Tuesday);

    /// <summary>Four invited to doubles; two said yes.</summary>
    private static Game TwoInTwoWaiting() =>
        Doubles(Invited(Mike), Invited(Dave), Invited(Sam), Invited(Lee), Yes(Mike), Yes(Dave));

    [Fact]
    public void Two_yes_one_no_one_waiting_tells_the_organizer_to_invite_one_more()
    {
        var game = TwoInTwoWaiting();
        Assert.Equal(0, game.Shortfall);

        var d = GameDecider.Decline(game, Ctx(), Sam, Channel.Sms);

        Assert.Contains(new ShortfallAlertIssued(1), d.Events);
        AcceptTests.AssertOneTextPerRecipient(d);
        Assert.Equal(NoticeKind.Declined, AcceptTests.KindFor(d, Sam));
        var alert = d.Notices.Single(n => n.To.IsOrganizer);
        Assert.Equal((NoticeKind.OrganizerShortfall, 1), (alert.Kind, alert.Shortfall));
    }

    [Fact]
    public void The_alert_is_sent_once_until_the_organizer_invites_enough_again()
    {
        var game = TwoInTwoWaiting();
        game = After(game, GameDecider.Decline(game, Ctx(), Sam, Channel.Sms));

        var second = GameDecider.Decline(game, Ctx(), Lee, Channel.Sms);
        Assert.DoesNotContain(second.Events, e => e is ShortfallAlertIssued);
        Assert.DoesNotContain(second.Notices, n => n.To.IsOrganizer);
        game = After(game, second);
        Assert.Equal(2, game.Shortfall);

        // The organizer invites two more, which covers it; when one of them says no, it's short again.
        game = After(game, GameDecider.AddAndInvite(game, Ctx(), [], [Kim, Id(7)], AllOptedIn, NewToken));
        Assert.Equal((0, false), (game.Shortfall, game.ShortfallAlertSent));

        var third = GameDecider.Decline(game, Ctx(), Kim, Channel.Sms);
        Assert.Contains(new ShortfallAlertIssued(1), third.Events);
    }

    [Fact]
    public void A_drop_that_leaves_a_shortfall_sends_one_combined_organizer_text()
    {
        var game = Doubles(Added(Phil), Added(Mike), Added(Dave), Invited(Sam));

        var d = GameDecider.Drop(game, Ctx(InsideWindow), Mike, Channel.Sms);

        var alert = Assert.Single(d.Notices, n => n.To.IsOrganizer);
        Assert.Equal((NoticeKind.OrganizerPlayerDropped, Mike, 1), (alert.Kind, alert.AboutPlayerId, alert.Shortfall));
        Assert.Contains(new ShortfallAlertIssued(1), d.Events); // 2 in, 1 waiting
    }

    [Fact]
    public void Stop_closing_an_invitation_can_cause_the_alert()
    {
        var game = Doubles(Added(Mike), Added(Dave), Added(Sam), Invited(Lee));
        var d = GameDecider.PlayerOptedOut(game, Ctx(), Lee);
        Assert.Equal(NoticeKind.OrganizerShortfall, Assert.Single(d.Notices).Kind);
    }

    [Fact]
    public void The_organizers_own_changes_and_full_games_never_alert()
    {
        var game = TwoInTwoWaiting();
        var removed = GameDecider.Remove(game, Ctx(), Sam);
        Assert.DoesNotContain(removed.Events, e => e is ShortfallAlertIssued);
        Assert.Equal(1, After(game, removed).Shortfall); // the game page shows it

        var full = Doubles(Added(Phil), Added(Mike), Added(Dave), Added(Sam), new GameFilled(Tuesday), Invited(Lee));
        Assert.Equal(0, full.Shortfall);
    }
}
