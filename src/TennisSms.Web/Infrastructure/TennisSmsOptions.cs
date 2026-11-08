namespace TennisSms.Web.Infrastructure;

/// <summary>App configuration, section "TennisSms".</summary>
public sealed class TennisSmsOptions
{
    public const string Section = "TennisSms";

    /// <summary>Short prefix on invitations and keyword replies. Working name until OD-4 is decided.</summary>
    public string Brand { get; set; } = "TennisSMS";

    /// <summary>Public base URL for links in texts, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5234";

    /// <summary>The service's own number (the "to" on inbound texts).</summary>
    public string ServiceNumber { get; set; } = "+15550100000";

    /// <summary>"Simulator" in v0.1. "Twilio" later (spec §13).</summary>
    public string Gateway { get; set; } = "Simulator";

    public OrganizerOptions Organizer { get; set; } = new();

    /// <summary>How long a "Which game?" question stays answerable (spec §6.3).</summary>
    public TimeSpan ClarificationExpiry { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Global cap on replies to unknown numbers per hour (spec §6.4).</summary>
    public int UnknownReplyCapPerHour { get; set; } = 20;
}

public sealed class OrganizerOptions
{
    /// <summary>How the organizer is named in texts ("Phil added you to...").</summary>
    public string Name { get; set; } = "Phil";

    /// <summary>
    /// Where organizer texts go (E.164). If a player has this number, that player is the
    /// organizer playing, and gets organizer texts instead of duplicate player texts.
    /// </summary>
    public string PhoneE164 { get; set; } = "+15550100001";
}
