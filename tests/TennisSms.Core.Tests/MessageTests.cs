using TennisSms.Core.Games;
using TennisSms.Core.Messages;
using static TennisSms.Core.Tests.TestGames;

namespace TennisSms.Core.Tests;

public class MessageTests
{
    private const string WorstName = "Abcdefghijkl"; // 12 chars, the maximum
    private const string WorstLink = "https://tennissms.example.com/g/Ab3dE6gH9k"; // 42 chars

    /// <summary>A game with worst-case data: long names, minutes on both times, 2-digit dates.</summary>
    private static GameMessageContext WorstCase(GameFormat format, string location, int confirmed)
    {
        var start = new DateTime(2026, 12, 30, 10, 30, 0);
        var events = new List<object>
        {
            new GameScheduled(GameId, format, GameFormats.CapacityOf(format), start, start.AddHours(2), location),
        };
        for (var i = 1; i <= confirmed; i++) events.Add(Added(Id(i)));
        var game = GameEvolver.From(events);

        return new GameMessageContext
        {
            Game = game,
            Names = Enumerable.Range(1, 8).ToDictionary(Id, _ => WorstName),
            PlayerLink = _ => WorstLink,
            OrganizerLink = WorstLink,
            OrganizerName = WorstName,
            Brand = "TennisSMS",
            CancellationCutoffLocal = start.AddDays(-1),
        };
    }

    public static TheoryData<NoticeKind, GameFormat, int> AllNotices()
    {
        var data = new TheoryData<NoticeKind, GameFormat, int>();
        foreach (var kind in Enum.GetValues<NoticeKind>())
            foreach (var format in Enum.GetValues<GameFormat>())
                data.Add(kind, format, GameFormats.CapacityOf(format));
        return data;
    }

    [Theory]
    [MemberData(nameof(AllNotices))]
    public void Every_template_fits_one_segment_with_worst_case_data(NoticeKind kind, GameFormat format, int confirmed)
    {
        var ctx = WorstCase(format, location: "Lincoln Park District Tennis Courts #4", confirmed);
        var to = kind.ToString().StartsWith("Organizer") ? Recipient.Organizer : Recipient.Player(Id(1));

        var text = MessageComposer.Render(new Notice(kind, to, Id(2), WithinCancellationWindow: true), ctx);

        Assert.True(Gsm7.FitsOneSegment(text), $"{kind}: {Gsm7.SeptetCount(text)} septets: {text}");
    }

    [Fact]
    public void Long_or_fancy_locations_are_dropped_rather_than_overflowing()
    {
        var ctx = WorstCase(GameFormat.Doubles, new string('x', 120), confirmed: 4);
        var reminder = MessageComposer.Render(new Notice(NoticeKind.Reminder, Recipient.Player(Id(1))), ctx);
        Assert.DoesNotContain("xxx", reminder);
        Assert.True(Gsm7.FitsOneSegment(reminder));

        var emoji = WorstCase(GameFormat.Doubles, "Club 🎾", confirmed: 1);
        var invite = MessageComposer.Render(new Notice(NoticeKind.Invite, Recipient.Player(Id(5))), emoji);
        Assert.DoesNotContain("Club", invite);
    }

    [Fact]
    public void Invite_reads_like_the_spec()
    {
        var game = Doubles(Added(Phil), Added(Mike));
        var ctx = new GameMessageContext
        {
            Game = game,
            Names = new Dictionary<Guid, string> { [Phil] = "Phil", [Mike] = "Mike" },
            PlayerLink = _ => "{Link}",
            OrganizerLink = "{OrganizerLink}",
            OrganizerName = "Phil",
            Brand = "{Brand}",
            CancellationCutoffLocal = Start.AddDays(-1),
        };

        Assert.Equal("{Brand}: Doubles Fri 11/13 1-2pm at the club. 2 spots open. Reply YES or NO, or tap {Link}",
            MessageComposer.Render(new Notice(NoticeKind.Invite, Recipient.Player(Dave)), ctx));
        Assert.Equal("Fri 11/13 1-2pm still needs 2. Free cancellation ends Thu 1pm. {OrganizerLink}",
            MessageComposer.Render(new Notice(NoticeKind.OrganizerNotFullAlert, Recipient.Organizer), ctx));
        Assert.Equal("Mike dropped from Fri 11/13 1-2pm. 2 spots open. Inside 24-hr window. {OrganizerLink}",
            MessageComposer.Render(new Notice(NoticeKind.OrganizerPlayerDropped, Recipient.Organizer, Mike, true), ctx));
    }

    [Theory]
    [InlineData(13, 0, 14, 0, "Fri 11/13 1-2pm")]
    [InlineData(10, 30, 12, 0, "Fri 11/13 10:30am-12pm")]
    [InlineData(9, 0, 10, 30, "Fri 11/13 9-10:30am")]
    [InlineData(11, 0, 12, 30, "Fri 11/13 11am-12:30pm")]
    public void Time_ranges(int sh, int sm, int eh, int em, string expected) =>
        Assert.Equal(expected, TimeText.Range(new DateTime(2026, 11, 13, sh, sm, 0), new DateTime(2026, 11, 13, eh, em, 0)));

    [Fact]
    public void General_messages_fit_one_segment()
    {
        var three = new[] { new DateTime(2026, 12, 30, 10, 30, 0), new DateTime(2026, 12, 31, 12, 30, 0), new DateTime(2026, 12, 29, 9, 0, 0) };
        foreach (var text in new[]
                 {
                     GeneralMessages.JoinConfirmed("TennisSMS"), GeneralMessages.StopConfirmed("TennisSMS"),
                     GeneralMessages.Help("TennisSMS"), GeneralMessages.UnknownNumberJoin, GeneralMessages.Unrecognized,
                     GeneralMessages.NoOpenInvites, GeneralMessages.NoUpcomingGames, GeneralMessages.NotSignedUp,
                     GeneralMessages.WhichGame(three),
                 })
            Assert.True(Gsm7.FitsOneSegment(text), text);

        Assert.Equal("Which game? 1) Fri 11/13 1pm 2) Sun 11/15 9am. Reply 1 or 2, or use the links.",
            GeneralMessages.WhichGame([new DateTime(2026, 11, 13, 13, 0, 0), new DateTime(2026, 11, 15, 9, 0, 0)]));
    }

    [Fact]
    public void Gsm7_rules()
    {
        Assert.True(Gsm7.FitsOneSegment(new string('a', 160)));
        Assert.False(Gsm7.FitsOneSegment(new string('a', 161)));
        Assert.False(Gsm7.FitsOneSegment(new string('a', 159) + "€")); // extension char costs 2
        Assert.False(Gsm7.IsEncodable("You’re in")); // curly apostrophe
        Assert.False(Gsm7.IsEncodable("1–2pm")); // en dash
        Assert.Equal("You're in 1-2pm", Gsm7.Plain("You’re in 1–2pm"));
    }
}
