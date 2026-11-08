using TennisSms.Core.Messages;

namespace TennisSms.Core.Players;

public enum ConsentStatus { Pending, OptedIn, OptedOut }

public enum OptOutSource { StopKeyword, Provider }

// Events on a player stream (spec §4.1): profile and consent together.
public sealed record PlayerRegistered(Guid PlayerId, string Name, string PhoneE164, DateTimeOffset At);
public sealed record PlayerDetailsChanged(string Name, string PhoneE164, DateTimeOffset At);
public sealed record PlayerOptedIn(string Keyword, Guid? InboundMessageId, DateTimeOffset At);
public sealed record PlayerOptedOut(OptOutSource Source, Guid? InboundMessageId, DateTimeOffset At);

/// <summary>Current state of a player. Polecat stores it as an inline snapshot (unique phone index).</summary>
public sealed partial class Player
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string PhoneE164 { get; set; } = "";
    public ConsentStatus ConsentStatus { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static Player Create(PlayerRegistered e) => new()
    {
        Id = e.PlayerId,
        Name = e.Name,
        PhoneE164 = e.PhoneE164,
        ConsentStatus = ConsentStatus.Pending,
        CreatedAt = e.At,
        UpdatedAt = e.At,
    };

    public void Apply(PlayerDetailsChanged e)
    {
        Name = e.Name;
        PhoneE164 = e.PhoneE164;
        UpdatedAt = e.At;
    }

    public void Apply(PlayerOptedIn e)
    {
        ConsentStatus = ConsentStatus.OptedIn;
        UpdatedAt = e.At;
    }

    public void Apply(PlayerOptedOut e)
    {
        ConsentStatus = ConsentStatus.OptedOut;
        UpdatedAt = e.At;
    }
}

public sealed class PlayerRuleException(string message) : Exception(message);

/// <summary>Player rules: names that fit in texts, phone normalization, consent changes.</summary>
public static class PlayerRules
{
    /// <summary>Longest name allowed. Keeps every template within one SMS segment (spec §4.1).</summary>
    public const int MaxNameLength = 12;

    public static string ValidateName(string name)
    {
        name = string.Join(' ', (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new PlayerRuleException("Name is required.");
        if (name.Length > MaxNameLength) throw new PlayerRuleException($"Name must be {MaxNameLength} characters or fewer.");
        if (!Gsm7.IsBasic(name)) throw new PlayerRuleException("Name can only use plain letters, numbers and basic punctuation.");
        return name;
    }

    public static PlayerRegistered Register(Guid id, string name, string phone, DateTimeOffset now) =>
        new(id, ValidateName(name), PhoneNumbers.NormalizeUs(phone), now);

    public static PlayerDetailsChanged? ChangeDetails(Player player, string name, string phone, DateTimeOffset now)
    {
        var e = new PlayerDetailsChanged(ValidateName(name), PhoneNumbers.NormalizeUs(phone), now);
        return e.Name == player.Name && e.PhoneE164 == player.PhoneE164 ? null : e;
    }

    /// <summary>Null when already opted in (no event; the player still gets the join confirmation).</summary>
    public static PlayerOptedIn? OptIn(Player player, string keyword, Guid? inboundMessageId, DateTimeOffset now) =>
        player.ConsentStatus == ConsentStatus.OptedIn ? null : new PlayerOptedIn(keyword, inboundMessageId, now);

    public static PlayerOptedOut? OptOut(Player player, OptOutSource source, Guid? inboundMessageId, DateTimeOffset now) =>
        player.ConsentStatus == ConsentStatus.OptedOut ? null : new PlayerOptedOut(source, inboundMessageId, now);
}

public static class PhoneNumbers
{
    /// <summary>
    /// Normalizes a US number to E.164 (+1XXXXXXXXXX). Accepts common formats:
    /// "(312) 555-0123", "312.555.0123", "1-312-555-0123", "+13125550123".
    /// </summary>
    public static string NormalizeUs(string input)
    {
        var digits = new string((input ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 11 && digits[0] == '1') digits = digits[1..];
        if (digits.Length != 10 || digits[0] is '0' or '1')
            throw new PlayerRuleException("Enter a 10-digit US mobile number.");
        return "+1" + digits;
    }

    public static bool TryNormalizeUs(string input, out string e164)
    {
        try { e164 = NormalizeUs(input); return true; }
        catch (PlayerRuleException) { e164 = ""; return false; }
    }
}
