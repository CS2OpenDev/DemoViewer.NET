#region

using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The launch auto-stage is a call nobody asked for, so "Check for updates automatically" off stops it.</summary>
public class ExtensionUpdateLaunchGateTests
{
    [Test]
    public async Task AutoStageAtLaunch_AsksNothing_WhenAutomaticChecksAreOff()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvgate-" + Guid.NewGuid().ToString("N"));
        try
        {
            string installed = Path.Combine(root, "extensions", FakeFeeds.Id, "1.0.0");
            byte[] zip = ExtensionZip(FakeFeeds.Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new()
            {
                Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(FakeFeeds.Id)] = FakeFeeds.Json(entry) }
            };
            ExtensionUpdateService service = new(root, [InstalledStatus(FakeFeeds.Id, "1.0.0", installed)], Host,
                TrustAll, client, FeedUrl, installTrust: TrustAll);

            bool asked = await service.AutoStageAtLaunchAsync(autoUpdate: true, checkAutomatically: false,
                lastCheckUtc: null, now: DateTimeOffset.UtcNow);

            using (Assert.Multiple())
            {
                await Assert.That(asked).IsFalse();
                await Assert.That(client.FeedFetches).IsEqualTo(0);
                await Assert.That(client.Downloads).IsEqualTo(0);
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
