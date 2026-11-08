namespace TennisSms.Core.Games;

/// <summary>
/// Folds events into a <see cref="Game"/> without the event store, for decisions and tests.
/// Polecat uses the same Create/Apply methods through its own generated dispatcher.
/// </summary>
public static class GameEvolver
{
    public static Game From(params object[] events) => From((IEnumerable<object>)events);

    public static Game From(IEnumerable<object> events)
    {
        Game? game = null;
        foreach (var e in events)
            game = game is null ? Game.Create((GameScheduled)e) : Apply(game, e);
        return game ?? throw new InvalidOperationException("No events.");
    }

    public static Game Apply(Game game, object e)
    {
        switch (e)
        {
            case GameDetailsChanged x: game.Apply(x); break;
            case PlayerAddedByOrganizer x: game.Apply(x); break;
            case PlayerInvited x: game.Apply(x); break;
            case InvitationAccepted x: game.Apply(x); break;
            case InvitationDeclined x: game.Apply(x); break;
            case GameFilled x: game.Apply(x); break;
            case InvitationClosed x: game.Apply(x); break;
            case PlayerDropped x: game.Apply(x); break;
            case PlayerRemovedByOrganizer x: game.Apply(x); break;
            case SpotReopened x: game.Apply(x); break;
            case GameCancelled x: game.Apply(x); break;
            case GameStarted x: game.Apply(x); break;
            case ReminderIssued x: game.Apply(x); break;
            case CancellationWindowAlertIssued x: game.Apply(x); break;
            default: throw new ArgumentException($"Unknown game event {e.GetType().Name}");
        }
        return game;
    }
}
