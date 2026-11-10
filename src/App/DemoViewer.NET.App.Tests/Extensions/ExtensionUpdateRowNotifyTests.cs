#region

using System.ComponentModel;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.ViewModels.Settings;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>The update line's computed members reach the view through change notifications, not only the getter.</summary>
public class ExtensionUpdateRowNotifyTests
{
    [Test]
    public async Task CanRemove_IsRaised_WhenACheckOrADownloadStartsAndEnds()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvrow-" + Guid.NewGuid().ToString("N"));
        ExtensionUpdateService service = new(root, [], Host, TrustAll, new FakeFeedClient(), FeedUrl);
        ExtensionUpdateRow row = new(FakeFeeds.Id, service, null,
            new PackSource.Installed(Path.Combine(root, "extensions", FakeFeeds.Id, "1.0.0")));
        List<bool> seen = [];
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExtensionUpdateRow.CanRemove))
            {
                seen.Add(row.CanRemove);
            }
        };

        row.IsChecking = true;
        row.IsChecking = false;
        row.IsDownloading = true;
        row.IsDownloading = false;

        await Assert.That(seen).IsEquivalentTo([false, true, false, true], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
