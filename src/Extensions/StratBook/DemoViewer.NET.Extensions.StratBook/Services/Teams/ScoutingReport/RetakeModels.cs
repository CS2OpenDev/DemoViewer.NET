#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>What the CTs did with one of the team's CT rounds once the opponents planted.</summary>
public enum RetakeKind
{
    /// <summary>The opponents did not plant.</summary>
    NoPlant,

    /// <summary>The positions do not hold the round or the plant spot: counted, never read as a retake or a save.</summary>
    Unread,

    /// <summary>No CT was alive at the plant.</summary>
    NoCtAlive,

    /// <summary>The CTs went for the bomb: someone reached it, the round was won, or nobody saved (<see cref="RetakeRules" />).</summary>
    Retook,

    /// <summary>The round was lost, no CT reached the bomb and at least one saved.</summary>
    Saved
}

/// <summary>
///     One of the team's CT rounds as the Retakes and saves layer reads it. Built by
///     <see cref="RetakeService.Build" />; the layer filters by map and buy and groups the rest.
/// </summary>
/// <param name="Map">The map.</param>
/// <param name="Buy">The team's own (CT) buy that round.</param>
/// <param name="Site">Where the bomb went down.</param>
/// <param name="CtAlive">CTs alive at the plant, from Round Facts.</param>
/// <param name="TAlive">Ts alive at the plant, from Round Facts.</param>
/// <param name="Kind">What the CTs did.</param>
/// <param name="Won">The CTs won the round.</param>
/// <param name="Outcome">How it ended for the CTs: <see cref="PostPlantService.OutcomeLabel" />.</param>
/// <param name="Grouping">The retake grouping (<see cref="PostPlantService.RetakeGroup" />); null unless read.</param>
/// <param name="Reached">Each CT that reached the bomb, where it first did.</param>
/// <param name="Saves">Each CT that saved, where it last stood before the end.</param>
/// <param name="Clip">The round's clip: from shortly before the plant to the end (the whole round with no plant).</param>
public sealed record RetakeRound(
    string Map,
    BuyType Buy,
    BombSite Site,
    int CtAlive,
    int TAlive,
    RetakeKind Kind,
    bool Won,
    string Outcome,
    string? Grouping,
    IReadOnlyList<RoundPosition> Reached,
    IReadOnlyList<RoundPosition> Saves,
    TendencyRound Clip)
{
    /// <summary>The man count at the plant, the CTs first: "4v3".</summary>
    public string ManCount => $"{CtAlive}v{TAlive}";

    /// <summary>The opponents planted.</summary>
    public bool Planted => Kind != RetakeKind.NoPlant;
}

/// <summary>Every CT round of one team with the retake read, and how many demos lacked positions.</summary>
public sealed class RetakeSet
{
    public IReadOnlyList<RetakeRound> Rounds { get; init; } = [];

    /// <summary>Demos of the team with Round Facts rows.</summary>
    public int DemosRead { get; init; }

    /// <summary>Demos read with no positions file; their planted rounds read as <see cref="RetakeKind.Unread" />.</summary>
    public int DemosWithoutPositions { get; init; }
}
