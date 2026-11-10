#region

using DemoViewer.NET.Extensions.Updates;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>A stage queued behind another observes its own cancellation while it waits.</summary>
public class ExtensionUpdateStageCancelTests
{
    [Test]
    public async Task AStageWaitingForAnother_IsCancelledAtOnce_AndTheNextStillWaitsForTheFirst()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvcancel-" + Guid.NewGuid().ToString("N"));
        try
        {
            FakeFeedClient client = new() { HalfwayGate = new TaskCompletionSource() };
            ExtensionFeedEntry[] entries = new ExtensionFeedEntry[3];
            for (int i = 0; i < 3; i++)
            {
                string version = $"1.0.{i + 1}";
                byte[] zip = ExtensionZip(FakeFeeds.Id, version);
                FakeFeeds.Entry entry = new(version, zip);
                client.Zips[entry.Url] = zip;
                entries[i] = ExtensionFeed.Parse(FakeFeeds.Json(entry)).Entries.Single();
            }

            ExtensionUpdateService service = new(root, [Status()], Host, TrustAll, client, FeedUrl,
                installTrust: TrustAll);
            using CancellationTokenSource cancel = new();

            Task<StageResult> first = service.DownloadAndStageAsync(entries[0]);
            await client.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Task<StageResult> second = service.DownloadAndStageAsync(entries[1], ct: cancel.Token);
            await Task.Delay(50);
            await cancel.CancelAsync();
            bool secondEndedWhileTheFirstRan = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5))) == second;
            Task<StageResult> third = service.DownloadAndStageAsync(entries[2]);
            await Task.Delay(100);
            bool thirdWaited = !third.IsCompleted;
            bool firstStillRunning = !first.IsCompleted;
            client.HalfwayGate.SetResult();
            StageResult[] results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(secondEndedWhileTheFirstRan).IsTrue();
                await Assert.That(firstStillRunning).IsTrue();
                await Assert.That(results[1].Outcome).IsEqualTo(StageOutcome.Cancelled);
                await Assert.That(thirdWaited).IsTrue().Because("stages of the service still run one at a time");
                await Assert.That(results[0].Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(results[2].Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(File.Exists(Path.Combine(root, "extensions", FakeFeeds.Id, "1.0.1", "Fake.dll"))).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", FakeFeeds.Id, "1.0.2"))).IsFalse();
                await Assert.That(client.Downloads).IsEqualTo(2);
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
