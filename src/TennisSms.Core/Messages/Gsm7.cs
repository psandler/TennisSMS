namespace TennisSms.Core.Messages;

/// <summary>
/// GSM 03.38 (GSM-7) checks for the one-segment rule (spec §8.2): at most 160 septets, and
/// no character outside GSM-7. Any other character (emoji, curly quotes, em dash) forces UCS-2
/// encoding, which cuts a segment to 70 characters.
/// </summary>
public static class Gsm7
{
    public const int SingleSegmentSeptets = 160;

    private const string BasicSet =
        "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
        "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";

    /// <summary>Extension table characters; each costs two septets (escape + char).</summary>
    private const string ExtensionSet = "^{}\\[~]|€\f";

    private static readonly HashSet<char> Basic = [.. BasicSet];
    private static readonly HashSet<char> Extension = [.. ExtensionSet];

    public static bool IsBasic(string text) => text.All(Basic.Contains);

    public static bool IsEncodable(string text) => text.All(c => Basic.Contains(c) || Extension.Contains(c));

    public static int SeptetCount(string text) => text.Sum(c => Extension.Contains(c) ? 2 : 1);

    public static bool FitsOneSegment(string text) => IsEncodable(text) && SeptetCount(text) <= SingleSegmentSeptets;

    /// <summary>The characters in <paramref name="text"/> that GSM-7 can't encode.</summary>
    public static IReadOnlyList<char> Unencodable(string text) =>
        text.Where(c => !Basic.Contains(c) && !Extension.Contains(c)).Distinct().ToList();

    /// <summary>
    /// Replaces common non-GSM characters that sneak in from phones and copy/paste
    /// (curly quotes, dashes, non-breaking space) with plain equivalents.
    /// </summary>
    public static string Plain(string text) => text
        .Replace('‘', '\'').Replace('’', '\'')
        .Replace('“', '"').Replace('”', '"')
        .Replace('–', '-').Replace('—', '-')
        .Replace(' ', ' ')
        .Replace("…", "...");
}
