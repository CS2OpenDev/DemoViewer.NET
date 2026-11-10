#region

using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.ExtensionUpdateRemovalTests;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>Only a user's Install or Update undoes a Remove; the launch auto-stage leaves a marked id alone.</summary>
public class ExtensionUpdateAutoStageRemovalTests
{
    private const string Id = FakeFeeds.Id;

    [Test]
    public async Task AutoStage_SkipsAMarkedId_AndAsksNothingForIt()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string running = Stage(extensions, Id, "1.0.0");
            ExtensionStaging.MarkForRemoval(extensions, Id);
            FakeFeedClient client = Published("1.0.1");
            ExtensionUpdateService service = new(root, [InstalledStatus(Id, "1.0.0", running)], Host, TrustAll,
                client, FeedUrl, installTrust: TrustAll);

            await service.AutoStageAsync();

            using (Assert.Multiple())
            {
                await Assert.That(client.FeedFetches).IsEqualTo(0);
                await Assert.That(File.Exists(ExtensionStaging.RemovalMarkerPath(extensions, Id))).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(extensions, Id, "1.0.1"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Remove pressed while the auto-stage is mid-download: the stage gives way and the removal stands.
    [Test]
    public async Task Remove_DuringAnAutoStageDownload_Stands()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string running = Stage(extensions, Id, "1.0.0");
            FakeFeedClient client = Published("1.0.1", gated: true);
            ExtensionUpdateService service = new(root, [InstalledStatus(Id, "1.0.0", running)], Host, TrustAll,
                client, FeedUrl, installTrust: TrustAll);

            Task<IReadOnlyList<ExtensionUpdateState>> staging = service.AutoStageAsync();
            await client.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            bool removed = await service.RemoveInstalledAsync(Id);
            client.HalfwayGate!.SetResult();
            await staging.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            using (Assert.Multiple())
            {
                await Assert.That(removed).IsTrue();
                await Assert.That(File.Exists(ExtensionStaging.RemovalMarkerPath(extensions, Id))).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(extensions, Id, "1.0.1"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static FakeFeedClient Published(string version, bool gated = false)
    {
        byte[] zip = ExtensionZip(Id, version);
        FakeFeeds.Entry entry = new(version, zip);
        return new FakeFeedClient
        {
            HalfwayGate = gated ? new TaskCompletionSource() : null,
            Zips = { [entry.Url] = zip },
            Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) }
        };
    }
}
