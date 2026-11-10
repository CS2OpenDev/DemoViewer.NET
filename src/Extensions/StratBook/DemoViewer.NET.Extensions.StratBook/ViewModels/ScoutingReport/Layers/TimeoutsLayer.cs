namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Tactical timeouts (strip): taken, the round after, the usual call. Nothing in a demo surfaces them yet:
///     needs engine support (K3).
/// </summary>
public sealed class TimeoutsLayer() : ScoutingLayerBase("timeouts", "Timeouts", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "K")
{
    /// <inheritdoc />
    public override string TitleFor(ScoutingSide side) => "Tactical timeouts";

    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("K3",
            "Tactical timeouts need engine support (K3): no demo event surfaces them yet."));
}
