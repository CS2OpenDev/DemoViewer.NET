namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     AWP routes: where the AWP starts, its trail to 0:15, 0:30 or first contact, who holds it, two AWPs, and
///     whether it is saved on a loss. A placeholder until the positions carry the weapon column (P3.1, then P3.2);
///     "bought" and "saved" stay approximate until the engine has inventory (K1).
/// </summary>
public sealed class AwpLayer() : ScoutingLayerBase("awp", "AWP routes", ScoutingLayerPlacement.Map,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Ring, ScoutingColor.Neutral), "D-rs then K")
{
    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "AWP";

    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("P3.1",
            "AWP routes need the weapon column in the positions files (P3.1). AWP bought and saved also need engine inventory support (K1)."));
}
