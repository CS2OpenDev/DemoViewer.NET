#region

using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The launch auto-stage reports a check only when a feed was asked; the caller records the time on it.</summary>
public class ExtensionUpdateLaunchRecordTests
{
    [Test]
    public async Task AutoStageAtLaunch_IsFalse_WhenTheQueueDropsTheItemUnrun()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvrec-" + Guid.NewGuid().ToString("N"));
        try
        {
            string installed = Path.Combine(root, "extensions", FakeFeeds.Id, "1.0.0");
            FakeFeedClient client = new()
            {
                Feeds = { [FeedUrl(FakeFeeds.Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0")) }
            };
            RecordingQueue dropping = new() { Drop = true };
            ExtensionUpdateService service = new(root, [InstalledStatus(FakeFeeds.Id, "1.0.0", installed)], Host,
                TrustAll, client, FeedUrl, dropping, installTrust: TrustAll);

            bool asked = await service.AutoStageAtLaunchAsync(true, true, null, DateTimeOffset.UtcNow);

            using (Assert.Multiple())
            {
                await Assert.That(dropping.Requests.Count).IsEqualTo(1);
                await Assert.That(client.FeedFetches).IsEqualTo(0);
                await Assert.That(asked).IsFalse().Because("no feed was asked, so the record must not move");
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
