#region

using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     Remove deletes a folder at once only for the id asked about and only when nothing of that id is mapped
///     in this process; every other marked folder waits for the next start.
/// </summary>
public class ExtensionUpdateRemovalTests
{
    private const string Id = FakeFeeds.Id;
    private const string Other = "net.demoviewer.pack.other";

    [Test]
    public async Task Remove_DeletesOnlyItsOwnFolder_AndLeavesAnotherMarkedLoadedId()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string loadedOther = Stage(extensions, Other, "1.0.0");
            ExtensionStaging.MarkForRemoval(extensions, Other);
            Stage(extensions, Id, "1.0.0");
            ExtensionUpdateService service = new(root, [InstalledStatus(Other, "1.0.0", loadedOther)], Host, TrustAll,
                new FakeFeedClient(), FeedUrl, catalogue: [Id, Other], externalRejected: [], shippedIds: []);

            bool removed = await service.RemoveInstalledAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(removed).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(extensions, Id))).IsFalse();
                await Assert.That(Directory.Exists(loadedOther)).IsTrue()
                    .Because("that id is loaded; its folder goes at the next start");
                await Assert.That(File.Exists(ExtensionStaging.RemovalMarkerPath(extensions, Other))).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A copy refused after its assembly loaded (here the reference check) is mapped for the life of the
    // process, so it is only marked; one refused before anything loaded goes at once.
    [Test]
    public async Task Remove_OfACopyRefusedAfterItLoaded_OnlyMarks_OneRefusedBeforeLoading_DeletesAtOnce()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string mapped = Stage(extensions, Id, "1.0.0");
            LoadOutcome afterLoad = new(mapped, FakeManifests.For(Id, "Fake", "1.0.0"), LoadFailure.ReferenceMismatch,
                "it was built against X 1.0; this app ships 2.0", External: true);
            bool marked = await Service(root, afterLoad).RemoveInstalledAsync(Id);
            bool stillThere = Directory.Exists(mapped);
            bool markerWritten = File.Exists(ExtensionStaging.RemovalMarkerPath(extensions, Id));

            Directory.Delete(Path.Combine(extensions, Id), true);
            string unsigned = Stage(extensions, Id, "1.0.0");
            LoadOutcome beforeLoad = afterLoad with { Failure = LoadFailure.ReservedId, Detail = "not signed" };
            bool deleted = await Service(root, beforeLoad).RemoveInstalledAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(marked).IsTrue();
                await Assert.That(stillThere).IsTrue();
                await Assert.That(markerWritten).IsTrue();
                await Assert.That(deleted).IsTrue();
                await Assert.That(Directory.Exists(unsigned)).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The sweep runs as a queue item after extensions loaded: a folder marked this session stays put.
    [Test]
    public async Task Cleanup_LeavesAMarkedFolderForTheNextStart()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string running = Stage(extensions, Id, "1.0.0");
            ExtensionStaging.MarkForRemoval(extensions, Id);

            new ExtensionUpdateService(root, [InstalledStatus(Id, "1.0.0", running)], Host, TrustAll,
                new FakeFeedClient(), FeedUrl).Cleanup();

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(running)).IsTrue();
                await Assert.That(File.Exists(ExtensionStaging.RemovalMarkerPath(extensions, Id))).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ExtensionUpdateService Service(string root, LoadOutcome rejected) =>
        new(root, [], Host, TrustAll, new FakeFeedClient(), FeedUrl, catalogue: [Id], externalRejected: [rejected],
            shippedIds: []);

    internal static string Stage(string extensions, string id, string version)
    {
        string dir = Path.Combine(extensions, id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionManifest.FileName), FakeFeeds.Manifest(id, new FakeFeeds.Entry(version)));
        File.WriteAllText(Path.Combine(dir, "Fake.dll"), "x");
        return dir;
    }

    internal static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvrm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
