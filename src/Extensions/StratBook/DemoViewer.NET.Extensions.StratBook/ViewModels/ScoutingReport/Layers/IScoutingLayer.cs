namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>The side the Scouting Report is showing. One side at a time: there is no CT and T compare.</summary>
public enum ScoutingSide
{
    T = 2,
    Ct = 3
}

/// <summary>Which sides a layer applies to.</summary>
[Flags]
public enum ScoutingSides
{
    Ct = 1,
    T = 2,
    Both = Ct | T
}

/// <summary>The buy filter. A layer that does not split by buy ignores it and files its stars under All.</summary>
public enum ScoutingBuy
{
    All,
    Eco,
    Semi,
    Force,
    Full
}

/// <summary>Where a layer lives: drawn on the map stage, or inspector-only in the strip under it.</summary>
public enum ScoutingLayerPlacement
{
    Map,
    Strip
}

/// <summary>
///     One Scouting Report layer: a thing the map can draw (setups, AWP routes, plants) or an inspector-only
///     strip (economy, pace). Each layer is its own file under <c>Layers/</c> and is registered once in
///     <see cref="ScoutingLayerCatalog.Create" />; the registry builds it for the scope in view and the long form
///     builds it for every map and side.
///     <para>
///         <b>Builds run off the UI thread.</b> <see cref="BuildAsync" /> awaits the team's shared loads on
///         <see cref="ScoutingLayerContext.Data" /> and returns plain records; it must not touch a view model. Heavy
///         work of the layer's own goes through <see cref="ScoutingLayerContext.Offload{T}" />. A build whose
///         token is cancelled may return anything: the registry drops it.
///     </para>
/// </summary>
public interface IScoutingLayer
{
    /// <summary>
    ///     The stable id: setups, awp, opening, util, retakes, plants, lurks, strats, against; econ, pace,
    ///     rotations, impact, score, timeouts. Star keys and the layer toggles persist it: never renamed.
    /// </summary>
    string Id { get; }

    ScoutingLayerPlacement Placement { get; }

    /// <summary>The sides the layer applies to; it is not offered on the other.</summary>
    ScoutingSides Sides { get; }

    /// <summary>True when the layer splits by buy; false files its stars under All whatever the filter says.</summary>
    bool UsesBuy { get; }

    /// <summary>What the map draws for the layer, for the chip and the legend.</summary>
    ScoutingSwatch Swatch { get; }

    /// <summary>
    ///     Where the layer's data stands against plan §2, for the "Build status" chip: E (exists), D (derivable),
    ///     D-rs (needs the extension's ruleset), R (needs App or Sdk work), K (needs engine work).
    /// </summary>
    string BuildCode { get; }

    /// <summary>The chip's name on a side; the CT opening layer is "Opening deaths".</summary>
    /// <param name="side">The side in view.</param>
    string NameFor(ScoutingSide side);

    /// <summary>The inspector's title on a side.</summary>
    /// <param name="side">The side in view.</param>
    string TitleFor(ScoutingSide side);

    /// <summary>Whether the layer draws when a team is first opened on a side.</summary>
    /// <param name="side">The side.</param>
    bool OnByDefault(ScoutingSide side);

    /// <summary>The layer's marks, reads, sections and legend for one scope.</summary>
    /// <param name="context">The team, map, side and buy, and the team's shared loads.</param>
    /// <param name="cancellationToken">Cancelled when the scope changes.</param>
    Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context, CancellationToken cancellationToken);
}

/// <summary>Whether a layer applies to a side.</summary>
public static class ScoutingLayerSides
{
    /// <summary>True when <paramref name="layer" /> is offered on <paramref name="side" />.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="side">The side.</param>
    public static bool AppliesTo(this IScoutingLayer layer, ScoutingSide side)
    {
        ArgumentNullException.ThrowIfNull(layer);
        return (layer.Sides & (side == ScoutingSide.Ct ? ScoutingSides.Ct : ScoutingSides.T)) != 0;
    }

    /// <summary>"CT" or "T", the word the keys and the scope line use.</summary>
    /// <param name="side">The side.</param>
    public static string Label(this ScoutingSide side) => side == ScoutingSide.Ct ? "CT" : "T";
}
