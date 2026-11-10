namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Opening impact (strip): round win % after losing and after winning the opening duel, and opening deaths
///     traded. A placeholder until the strip build (P3.5) reads it from Round Facts kills.
/// </summary>
public sealed class ImpactLayer() : ScoutingLayerBase("impact", "Opening impact", ScoutingLayerPlacement.Strip,
    ScoutingSides.Both, new ScoutingSwatch(ScoutingMarkShape.Dot, ScoutingColor.Neutral), "D")
{
    /// <inheritdoc />
    public override Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ScoutingLayerResult.Placeholder("P3.5",
            "Round win % by opening duel comes with the strip build (P3.5)."));
}
