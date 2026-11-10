namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>
///     One team's opening duels: one block per map and side the team played. Built by
///     <see cref="OpeningDuelsService.Build" />.
/// </summary>
public sealed class OpeningDuelSet
{
    public IReadOnlyList<OpeningDuelBlock> Blocks { get; init; } = [];

    /// <summary>Demos of the team with Round Facts rows: the ones the blocks are built from.</summary>
    public int DemosRead { get; init; }

    /// <summary>Demos read with no positions file; their duels count but draw no mark.</summary>
    public int DemosWithoutPositions { get; init; }

    /// <summary>The block of a map and side, or null.</summary>
    /// <param name="map">The map.</param>
    /// <param name="side">2 = T, 3 = CT.</param>
    public OpeningDuelBlock? For(string map, int side) =>
        Blocks.FirstOrDefault(b => string.Equals(b.Map, map, StringComparison.OrdinalIgnoreCase) && b.Side == side);
}

/// <summary>The team's opening duels and first deaths on one map and side.</summary>
public sealed class OpeningDuelBlock
{
    public string Map { get; init; } = "";

    /// <summary>2 = T, 3 = CT.</summary>
    public int Side { get; init; }

    /// <summary>Every live round of this map on this side, clipped from freeze end.</summary>
    public TendencyBucket Rounds { get; init; } = TendencyBucket.Empty("rounds");

    /// <summary>Rounds whose positions file could be read: the ones that can draw a mark.</summary>
    public int PositionRounds { get; init; }

    /// <summary>The round's opening duel, one per round that had one, in round order.</summary>
    public IReadOnlyList<OpeningDuelRound> Duels { get; init; } = [];

    /// <summary>The team's first death to an enemy, one per round that had one, in round order.</summary>
    public IReadOnlyList<OpeningDeathRound> FirstDeaths { get; init; } = [];
}

/// <summary>Where a player stood at a moment: the positions sample at or just before it.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Place">The place name, or null when the sample named none.</param>
public sealed record OpeningSpot(int X, int Y, int Z, string? Place);

/// <summary>A round's opening duel from the team's side.</summary>
/// <param name="Clip">The clip around the kill.</param>
/// <param name="Rest">The clip from the kill to the round's end, for "round won after".</param>
/// <param name="PlayerKey">The team's player in the duel: SteamID when the roster has one, else the name.</param>
/// <param name="Player">That player's name.</param>
/// <param name="Won">The team's player got the kill.</param>
/// <param name="Traded">Lost duels only: the killer died to a teammate within the trade window.</param>
/// <param name="RoundWon">The team won the round.</param>
/// <param name="Spot">The team's player at the kill (killer or victim); null without positions.</param>
public sealed record OpeningDuelRound(
    TendencyRound Clip,
    TendencyRound Rest,
    string PlayerKey,
    string Player,
    bool Won,
    bool Traded,
    bool RoundWon,
    OpeningSpot? Spot);

/// <summary>The team's first death to an enemy in a round.</summary>
/// <param name="Clip">The clip around the death.</param>
/// <param name="Player">Who died.</param>
/// <param name="IsOpening">It was the round's opening kill.</param>
/// <param name="Traded">The killer died to a teammate within the trade window.</param>
/// <param name="Spot">Where the player died; null without positions.</param>
public sealed record OpeningDeathRound(
    TendencyRound Clip,
    string Player,
    bool IsOpening,
    bool Traded,
    OpeningSpot? Spot);
