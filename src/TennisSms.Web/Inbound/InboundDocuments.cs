using TennisSms.Web.Games;

namespace TennisSms.Web.Inbound;

/// <summary>An open "Which game?" question (spec §6.3). One per player: Id = player id.</summary>
public sealed class PendingClarification
{
    public Guid Id { get; set; }
    public GameReply Reply { get; set; }

    /// <summary>The games in the order they were listed: reply "1" means the first.</summary>
    public List<Guid> GameIds { get; set; } = [];

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A number that isn't a player (spec §6.4). Id = the E.164 number.</summary>
public sealed class UnknownSender
{
    public string Id { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int MessageCount { get; set; }
    public string LastBody { get; set; } = "";

    /// <summary>The "invited players only" reply goes at most once per number.</summary>
    public bool JoinReplied { get; set; }

    public DateTimeOffset? LastStopReplyAt { get; set; }
    public DateTimeOffset? LastHelpReplyAt { get; set; }
}

/// <summary>The global hourly cap on replies to unknown numbers (spec §6.4). A single document.</summary>
public sealed class UnknownReplyBudget
{
    public const string SingletonId = "unknown-reply-budget";

    public string Id { get; set; } = SingletonId;
    public DateTimeOffset WindowStart { get; set; }
    public int Count { get; set; }
    public bool OrganizerAlerted { get; set; }
}
