#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.ExtensionUpdateRemovalTests;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The launch deletes marked folders before any logger exists, so it reports them on its result for the
///     app to log once logging is up.
/// </summary>
public class ExtensionStartupRemovalTests
{
    [Test]
    public async Task Resolve_ReportsTheFoldersItRemoved()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            Stage(extensions, FakeFeeds.Id, "1.0.0");
            Stage(extensions, "net.demoviewer.pack.kept", "1.0.0");
            ExtensionStaging.MarkForRemoval(extensions, FakeFeeds.Id);

            ExtensionStartupResult result = ExtensionStartup.Resolve(root, [], ExtensionHost.Current,
                TrustPolicy.Nothing, [], allowUnverified: false, safeMode: true);

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(Path.Combine(extensions, FakeFeeds.Id))).IsFalse();
                await Assert.That(result.RemovedAtStart.Select(p => Path.GetFileName(p)!))
                    .IsEquivalentTo([FakeFeeds.Id]);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }
}
