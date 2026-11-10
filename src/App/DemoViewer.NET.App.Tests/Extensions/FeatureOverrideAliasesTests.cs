#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Features;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     A renamed extension feature keeps the user's choice: an override saved under one of its
///     <see cref="ExtensionFeature.PreviousIds" /> moves to the new id at startup, and the old key goes. Synthetic
///     ids only: the mechanism is the host's, whichever extension renames.
/// </summary>
public class FeatureOverrideAliasesTests
{
    private const string PackId = "pack.renamer";
    private const string NewId = "tab.renamer.new";
    private const string MiddleId = "tab.renamer.middle";
    private const string OldestId = "tab.renamer.oldest";

    private static FeatureDescriptor[] Catalog(params string[] previousIds) =>
        FeatureCatalog.Build([new RenamingPack(previousIds)]);

    [Test]
    public async Task TheCatalog_CarriesTheExtensionsPreviousIds()
    {
        FeatureDescriptor tab = Catalog(OldestId, MiddleId).Single(d => d.Id == NewId);
        await Assert.That(tab.PreviousIds).IsEquivalentTo([OldestId, MiddleId]);
    }

    [Test]
    public async Task AnOverrideUnderAPreviousId_MovesToTheNewId_AndTheOldKeyGoes()
    {
        Dictionary<string, bool> overrides = new(StringComparer.Ordinal) { [OldestId] = false, ["tab.stats"] = true };

        bool changed = FeatureOverrideAliases.Migrate(overrides, Catalog(OldestId));
        bool again = FeatureOverrideAliases.Migrate(overrides, Catalog(OldestId));

        using (Assert.Multiple())
        {
            await Assert.That(changed).IsTrue();
            await Assert.That(again).IsFalse().Because("a second pass finds nothing to move");
            await Assert.That(overrides[NewId]).IsFalse();
            await Assert.That(overrides.ContainsKey(OldestId)).IsFalse();
            await Assert.That(overrides["tab.stats"]).IsTrue().Because("an unrelated override is untouched");
        }
    }

    [Test]
    public async Task OfSeveralPreviousIds_TheNewestWithAValueWins()
    {
        Dictionary<string, bool> overrides = new(StringComparer.Ordinal) { [OldestId] = true, [MiddleId] = false };

        FeatureOverrideAliases.Migrate(overrides, Catalog(OldestId, MiddleId));

        using (Assert.Multiple())
        {
            await Assert.That(overrides[NewId]).IsFalse().Because("the middle id is newer than the oldest");
            await Assert.That(overrides.ContainsKey(OldestId)).IsFalse();
            await Assert.That(overrides.ContainsKey(MiddleId)).IsFalse();
        }
    }

    [Test]
    public async Task AnOverrideAlreadyUnderTheNewId_Wins_AndTheStaleKeyIsRemoved()
    {
        Dictionary<string, bool> overrides = new(StringComparer.Ordinal) { [NewId] = true, [OldestId] = false };

        bool changed = FeatureOverrideAliases.Migrate(overrides, Catalog(OldestId));

        using (Assert.Multiple())
        {
            await Assert.That(changed).IsTrue();
            await Assert.That(overrides[NewId]).IsTrue();
            await Assert.That(overrides.ContainsKey(OldestId)).IsFalse();
        }
    }

    [Test]
    public async Task Apply_PersistsTheMove_AndWritesNothingWhenThereIsNothingToMove()
    {
        string dir = Directory.CreateTempSubdirectory("dv-feature-alias-").FullName;
        try
        {
            SettingsService settings = new(dir);
            settings.Write(s => s.Features.Overrides[OldestId] = false);
            FeatureDescriptor[] catalog = Catalog(OldestId);

            bool wrote = FeatureOverrideAliases.Apply(settings, catalog);
            string path = Directory.EnumerateFiles(dir, "*.json").Single();
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            string text = await File.ReadAllTextAsync(path);
            bool wroteAgain = FeatureOverrideAliases.Apply(new SettingsService(dir), catalog);

            using (Assert.Multiple())
            {
                await Assert.That(wrote).IsTrue();
                await Assert.That(new SettingsService(dir).Current.Features.Overrides[NewId]).IsFalse();
                await Assert.That(new SettingsService(dir).Current.Features.Overrides.ContainsKey(OldestId)).IsFalse();
                await Assert.That(text).DoesNotContain(OldestId);
                await Assert.That(wroteAgain).IsFalse();
                await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(stamp);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task APreviousId_ThatIsStillLive_IsRefusedAtCompose()
    {
        await Assert.That(() => Catalog(PackId)).Throws<InvalidOperationException>()
            .Because("a live feature's own override must never be taken by a rename");
    }

    // One extension with one tab, renamed from previousIds.
    private sealed class RenamingPack(string[] previousIds) : IExtension, IManifestSource
    {
        public string Id => "net.demoviewer.test.renamer";
        public string FeatureId => PackId;
        public ExtensionManifest Manifest => FakeManifests.For(Id);

        public IEnumerable<ExtensionFeature> Features =>
        [
            new(PackId, ExtensionFeatureKind.Extension, "Renamer", "a test pack", null, AudienceDefaults.Everyone),
            new(NewId, ExtensionFeatureKind.Tab, "Renamed tab", "a renamed tab", PackId, AudienceDefaults.Everyone)
            {
                PreviousIds = previousIds
            }
        ];

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
        }
    }
}
