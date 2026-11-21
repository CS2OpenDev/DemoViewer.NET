#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A removal whose work throws is a failure the caller can see, not the "did not run" answer a drop gives.
/// </summary>
public class PackDataRemoverFailureTests
{
    [Test]
    public async Task ADeleteWhoseWorkThrows_FaultsInsteadOfAnsweringNotRun()
    {
        PackDataRemover remover = new(new DemoCacheStore(null), null, null, new SwallowingJobs());

        Task<ExtensionDataRemovalResult> delete = remover.DeleteAsync("pack", [], [], "owner", "title",
            () => throw new IOException("disk gone"));

        await Assert.That(async () => await delete).Throws<IOException>();
    }

    [Test]
    public async Task ADropBeforeTheWorkRan_StillAnswersNotRun()
    {
        PackDataRemover remover = new(new DemoCacheStore(null), null, null, new SwallowingJobs(false));

        ExtensionDataRemovalResult result = await remover.DeleteAsync("pack", [], [], "owner", "title");

        await Assert.That(result.Ran).IsFalse();
    }

    [Test]
    public async Task AFailedCount_IsNotReportedAsNothingToDelete()
    {
        ExtensionDataActionViewModel row = new(new ThrowingRemoval(), "Pack");

        await row.ArmCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(row.StatusText).IsNotEqualTo("Nothing to delete.");
            await Assert.That(row.StatusText).Contains("disk gone");
            await Assert.That(row.IsBusy).IsFalse();
            await Assert.That(row.IsConfirming).IsFalse();
        }
    }

    [Test]
    public async Task AFailedDelete_IsNotReportedAsTheExtensionTurnedBackOn()
    {
        ExtensionDataActionViewModel row = new(new ThrowingRemoval(), "Pack");

        await row.ConfirmCommand.ExecuteAsync(null);

        using (Assert.Multiple())
        {
            await Assert.That(row.StatusText).DoesNotContain("turned back on");
            await Assert.That(row.StatusText).Contains("disk gone");
            await Assert.That(row.IsBusy).IsFalse();
        }
    }

    private sealed class ThrowingRemoval : IExtensionDataRemoval
    {
        public string FeatureId => "pack";

        public Task<ExtensionDataInventory> InventoryAsync() => throw new IOException("disk gone");

        public Task<ExtensionDataRemovalResult> DeleteAsync() => throw new IOException("disk gone");
    }

    // The host's queue: a job that throws is recorded as failed and its task still completes.
    private sealed class SwallowingJobs(bool run = true) : IExtensionJobs
    {
        public IJobHandle Enqueue(JobRequest request) => throw new NotSupportedException();

        public async Task RunAsync(string title, Func<IJobContext, Task> work, JobOptions? options = null)
        {
            if (!run)
            {
                return;
            }

            try
            {
                await work(new Context()).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        public void CancelAll()
        {
        }

        private sealed class Context : IJobContext
        {
            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, int total, string? detail = null)
            {
            }
        }
    }
}
