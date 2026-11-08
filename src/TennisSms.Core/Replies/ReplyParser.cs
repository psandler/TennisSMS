using System.Text.RegularExpressions;

namespace TennisSms.Core.Replies;

public enum ReplyIntent { Join, Stop, Help, Accept, Decline, Drop, Choice, Unknown }

public sealed record ParsedReply(ReplyIntent Intent, int? Choice = null, string? Keyword = null)
{
    public static ParsedReply Unknown { get; } = new(ReplyIntent.Unknown);
}

/// <summary>
/// Turns an inbound text into an intent (spec §6.2). Case-insensitive, trimmed, tolerant of
/// trailing punctuation and curly apostrophes. Anything not listed is Unknown.
/// </summary>
public static partial class ReplyParser
{
    private static readonly Dictionary<string, ReplyIntent> Words = new(StringComparer.Ordinal)
    {
        ["JOIN"] = ReplyIntent.Join, ["START"] = ReplyIntent.Join, ["UNSTOP"] = ReplyIntent.Join,

        // Carrier-standard opt-out words. CANCEL here is opt-out, not "cancel the game".
        ["STOP"] = ReplyIntent.Stop, ["STOPALL"] = ReplyIntent.Stop, ["UNSUBSCRIBE"] = ReplyIntent.Stop,
        ["CANCEL"] = ReplyIntent.Stop, ["END"] = ReplyIntent.Stop, ["QUIT"] = ReplyIntent.Stop,

        ["HELP"] = ReplyIntent.Help,

        ["YES"] = ReplyIntent.Accept, ["Y"] = ReplyIntent.Accept, ["YEP"] = ReplyIntent.Accept,
        ["YEAH"] = ReplyIntent.Accept, ["IN"] = ReplyIntent.Accept, ["I'M IN"] = ReplyIntent.Accept,
        ["IM IN"] = ReplyIntent.Accept,

        ["NO"] = ReplyIntent.Decline, ["N"] = ReplyIntent.Decline, ["NOPE"] = ReplyIntent.Decline,
        ["CAN'T"] = ReplyIntent.Decline, ["CANT"] = ReplyIntent.Decline,

        ["OUT"] = ReplyIntent.Drop, ["DROP"] = ReplyIntent.Drop,
    };

    public static ParsedReply Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParsedReply.Unknown;

        var trimmed = text.Trim();

        // iPhone tapback sent as text to non-iPhones: Liked "…" / Loved "…" / Disliked "…".
        var reaction = Reaction().Match(trimmed);
        if (reaction.Success)
        {
            return reaction.Groups["verb"].Value.ToUpperInvariant() switch
            {
                "LIKED" or "LOVED" => new ParsedReply(ReplyIntent.Accept),
                "DISLIKED" => new ParsedReply(ReplyIntent.Decline),
                _ => ParsedReply.Unknown,
            };
        }

        var normalized = Normalize(trimmed);

        if (Number().IsMatch(normalized))
            return new ParsedReply(ReplyIntent.Choice, Choice: int.Parse(normalized));

        return Words.TryGetValue(normalized, out var intent)
            ? new ParsedReply(intent, Keyword: normalized)
            : ParsedReply.Unknown;
    }

    private static string Normalize(string text)
    {
        var s = text.Replace('’', '\'').Replace('‘', '\'').ToUpperInvariant();
        s = TrailingPunctuation().Replace(s, "");
        return Whitespace().Replace(s, " ").Trim();
    }

    [GeneratedRegex("""^(?<verb>Liked|Loved|Disliked|Laughed at|Emphasized|Questioned)\s+[“"].*[”"]\s*$""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Reaction();

    [GeneratedRegex(@"^\d{1,2}$")]
    private static partial Regex Number();

    [GeneratedRegex(@"[\s.!?,;:]+$")]
    private static partial Regex TrailingPunctuation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
