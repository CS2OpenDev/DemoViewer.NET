namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     One team's Post-Plant And Retake: one block per map and side the team
///     played, the map with the most rounds first, T before CT. Built by <see cref="PostPlantService.Build" />.
/// </summary>
public sealed class PostPlantSet
{
    public IReadOnlyList<PostPlantBlock> Blocks { get; init; } = [];

    /// <summary>Demos of the team with Round Facts rows: the ones the blocks are built from.</summary>
    public int DemosRead { get; init; }

    /// <summary>Demos read with no current positions file; their rounds are left out of the position numbers.</summary>
    public int DemosWithoutPositions { get; init; }
}

/// <summary>
///     The team's rounds with a plant on one map and side: on T its own plants (where the bomb went down
///     and how the players held it), on CT the opponent's (how the retake grouped). Every number the
///     Scouting Report prints is a <see cref="TendencyBucket" />, so every number opens the rounds behind it.
///     The plant clusters and the holds are the T side's, the retake grouping the CT side's; the other
///     block leaves them empty.
/// </summary>
public sealed class PostPlantBlock
{
    public string Map { get; init; } = "";

    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; init; }

    /// <summary>Every live round of this map on this side: the denominator of <see cref="Planted" />.</summary>
    public TendencyBucket SideRounds { get; init; } = TendencyBucket.Empty("rounds");

    /// <summary>The rounds with a plant, clipped from shortly before the plant to the end.</summary>
    public TendencyBucket Planted { get; init; } = TendencyBucket.Empty("planted");

    /// <summary>Alive players at the plant, the team's count first ("4v3"), most rounds first.</summary>
    public IReadOnlyList<TendencyBucket> ManCount { get; init; } = [];

    /// <summary>How the planted rounds ended for the team: "won: exploded", "lost: defused" and so on.</summary>
    public IReadOnlyList<TendencyBucket> Outcomes { get; init; } = [];

    /// <summary>Planted rounds whose positions could be read: the denominator of the position lists.</summary>
    public int PositionRounds { get; init; }

    /// <summary>Plant spots clustered per site, most rounds first; a spot the positions do not give last. T side only.</summary>
    public IReadOnlyList<TendencyBucket> PlantClusters { get; init; } = [];

    /// <summary>The hold's shape a few seconds after the plant: players near the bomb and away from it. T side only.</summary>
    public IReadOnlyList<TendencyBucket> HoldShapes { get; init; } = [];

    /// <summary>The places the post-plant was held from, one count per round a player stood there. T side only.</summary>
    public IReadOnlyList<TendencyBucket> HoldPlaces { get; init; } = [];

    /// <summary>How the CTs alive at the plant reached the bomb: together, trickled, one player, nobody. CT side only.</summary>
    public IReadOnlyList<TendencyBucket> RetakeGroups { get; init; } = [];

    /// <summary>
    ///     Where each plant of the team's own went, one point per planted round whose positions gave the spot, with
    ///     whether the round was won and the T buy. T side only; the Scouting Report's Plants layer draws it.
    /// </summary>
    public IReadOnlyList<PlantSpotPoint> PlantSpots { get; init; } = [];

    /// <summary>The team's buy class per round of this side, keyed by demo path and round number; the buy filter reads it.</summary>
    public IReadOnlyDictionary<(string DemoPath, int RoundNumber), BuyType> RoundBuys { get; init; } =
        new Dictionary<(string, int), BuyType>();
}

/// <summary>One plant of the team's: the planter's spot, and how the round went.</summary>
/// <param name="Round">The planted round's clip, the same record the plant cluster holds.</param>
/// <param name="X">World X of the spot.</param>
/// <param name="Y">World Y of the spot.</param>
/// <param name="Z">World Z of the spot.</param>
/// <param name="Site">"A", "B" or "unknown site".</param>
/// <param name="Won">The team won the round.</param>
/// <param name="Buy">The team's buy that round.</param>
public sealed record PlantSpotPoint(TendencyRound Round, int X, int Y, int Z, string Site, bool Won, BuyType Buy);
