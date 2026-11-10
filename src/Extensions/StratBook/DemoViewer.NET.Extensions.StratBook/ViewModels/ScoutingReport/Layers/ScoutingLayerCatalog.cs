namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     Every Scouting Report layer, in chip order: the map layers, then the strip. A Phase 2 layer replaces its
///     file's class body; a new layer adds one line here.
/// </summary>
public static class ScoutingLayerCatalog
{
    /// <summary>A fresh instance of every layer.</summary>
    public static IReadOnlyList<IScoutingLayer> Create() =>
    [
        new SetupsLayer(),
        new AwpLayer(),
        new OpeningLayer(),
        new UtilityLayer(),
        new RetakesLayer(),
        new PlantsLayer(),
        new LurksLayer(),
        new StratsLayer(),
        new AgainstLayer(),
        new EconomyLayer(),
        new PaceLayer(),
        new RotationsLayer(),
        new ImpactLayer(),
        new ScoreLayer(),
        new TimeoutsLayer()
    ];
}
