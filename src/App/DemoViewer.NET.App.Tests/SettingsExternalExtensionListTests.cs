#region

using DemoViewer.NET.AppTests.Extensions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.ViewModels.Settings;
using Microsoft.Extensions.DependencyInjection;
using static DemoViewer.NET.AppTests.SettingsViewModelTests;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     A refused first-party copy is listed once, under "Available extensions" with its reason and Remove,
///     even before any feed is asked; a refused third-party copy stays in the third-party list.
/// </summary>
[NotInParallel]
public class SettingsExternalExtensionListTests
{
    [Test]
    public async Task ARefusedFirstPartyCopy_IsListedOnceInTheCatalogue_NotAmongTheThirdPartyOnes()
    {
        string dir = NewTempDir();
        try
        {
            string extensions = Path.Combine(dir, "extensions");
            string firstParty = ExtensionUpdateRemovalTests.Stage(extensions, StratBookPack.PackId, "1.0.0");
            string thirdParty = ExtensionUpdateRemovalTests.Stage(extensions, "dev.example.other", "1.0.0");
            LoadOutcome refusedFirst = new(firstParty, FakeManifests.For(StratBookPack.PackId, "Strat Book", "1.0.0"),
                LoadFailure.ReservedId, "the copy is not signed", External: true);
            LoadOutcome refusedThird = new(thirdParty, FakeManifests.For("dev.example.other", "Other", "1.0.0"),
                LoadFailure.Unverified, "it is not verified", External: true);
            UpdateFixtures.FakeFeedClient client = new();
            ExtensionUpdateService updater = new(dir, [], ExtensionHost.Current, TrustPolicy.Nothing, client,
                UpdateFixtures.FeedUrl, externalRejected: [refusedFirst, refusedThird], shippedIds: []);

            (SettingsViewModel vm, SettingsService _, IFeatureGate _, ServiceProvider sp) = NewVm(dir,
                packStatuses: [], extensionUpdates: updater, isBrowser: () => false,
                seed: s => s.Updates.CheckAutomatically = false);
            await using (sp)
            {
                using (Assert.Multiple())
                {
                    await Assert.That(vm.ExternalExtensionRows.Select(r => r.Name)).IsEquivalentTo(["Other"]);
                    InstallableExtensionRow row = vm.InstallableExtensionRows.Single();
                    await Assert.That(row.Id).IsEqualTo(StratBookPack.PackId);
                    await Assert.That(row.CanRemove).IsTrue();
                    await Assert.That(row.Message).IsEqualTo(refusedFirst.UserMessage);
                    await Assert.That(vm.HasInstallableExtensions).IsTrue();
                    await Assert.That(client.FeedFetches).IsEqualTo(0);
                }

                vm.Dispose();
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
