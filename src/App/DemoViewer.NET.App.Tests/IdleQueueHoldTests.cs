#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Idle mode with background processing parked: idle's park is its own, apart from the user's Pause in the
///     status strip, and it ends on Resume whatever the setting reads by then.
/// </summary>
[NotInParallel]
public class IdleQueueHoldTests
{
    private static async Task WithShell(Func<MainViewModel, DemoProcessingQueue, SettingsService, Task> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvidlehold_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SettingsService settings = new(dir);
            settings.Write(s => s.Idle.KeepBackgroundProcessing = false);
            ServiceCollection services = new();
            services.Configure<AppSettings>(settings.Configuration);
            await using ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();

            await HeadlessSession.RunOnUi(async () =>
            {
                using HeavyJobGate gate = new();
                using DemoProcessingQueue queue = new(gate, a => a(), compactHeap: () => Task.CompletedTask);
                MainViewModel vm = new(library: TestLibraries.Empty(), settings: monitor, heavyJobGate: gate,
                    processingQueue: queue);
                await body(vm, queue, settings);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    private static async Task<bool> BackgroundWorkRuns(DemoProcessingQueue queue)
    {
        TaskCompletionSource ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.SubmitJob(new QueueJobRequest(QueueJobKind.DataTransfer, "Later", "test",
            DemoJobPriority.Background, _ =>
            {
                ran.TrySetResult();
                return Task.CompletedTask;
            }));
        return await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5))) == ran.Task;
    }

    [Test]
    public async Task LeavingIdle_DoesNotLiftTheUsersOwnPause() =>
        await WithShell(async (vm, queue, _) =>
        {
            queue.Pause();
            await vm.EnterIdleModeAsync();
            await vm.ResumeFromIdleAsync();

            await Assert.That(queue.IsPaused).IsTrue().Because("the user paused the queue before idle did anything");
        });

    [Test]
    public async Task LeavingIdle_EndsIdlesPark_AfterTheSettingWasTurnedOnFromTheOverlay() =>
        await WithShell(async (vm, queue, settings) =>
        {
            await vm.EnterIdleModeAsync();
            settings.Write(s => s.Idle.KeepBackgroundProcessing = true);
            await vm.ResumeFromIdleAsync();

            await Assert.That(await BackgroundWorkRuns(queue)).IsTrue()
                .Because("idle parked the queue, so leaving idle has to release it");
        });
}
