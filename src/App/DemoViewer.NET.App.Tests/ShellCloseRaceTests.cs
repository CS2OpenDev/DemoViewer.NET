#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Shell;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A Close Demo still draining the old demo's holds must not finish over a demo opened meanwhile.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class ShellCloseRaceTests
{
    [Test]
    public async Task ACloseThatFinishesAfterANewerOpen_LeavesTheNewDemosStatusAlone()
    {
        string demo = DemoTestHelper.RequireDemo();

        await HeadlessSession.RunOnUi(async () =>
        {
            using HeavyJobGate gate = new();
            using DemoProcessingQueue queue = new(gate, compactHeap: () => Task.CompletedTask);
            MainViewModel vm = new(library: TestLibraries.Empty(), heavyJobGate: gate, processingQueue: queue);

            await vm.LoadDemoFromPathAsync(demo);
            await Assert.That(vm.HasFile).IsTrue();

            // A queue pass still reading the old parse: the close waits for it to let go.
            IHeldParse? hold = queue.ShellDemo!.TryHold(demo);
            await Assert.That(hold).IsNotNull();

            Task close = vm.CloseDemoCommand.ExecuteAsync(null);
            await Assert.That(close.IsCompleted).IsFalse();

            await vm.LoadDemoFromPathAsync(demo);
            string afterOpen = vm.StatusText;

            hold!.Dispose();
            await close.WaitAsync(TimeSpan.FromSeconds(60));

            using (Assert.Multiple())
            {
                await Assert.That(vm.HasFile).IsTrue();
                await Assert.That(vm.StatusText).IsEqualTo(afterOpen)
                    .Because("the close belongs to the old demo; the newer one is loaded and says so");
            }
        });
    }
}
