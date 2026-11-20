#region

using CS2DemoKit.Parser;
using DemoViewer.NET.ViewModels;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A run that a newer run or a reset has cancelled must not publish. The cancel only reaches the evaluation
///     while it is running; one that lands after the evaluation returned used to go unnoticed, and the stale run
///     went on to draw its graph and raise <see cref="AnalysisViewModel.EvaluationCompleted" />, which the shell
///     files under whatever demo is loaded by then.
/// </summary>
[NotInParallel]
public class AnalysisRunSupersedeTests
{
    [Test]
    public async Task ARunCancelledAfterItsEvaluation_DoesNotPublish() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            AnalysisViewModel vm = new();
            List<ParsedDemo> published = [];
            vm.EvaluationCompleted += (_, demo) => published.Add(demo);
            try
            {
                // Control: the same synthetic demo, uncancelled, does publish, so the assertion below is about
                // the cancel and not about a demo too empty to reach the event.
                ParsedDemo control = SyntheticParsedDemo.Create(mapName: "de_control");
                await vm.RunAsync(control);

                ParsedDemo stale = SyntheticParsedDemo.Create(mapName: "de_stale");
                TaskCompletionSource evaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                vm.AfterEvaluationForTests = () =>
                {
                    evaluated.TrySetResult();
                    return release.Task;
                };

                Task run = vm.RunAsync(stale);
                await evaluated.Task;
                vm.AfterEvaluationForTests = null;

                // What closing the demo, or opening the next one, does to the run in flight.
                vm.Reset();
                release.SetResult();
                await run;

                using (Assert.Multiple())
                {
                    await Assert.That(published).Contains(control);
                    await Assert.That(published).DoesNotContain(stale)
                        .Because("a reset after the evaluation returned must still stop the run publishing");
                }
            }
            finally
            {
                vm.Dispose();
            }
        });
}
