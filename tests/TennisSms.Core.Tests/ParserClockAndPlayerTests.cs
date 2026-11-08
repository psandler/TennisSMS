using TennisSms.Core.Players;
using TennisSms.Core.Replies;
using TennisSms.Core.Time;

namespace TennisSms.Core.Tests;

public class ReplyParserTests
{
    [Theory]
    [InlineData("JOIN", ReplyIntent.Join)]
    [InlineData("join ", ReplyIntent.Join)]
    [InlineData("Start", ReplyIntent.Join)]
    [InlineData("UNSTOP", ReplyIntent.Join)]
    [InlineData("STOP", ReplyIntent.Stop)]
    [InlineData("stopall", ReplyIntent.Stop)]
    [InlineData("Unsubscribe", ReplyIntent.Stop)]
    [InlineData("cancel", ReplyIntent.Stop)]
    [InlineData("END", ReplyIntent.Stop)]
    [InlineData("quit", ReplyIntent.Stop)]
    [InlineData("help", ReplyIntent.Help)]
    [InlineData("YES", ReplyIntent.Accept)]
    [InlineData("y", ReplyIntent.Accept)]
    [InlineData("Yep!", ReplyIntent.Accept)]
    [InlineData("yeah.", ReplyIntent.Accept)]
    [InlineData("in", ReplyIntent.Accept)]
    [InlineData("I'm in", ReplyIntent.Accept)]
    [InlineData("I’m in!!", ReplyIntent.Accept)]
    [InlineData("im  in", ReplyIntent.Accept)]
    [InlineData("NO", ReplyIntent.Decline)]
    [InlineData("n", ReplyIntent.Decline)]
    [InlineData("nope", ReplyIntent.Decline)]
    [InlineData("can't", ReplyIntent.Decline)]
    [InlineData("Can’t", ReplyIntent.Decline)]
    [InlineData("cant", ReplyIntent.Decline)]
    [InlineData("OUT", ReplyIntent.Drop)]
    [InlineData("drop", ReplyIntent.Drop)]
    [InlineData("Liked “Doubles Fri 11/13 1-2pm at the club...”", ReplyIntent.Accept)]
    [InlineData("Loved \"Doubles Fri\"", ReplyIntent.Accept)]
    [InlineData("Disliked “Doubles Fri”", ReplyIntent.Decline)]
    [InlineData("Laughed at “Doubles Fri”", ReplyIntent.Unknown)]
    [InlineData("maybe", ReplyIntent.Unknown)]
    [InlineData("yes but late", ReplyIntent.Unknown)]
    [InlineData("", ReplyIntent.Unknown)]
    [InlineData("   ", ReplyIntent.Unknown)]
    public void Parses(string text, ReplyIntent expected) => Assert.Equal(expected, ReplyParser.Parse(text).Intent);

    [Theory]
    [InlineData("1", 1)]
    [InlineData(" 2.", 2)]
    [InlineData("10", 10)]
    public void Parses_clarification_numbers(string text, int choice)
    {
        var r = ReplyParser.Parse(text);
        Assert.Equal(ReplyIntent.Choice, r.Intent);
        Assert.Equal(choice, r.Choice);
    }
}

public class GameClockTests
{
    private static readonly GameClock Clock = GameClock.Chicago();

    [Fact]
    public void Standard_case()
    {
        var t = Clock.For(new DateTime(2026, 11, 13, 13, 0, 0), new DateTime(2026, 11, 13, 14, 0, 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 19, 0, 0, TimeSpan.Zero), t.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 19, 0, 0, TimeSpan.Zero), t.CancellationCutoff);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero), t.NotFullAlertAt);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 1, 0, 0, TimeSpan.Zero), t.ReminderAt); // Thu 7pm CST
    }

    [Fact]
    public void Game_the_day_DST_ends_uses_local_clock_for_relative_times()
    {
        // DST ends Sunday 11/1/2026 at 2am. Game Sunday 9am CST (UTC-6).
        var t = Clock.For(new DateTime(2026, 11, 1, 9, 0, 0), new DateTime(2026, 11, 1, 10, 0, 0));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero), t.StartsAt);
        // Cutoff is Saturday 9am CDT (UTC-5): 25 real hours earlier, same wall-clock time.
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 14, 0, 0, TimeSpan.Zero), t.CancellationCutoff);
        // Reminder Saturday 7pm CDT.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), t.ReminderAt);
    }

    [Fact]
    public void Game_the_day_after_DST_starts()
    {
        // DST starts Sunday 3/8/2026. Game Monday 3/9 1pm CDT; reminder Sunday 7pm CDT; cutoff Sunday 1pm CDT.
        var t = Clock.For(new DateTime(2026, 3, 9, 13, 0, 0), new DateTime(2026, 3, 9, 14, 0, 0));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 18, 0, 0, TimeSpan.Zero), t.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 18, 0, 0, TimeSpan.Zero), t.CancellationCutoff);
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 0, 0, 0, TimeSpan.Zero), t.ReminderAt);
    }

    [Fact]
    public void Nonexistent_and_repeated_local_times()
    {
        // 2:30am on 3/8/2026 doesn't exist: treated as 3:30am CDT.
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 30, 0, TimeSpan.Zero), Clock.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0)));
        // 1:30am on 11/1/2026 happens twice: the first (CDT) one.
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), Clock.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0)));
    }
}

public class PlayerRulesTests
{
    [Theory]
    [InlineData("(312) 555-0123", "+13125550123")]
    [InlineData("312.555.0123", "+13125550123")]
    [InlineData("1-312-555-0123", "+13125550123")]
    [InlineData("+13125550123", "+13125550123")]
    public void Normalizes_us_numbers(string input, string expected) => Assert.Equal(expected, PhoneNumbers.NormalizeUs(input));

    [Theory]
    [InlineData("555-0123")]
    [InlineData("+44 20 7946 0958")]
    [InlineData("012-555-0123")]
    public void Rejects_bad_numbers(string input) => Assert.Throws<PlayerRuleException>(() => PhoneNumbers.NormalizeUs(input));

    [Theory]
    [InlineData("Mike R", "Mike R")]
    [InlineData("  Mike   R ", "Mike R")]
    [InlineData("Abcdefghijkl", "Abcdefghijkl")]
    public void Valid_names(string input, string expected) => Assert.Equal(expected, PlayerRules.ValidateName(input));

    [Theory]
    [InlineData("")]
    [InlineData("Abcdefghijklm")]
    [InlineData("Zoë 🎾")]
    public void Invalid_names(string input) => Assert.Throws<PlayerRuleException>(() => PlayerRules.ValidateName(input));

    [Fact]
    public void Consent_changes_are_idempotent()
    {
        var now = DateTimeOffset.UtcNow;
        var p = Player.Create(PlayerRules.Register(Guid.NewGuid(), "Mike", "3125550123", now));
        Assert.Equal(ConsentStatus.Pending, p.ConsentStatus);

        var optIn = PlayerRules.OptIn(p, "JOIN", null, now)!;
        p.Apply(optIn);
        Assert.Null(PlayerRules.OptIn(p, "JOIN", null, now));

        p.Apply(PlayerRules.OptOut(p, OptOutSource.StopKeyword, null, now)!);
        Assert.Equal(ConsentStatus.OptedOut, p.ConsentStatus);
        Assert.Null(PlayerRules.OptOut(p, OptOutSource.StopKeyword, null, now));
    }
}
