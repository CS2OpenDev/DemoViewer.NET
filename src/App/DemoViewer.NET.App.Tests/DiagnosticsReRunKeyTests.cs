#region

using CS2DemoKit.Parser;
using DemoViewer.NET.Debugging;
using DemoViewer.NET.ViewModels;
using DemoViewer.NET.ViewModels.Analysis;
using DemoViewer.NET.ViewModels.Diagnostics;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Diagnostics tab's "Re-run with capture" re-runs the analysis of the demo already open. It
///     used to call the run without the demo's key, which is the in-memory-only mode: the re-run
///     cleared the user's graph breakpoints and stopped saving new ones for the rest of the session.
/// </summary>
[NotInParallel]
public class DiagnosticsReRunKeyTests
{
    [Test]
    public async Task ReRunWithCapture_KeepsTheDemosBreakpoints() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            bool profiling = Profiling.Enabled;
            AnalysisTabViewModel tab = new();
            AnalysisViewModel vm = tab.Analysis;
            using DiagnosticsTabViewModel diagnostics =
                new(tab, () => null, () => null, new DiagnosticsTelemetryHub(uiPost: a => a()));
            try
            {
                ParsedDemo demo = SyntheticParsedDemo.Create(mapName: "de_rerun");
                await vm.RunAsync(demo, "r2-rerun-" + Guid.NewGuid().ToString("N"));
                vm.GraphBreakpoints.Add(new GraphBreakpoint { TargetKind = GraphBreakpointTarget.Node, NodeKey = "n" });

                await diagnostics.ReRunCapturedCommand.ExecuteAsync(null);

                await Assert.That(vm.GraphBreakpoints.Breakpoints.Count).IsEqualTo(1)
                    .Because("the re-run must restore the demo's persisted breakpoints, not run keyless");
            }
            finally
            {
                Profiling.Enabled = profiling;
                vm.Dispose();
            }
        });
}
