#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>Drives the Scouting Report tab in a test: lets its loads land, picks a scope, reads the inspector.</summary>
internal static class ScoutingReportTestKit
{
    /// <summary>Waits for the team's loads and the stage's builds, running what they post, until nothing is left.</summary>
    /// <param name="vm">The tab.</param>
    /// <param name="posted">The post the tab was given.</param>
    public static async Task Settle(ScoutingReportTabViewModel vm, QueuedPost posted)
    {
        for (int i = 0; i < 4; i++)
        {
            await vm.LoadTask;
            await vm.Layers.Idle;
            posted.Drain();
        }
    }

    /// <summary>Shows a map and side, inspects a layer, and returns the inspector's rows (reads left out).</summary>
    /// <param name="vm">The tab.</param>
    /// <param name="posted">The post the tab was given.</param>
    /// <param name="map">The map.</param>
    /// <param name="side">The side.</param>
    /// <param name="layer">The layer id.</param>
    public static async Task<IReadOnlyList<ScoutingInspectorRowViewModel>> Inspect(ScoutingReportTabViewModel vm,
        QueuedPost posted, string map, ScoutingSide side, string layer)
    {
        vm.SelectMapCommand.Execute(vm.Maps.Single(m => m.Map == map));
        vm.Side = side;
        await Settle(vm, posted);
        vm.Layers.Focus(vm.Layers.Find(layer)!);
        return [.. vm.Layers.Inspector.Sections.SelectMany(s => s.Rows)];
    }

    /// <summary>The inspector row whose Review title is <paramref name="title" />.</summary>
    /// <param name="rows">The rows.</param>
    /// <param name="title">"de_nuke T · plant A".</param>
    public static ScoutingInspectorRowViewModel Titled(this IEnumerable<ScoutingInspectorRowViewModel> rows,
        string title) => rows.Single(r => r.Row?.OpenTitle == title);

    /// <summary>Every row with a number opens exactly its rounds, and a row with none opens nothing.</summary>
    /// <param name="rows">The rows.</param>
    public static bool EveryNumberOpensItsRounds(this IEnumerable<ScoutingInspectorRowViewModel> rows) =>
        rows.All(r => r.CanOpen == r.Row?.Rounds.Count > 0
                      && (!r.CanOpen || r.Row!.OpenTitle.Length > 0));
}
