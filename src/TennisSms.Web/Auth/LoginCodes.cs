using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Polecat;
using TennisSms.Core.Messages;
using TennisSms.Core.Players;
using TennisSms.Web.Infrastructure;
using TennisSms.Web.Sms;
using Wolverine;

namespace TennisSms.Web.Auth;

/// <summary>
/// A one-time login code (spec §11.1). One per phone: Id = E.164 number. Only a hash is
/// stored; the code itself exists only in the text.
/// </summary>
public sealed class LoginCode
{
    public string Id { get; set; } = "";
    public string Hash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
}

public sealed record RequestLoginCode(string Phone);
public sealed record VerifyLoginCode(string Phone, string Code);
public sealed record LoginCodeResult(bool Ok, string? Error = null);

public static class LoginCodeHandler
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxAttempts = 5;

    /// <summary>A new code can't be requested more often than this (each one is a paid text).</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Texts a code if the number is the organizer's. Says nothing either way, so the login
    /// page can't be used to discover the organizer's number.
    /// </summary>
    public static async Task<OutgoingMessages> Handle(
        RequestLoginCode cmd, IDocumentSession session, IOptions<TennisSmsOptions> options, TimeProvider time, CancellationToken ct)
    {
        var organizerPhone = options.Value.Organizer.PhoneE164;
        if (!PhoneNumbers.TryNormalizeUs(cmd.Phone, out var phone) || phone != organizerPhone) return [];

        var now = time.GetUtcNow();
        var existing = await session.LoadAsync<LoginCode>(phone, ct);
        if (existing is not null && now - existing.CreatedAt < ResendCooldown) return [];

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        session.Store(new LoginCode { Id = phone, Hash = HashOf(phone, code), CreatedAt = now, ExpiresAt = now + Lifetime });

        var body = GeneralMessages.LoginCode(options.Value.Brand, code, (int)Lifetime.TotalMinutes);
        return [SendSms.Create(phone, body, "LoginCode")];
    }

    public static async Task<LoginCodeResult> Handle(
        VerifyLoginCode cmd, IDocumentSession session, TimeProvider time, CancellationToken ct)
    {
        const string expired = "That code has expired. Ask for a new one.";
        if (!PhoneNumbers.TryNormalizeUs(cmd.Phone, out var phone)) return new(false, expired);

        var login = await session.LoadAsync<LoginCode>(phone, ct);
        if (login is null || login.ExpiresAt <= time.GetUtcNow()) return new(false, expired);

        var given = new string((cmd.Code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(HashOf(phone, given)), Encoding.UTF8.GetBytes(login.Hash)))
        {
            session.Delete<LoginCode>(phone); // single use
            return new(true);
        }

        login.Attempts++;
        if (login.Attempts >= MaxAttempts)
        {
            session.Delete<LoginCode>(phone);
            return new(false, "Too many wrong codes. Ask for a new one.");
        }
        session.Store(login);
        return new(false, "That code isn't right.");
    }

    private static string HashOf(string phone, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{phone}:{code}")));
}
