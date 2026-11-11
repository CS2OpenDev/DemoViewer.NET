#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.ViewModels.Settings;
using static DemoViewer.NET.AppTests.Extensions.ExtensionUpdateRemovalTests;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     An installed first-party copy that stopped loading is updated in place when the feed offers a newer
///     version, under the signed-only install policy; and once its folder is gone the catalogue stops saying
///     it did not load.
/// </summary>
public class ExtensionUpdateCatalogueRefreshTests
{
    private const string Id = FakeFeeds.Id;

    [Test]
    public async Task ARefusedCopy_IsOfferedAnUpdate_StagedByTheInstallPolicy()
    {
        string root = NewRoot();
        try
        {
            (ExtensionUpdateService service, FakeFeedClient _, RecordingTrust shipped, RecordingTrust install) =
                Setup(root, LoadFailure.ReferenceMismatch, out _);
            InstallableExtensionRow row = new((await service.CheckCatalogueAsync()).Single(), service);
            bool offered = row.CanInstall;
            string label = row.InstallLabel;
            bool removeToo = row.CanRemove;
            await row.InstallCommand.ExecuteAsync(null);

            using (Assert.Multiple())
            {
                await Assert.That(offered).IsTrue();
                await Assert.That(label).IsEqualTo("Update");
                await Assert.That(removeToo).IsTrue();
                await Assert.That(shipped.Asked).IsEmpty();
                await Assert.That(install.Asked.Count).IsEqualTo(1);
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", Id, "1.0.1"))).IsTrue();
                await Assert.That(row.Message).IsEqualTo("1.0.1 installed, restart to use it.");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Nothing of an incompatible copy was loaded, so Remove deletes it now; the row and every later check
    // then offer Install, not the launch's stale "not loaded".
    [Test]
    public async Task AfterRemoveDeletesTheFolder_TheCatalogueOffersInstall_NotTheLaunchSnapshot()
    {
        string root = NewRoot();
        try
        {
            (ExtensionUpdateService service, FakeFeedClient _, RecordingTrust _, RecordingTrust _) =
                Setup(root, LoadFailure.Incompatible, out string onDisk);
            InstallableExtensionRow row = new((await service.CheckCatalogueAsync()).Single(), service);
            await row.RemoveCommand.ExecuteAsync(null);
            InstallableStatus rowStatus = row.Entry.Status;
            InstallableExtension current = service.CurrentCatalogue().Single();
            InstallableExtension rechecked = (await service.CheckCatalogueAsync()).Single();

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(onDisk)).IsFalse();
                await Assert.That(rowStatus).IsEqualTo(InstallableStatus.Available);
                await Assert.That(row.CanInstall).IsTrue();
                await Assert.That(row.InstallLabel).IsEqualTo("Install");
                await Assert.That(current.Status).IsEqualTo(InstallableStatus.Available);
                await Assert.That(rechecked.Status).IsEqualTo(InstallableStatus.Available);
                await Assert.That(rechecked.Rejected).IsNull();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static (ExtensionUpdateService, FakeFeedClient, RecordingTrust, RecordingTrust) Setup(string root,
        LoadFailure failure, out string onDisk)
    {
        string extensions = Path.Combine(root, "extensions");
        onDisk = Stage(extensions, Id, "1.0.0");
        if (failure == LoadFailure.Incompatible)
        {
            File.WriteAllText(Path.Combine(onDisk, ExtensionManifest.FileName),
                FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.0", RequiresHost: "^3.0")));
        }

        LoadOutcome refused = new(onDisk, FakeManifests.For(Id, "Fake", "1.0.0"), failure, "refused", External: true);
        byte[] zip = ExtensionZip(Id, "1.0.1");
        FakeFeeds.Entry entry = new("1.0.1", zip);
        FakeFeedClient client = new()
        {
            Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) }
        };
        RecordingTrust shipped = new(true);
        RecordingTrust install = new(true);
        ExtensionUpdateService service = new(root, [], Host, shipped, client, FeedUrl, installTrust: install,
            catalogue: [Id], externalRejected: [refused], shippedIds: []);
        return (service, client, shipped, install);
    }
}
