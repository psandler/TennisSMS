using TennisSms.Core.Games;
using TennisSms.Core.Messages;

namespace TennisSms.Web.Components;

/// <summary>Small display helpers shared by the pages.</summary>
public static class Ui
{
    /// <summary>"+13125550123" → "(312) 555-0123".</summary>
    public static string Phone(string e164) =>
        e164.Length == 12 && e164.StartsWith("+1")
            ? $"({e164[2..5]}) {e164[5..8]}-{e164[8..]}"
            : e164;

    public static string When(Game g) => TimeText.Range(g.StartsAtLocal, g.EndsAtLocal);

    public static string Status(Game g) => g.Status switch
    {
        GameStatus.Open => $"Open: {g.OpenSpots} {(g.OpenSpots == 1 ? "spot" : "spots")}",
        GameStatus.Full => "Full",
        GameStatus.Cancelled => "Cancelled",
        GameStatus.Started => "Started",
        _ => g.Status.ToString(),
    };

    public static string StatusClass(Game g) => g.Status.ToString().ToLowerInvariant();

    public static string CloseReason(CloseReason? reason) => reason switch
    {
        Core.Games.CloseReason.GameFilled => "told it filled",
        Core.Games.CloseReason.GameStarted => "game started",
        Core.Games.CloseReason.GameCancelled => "game cancelled",
        Core.Games.CloseReason.OptedOut => "opted out (STOP)",
        _ => "closed",
    };
}
