namespace TennisSms.Web.Simulator;

public enum SimPhoneKind { Player, Organizer, Unknown }

/// <summary>One fake phone on the simulator page.</summary>
public sealed record SimPhone(string Number, string Name, SimPhoneKind Kind);

/// <summary>How a fake phone answers texts on its own (spec §11.3).</summary>
public enum AutoReplyMode { Manual, AlwaysYes, AlwaysNo, YesThenOut, Random, Silent }

public static class SimulatorPhones
{
    /// <summary>The 7 seeded players. Numbers are in the 555-01xx fictional range.</summary>
    public static readonly IReadOnlyList<SimPhone> Players =
    [
        new("+15550100011", "Kayla", SimPhoneKind.Player),
        new("+15550100012", "Jeremy", SimPhoneKind.Player),
        new("+15550100013", "George", SimPhoneKind.Player),
        new("+15550100014", "Mike", SimPhoneKind.Player),
        new("+15550100015", "Kevin", SimPhoneKind.Player),
        new("+15550100016", "Steve", SimPhoneKind.Player),
        new("+15550100017", "DJ", SimPhoneKind.Player),
    ];

    public static readonly SimPhone Unknown = new("+15550100099", "Unknown number", SimPhoneKind.Unknown);

    /// <summary>Organizer first (top left on the page), then the players, then the unknown number.</summary>
    public static IReadOnlyList<SimPhone> All(string organizerPhone, string organizerName) =>
        [new SimPhone(organizerPhone, $"{organizerName} (organizer)", SimPhoneKind.Organizer), .. Players, Unknown];

    public static SimPhone Player(string name) => Players.Single(p => p.Name == name);

    public static string Label(this AutoReplyMode mode) => mode switch
    {
        AutoReplyMode.Manual => "Manual",
        AutoReplyMode.AlwaysYes => "Always YES",
        AutoReplyMode.AlwaysNo => "Always NO",
        AutoReplyMode.YesThenOut => "YES then OUT",
        AutoReplyMode.Random => "Random",
        AutoReplyMode.Silent => "Silent",
        _ => mode.ToString(),
    };
}
