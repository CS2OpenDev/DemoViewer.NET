#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;

/// <summary>
///     The Dashboard's cards in board order, each mapped onto the layers it reads. Where the board splits one layer
///     over two cards (Economy into the pistol card and the economy card, Pace into round pace and time to contact,
///     Setups into the setups and the site distribution, Retakes into retake-vs-save and by site), the split is by
///     the layer's own section titles, so a card shows exactly the rows the Layers tab's inspector shows for them.
///     Where two layers repeat a section (Setups and Rotations both list rotations, By score and Timeouts both list
///     timeouts), the card that holds both keeps one.
/// </summary>
public static class ScoutingDashboardCards
{
    private const string AreasSection = "CTs per area · A · mid · B";
    private const string RotationsSection = "Rotations";
    private const string ContactSection = "Time to first contact, by buy";
    private const string ScoreTimeoutsSection = "Timeouts by the score";
    private static readonly string[] _bySiteSections = ["By site", "Retake grouping"];

    private static bool IsPistol(string section) => section.StartsWith("Pistol", StringComparison.Ordinal);

    // The pistol sections of one side: Economy lists both sides' loadouts whichever side is in view.
    private static Func<string, bool> PistolOf(ScoutingSide side) => section =>
        IsPistol(section) && !section.EndsWith(" · " + (side == ScoutingSide.Ct ? ScoutingSide.T : ScoutingSide.Ct).Label(),
            StringComparison.Ordinal);

    private static bool IsPistolRead(ScoutingRead read) =>
        read.StarText.StartsWith("read pistol", StringComparison.Ordinal);

    private static ScoutingDashboardCardViewModel Card(string key, string title, params ScoutingDashboardPart[] parts) =>
        new(key, (_, _) => title, parts);

    /// <summary>The CT side's cards.</summary>
    public static IReadOnlyList<ScoutingDashboardCardViewModel> Ct() =>
    [
        new("ct.setups", (_, _) => "Starting setups",
            [new ScoutingDashboardPart("setups", s => s is not (AreasSection or RotationsSection), r => r.StarText != "rotations")],
            "setups"),
        new("ct.retakes", (_, _) => "Retake vs save",
            [new ScoutingDashboardPart("retakes", s => !_bySiteSections.Contains(s))], "retakes"),
        new("ct.opening", (_, _) => "Opening death heatmap", [new ScoutingDashboardPart("opening")], "opening"),
        Card("ct.pistol", "Pistol · CT",
            new ScoutingDashboardPart("econ", PistolOf(ScoutingSide.Ct), IsPistolRead, Chart: false)),
        Card("ct.sites", "Plant and retake by site",
            new ScoutingDashboardPart("retakes", _bySiteSections.Contains, ScoutingDashboardPart.NoReads)),
        Card("ct.against", "Strats run against them", new ScoutingDashboardPart("against")),
        Card("ct.rotations", "Site distribution and rotations",
            new ScoutingDashboardPart("setups", s => s == AreasSection, r => r.StarText == "rotations"),
            new ScoutingDashboardPart("rotations")),
        Card("ct.util", "CT utility", new ScoutingDashboardPart("util"))
    ];

    /// <summary>The T side's cards. Round pace spans the grid, so it leads and leaves no hole above it.</summary>
    public static IReadOnlyList<ScoutingDashboardCardViewModel> T() =>
    [
        new("t.pace", (_, _) => "Round pace · when the bomb goes down",
            [new ScoutingDashboardPart("pace", s => s != ContactSection)], isWide: true),
        Card("t.pistol", "Pistol · T",
            new ScoutingDashboardPart("econ", PistolOf(ScoutingSide.T), IsPistolRead, Chart: false)),
        new("t.plants", (_, _) => "Site targeting and plants", [new ScoutingDashboardPart("plants")], "plants"),
        Card("t.contact", "Time to contact",
            new ScoutingDashboardPart("pace", s => s == ContactSection, ScoutingDashboardPart.NoReads, Chart: false)),
        Card("t.util", "Common utility", new ScoutingDashboardPart("util")),
        Card("t.strats", "Detected strats", new ScoutingDashboardPart("strats")),
        new("t.opening", (_, _) => "Opening kill / death heatmap", [new ScoutingDashboardPart("opening")], "opening"),
        Card("t.lurks", "Lurk", new ScoutingDashboardPart("lurks"))
    ];

    /// <summary>The AWP card, on either side.</summary>
    public static ScoutingDashboardCardViewModel Awp() =>
        new("awp", (side, map) => map.Length == 0 ? $"AWP · {side.Label()}" : $"AWP · {side.Label()} · {map}",
            [new ScoutingDashboardPart("awp")], "awp", isWide: true);

    /// <summary>The map-wide cards: economy, opening impact, and by score with the tactical timeouts.</summary>
    public static IReadOnlyList<ScoutingDashboardCardViewModel> MapWide() =>
    [
        new("econ", (_, map) => map.Length == 0 ? "Economy" : $"Economy · {map}",
            [new ScoutingDashboardPart("econ", s => !IsPistol(s), r => !IsPistolRead(r))], isWide: true),
        Card("impact", "Opening impact", new ScoutingDashboardPart("impact")),
        Card("score", "By score · tactical timeouts",
            new ScoutingDashboardPart("score", s => s != ScoreTimeoutsSection), new ScoutingDashboardPart("timeouts"))
    ];
}
