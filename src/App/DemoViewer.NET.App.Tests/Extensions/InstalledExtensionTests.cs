#region

using System.Runtime.Loader;
using System.Security.Cryptography;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services.DemoProcessing;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The Strat Book as the pilot of an installed first-party extension: packed the way the release workflow
///     packs it and signed with an ephemeral key, installed through the updater on a build that leaves it
///     out (an empty shipped list), loaded on the strength of its signature alone, and refused for every way
///     a reserved-id copy can be wrong. The test process still has the shipped assembly beside it, so a loaded
///     copy is a second assembly of the same name, as <see cref="ExtensionLoaderTests" /> already allows.
/// </summary>
[NotInParallel]
public class InstalledExtensionTests
{
    private const string Id = StratBookPack.PackId;

    // Lower than any real version, so an installed copy at it reads as the older of two.
    private const string Older = "0.0.1";

    // ── Install and load ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Install_OnABuildWithoutStratBook_LoadsItAsInstalled_InTheDependencyResolvingContext()
    {
        string root = NewRoot();
        string? previous = Environment.GetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar);
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            (FakeFeedClient client, FakeFeeds.Entry entry) = Published(root, Built, key);
            RecordingTrust shippedPolicy = new(true);
            ExtensionUpdateService service = new(root, [], ExtensionHost.Current, shippedPolicy, client, FeedUrl,
                installTrust: new SignedTrustPolicy([publicKey]));

            StageResult staged = await service.DownloadAndStageAsync(Feed(entry));
            ExtensionStartupResult result = ExtensionStartup.Resolve(root, [], ExtensionHost.Current,
                TrustPolicy.Nothing, [publicKey], allowUnverified: false, safeMode: false);

            PackStatus status = result.Statuses.Single();
            IExtension pack = status.Pack;
            using (Assert.Multiple())
            {
                await Assert.That(staged.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(shippedPolicy.Asked).IsEmpty()
                    .Because("an id this build does not ship is the install policy's to judge");
                await Assert.That(result.ExternalRejected).IsEmpty();
                await Assert.That(status.Source).IsEqualTo(new PackSource.Installed(staged.Directory!));
                await Assert.That(status.Source.Label).IsEqualTo("installed");
                await Assert.That(status.IsCompatible).IsTrue().Because(status.Problem ?? "compatible");
                await Assert.That(pack.Id).IsEqualTo(Id);
                await Assert.That(AssemblyLoadContext.GetLoadContext(pack.GetType().Assembly))
                    .IsTypeOf<ExternalLoadContext>();
                await Assert.That(ReferenceEquals(pack.GetType().Assembly, typeof(StratBookPack).Assembly)).IsFalse()
                    .Because("the installed copy is a second assembly, not the one beside the test host");
                await Assert.That(pack.GetType().FullName).IsEqualTo(typeof(StratBookPack).FullName);
                await Assert.That(FeatureCatalog.Build([pack]).Select(d => d.Id))
                    .Contains(StratBookPack.PackFeatureId).And.Contains("tab.situations").And.Contains("tab.teams");
                await Assert.That(result.ClaimedRulesets)
                    .Contains(RulesetContribution.QualifiedId(Id, "roundindex_positions"));
            }

            // The installed copy composes the way the shipped one does: its settings pages and passes are
            // there, through the host's own contract types.
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, root);
            await HeadlessSession.RunOnUi(async () =>
            {
                Microsoft.Extensions.DependencyInjection.ServiceCollection services =
                    App.ComposeServices(new Services.DesktopWindowService(() => null), [pack]);
                await using Microsoft.Extensions.DependencyInjection.ServiceProvider provider =
                    Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions
                        .BuildServiceProvider(services);
                PackContributions contributed = Microsoft.Extensions.DependencyInjection
                    .ServiceProviderServiceExtensions
                    .GetRequiredService<PackContributionSet>(provider).Packs.Single();

                using (Assert.Multiple())
                {
                    await Assert.That(ReferenceEquals(contributed.Pack, pack)).IsTrue();
                    await Assert.That(contributed.SettingsPages).IsNotEmpty();
                    await Assert.That(contributed.Passes.Count + contributed.RecordPasses.Count).IsGreaterThan(0);
                    await Assert.That(contributed.Modules).IsNotEmpty();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(Services.AppPaths.ConfigDirEnvVar, previous);
            Cleanup(root);
        }
    }

    // ── The negatives: every way a reserved-id copy on disk can be wrong, each its own reason ────

    [Test]
    public async Task Unsigned_IsReservedId_EvenWithUnverifiedAllowedAndTheEnvOptIn()
    {
        string root = NewRoot();
        try
        {
            InstallTree(root, Built, key: null);
            LoadOutcome refused = ResolveLenient(root).ExternalRejected.Single();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.ReservedId);
                await Assert.That(refused.Detail).IsEqualTo(
                    $"the id '{Id}' is reserved for extensions signed by this app's publisher, and the copy is not signed");
                await Assert.That(refused.UserMessage).StartsWith($"Strat Book {Built} was not loaded");
                await Assert.That(refused.UserMessage).DoesNotContain(ExternalExtensions.AllowUnverifiedLabel);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task SignedByAnotherKey_IsReservedForeignKey()
    {
        string root = NewRoot();
        using ECDsa other = NewKey(out _);
        try
        {
            InstallTree(root, Built, other);
            LoadOutcome refused = ResolveLenient(root).ExternalRejected.Single();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.ReservedForeignKey);
                await Assert.That(refused.Detail).IsEqualTo(
                    $"the id '{Id}' is reserved for extensions signed by this app's publisher, and the copy is signed by a key this app does not know");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task OneByteChangedAfterSigning_IsUntrusted_ChangedAfterSigning()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string dir = InstallTree(root, Built, key);
            string dll = Path.Combine(dir, BuiltManifest().Assembly);
            byte[] bytes = await File.ReadAllBytesAsync(dll);
            bytes[bytes.Length / 2] ^= 0x01;
            await File.WriteAllBytesAsync(dll, bytes);

            LoadOutcome refused = ResolveLenient(root, publicKey).ExternalRejected.Single();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.Untrusted);
                await Assert.That(refused.Detail).IsEqualTo("a file changed after signing");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A signed tree moved under another id or version: discovery refuses the folder before any signature
    // is read, and the manifest inside the digest cannot be edited to match without breaking the signature.
    [Test]
    public async Task IdFolderRenamed_IsFolderMismatch()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            InstallTree(root, Built, key);
            Directory.Move(Path.Combine(root, "extensions", Id), Path.Combine(root, "extensions", "net.demoviewer.pack.other"));

            LoadOutcome refused = ResolveLenient(root, publicKey).ExternalRejected.Single();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.FolderMismatch);
                await Assert.That(refused.External).IsTrue();
                await Assert.That(refused.Detail).Contains("net.demoviewer.pack.other");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task VersionFolderRenamed_IsFolderMismatch()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string dir = InstallTree(root, Built, key);
            Directory.Move(dir, Path.Combine(Path.GetDirectoryName(dir)!, "9.9.9"));

            LoadOutcome refused = ResolveLenient(root, publicKey).ExternalRejected.Single();

            using (Assert.Multiple())
            {
                await Assert.That(refused.Failure).IsEqualTo(LoadFailure.FolderMismatch);
                await Assert.That(refused.Detail).Contains("9.9.9");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Every shipped id is the shipped loader's alone, whatever its verdict: with the shipped copy
    // incompatible, a signed copy on disk is still not taken in through the first-party branch.
    [Test]
    public async Task AShippedIdIsNeverInstalled_EvenWhenTheShippedCopyIsIncompatible()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            InstallTree(root, Built, key);
            ExtensionHostInfo tooOld = new(SemVersion.Parse("1.0.0"), null, SemVersion.Parse("0.14.1"));
            ShippedPack shipped = ShippedPack.BesideApp(Id, static () => new StratBookPack());

            ExtensionStartupResult result = ExtensionStartup.Resolve(root, [shipped], tooOld,
                new SignedTrustPolicy([publicKey]), [publicKey], allowUnverified: true, safeMode: false);

            using (Assert.Multiple())
            {
                await Assert.That(result.Statuses.Count(s => s.Pack.Id == Id)).IsEqualTo(1);
                await Assert.That(result.Statuses.Single().IsCompatible).IsFalse();
                await Assert.That(result.Statuses.Single().Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(result.ExternalRejected).IsEmpty()
                    .Because("the folder is the shipped loader's to judge, not a first-party install");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Automatic staging ────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task AutoStage_StagesASignedSecondVersion_LeavesTheFirst_AndTheNextResolveLoadsIt()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string first = InstallTree(root, Older, key);
            (FakeFeedClient client, FakeFeeds.Entry entry) = Published(root, Built, key);
            RecordingQueue queue = new();
            ExtensionUpdateService service = new(root, [InstalledStatus(Id, Older, first)], ExtensionHost.Current,
                TrustPolicy.Nothing, client, FeedUrl, queue, installTrust: new SignedTrustPolicy([publicKey]));

            IReadOnlyList<ExtensionUpdateState> states = await service.AutoStageAsync();
            ExtensionStartupResult next = ExtensionStartup.Resolve(root, [], ExtensionHost.Current,
                TrustPolicy.Nothing, [publicKey], allowUnverified: false, safeMode: false);
            string second = Path.Combine(root, "extensions", Id, Built);

            using (Assert.Multiple())
            {
                await Assert.That(queue.Requests.Select(r => (r.Title, r.Priority)))
                    .IsEquivalentTo([("Update extensions", DemoJobPriority.Background)]);
                await Assert.That(states.Single().Status).IsEqualTo(ExtensionUpdateStatus.PendingRestart);
                await Assert.That(states.Single().Pending).IsEqualTo(SemVersion.Parse(Built));
                await Assert.That(service.LastState(Id)).IsEqualTo(states.Single());
                await Assert.That(Directory.Exists(first)).IsTrue().Because("the running copy is left for the sweep");
                await Assert.That(Directory.Exists(second)).IsTrue();
                await Assert.That(next.Statuses.Single().Source).IsEqualTo(new PackSource.Installed(second));
            }

            // The next launch's sweep: the superseded copy goes, the running one stays.
            new ExtensionUpdateService(root, [InstalledStatus(Id, Built, second)], ExtensionHost.Current,
                TrustPolicy.Nothing, client, FeedUrl).Cleanup();
            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(first)).IsFalse();
                await Assert.That(Directory.Exists(second)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task AutoStage_StagesNothingUnsigned()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string first = InstallTree(root, Older, key);
            (FakeFeedClient client, _) = Published(root, Built, key: null);
            ExtensionUpdateService service = new(root, [InstalledStatus(Id, Older, first)], ExtensionHost.Current,
                TrustAll, client, FeedUrl, installTrust: new SignedTrustPolicy([publicKey]));

            IReadOnlyList<ExtensionUpdateState> states = await service.AutoStageAsync();

            using (Assert.Multiple())
            {
                await Assert.That(client.Downloads).IsEqualTo(1);
                await Assert.That(states.Single().Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable)
                    .Because("the offer stands; the user may still try it and see the refusal");
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", Id, Built))).IsFalse();
                await Assert.That(Directory.EnumerateFiles(Path.Combine(root, "extensions"), "*", SearchOption.AllDirectories)
                        .Where(f => !f.StartsWith(first, StringComparison.Ordinal)))
                    .IsEmpty().Because("a refusal leaves nothing of the download behind");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The launch gate: off, or within the hour, asks nothing; due with nothing first-party to check asks
    // nothing and says so, since the caller records the time only on a true; due with an installed copy
    // asks the feed and stages.
    [Test]
    public async Task AutoStageAtLaunch_RunsOnlyWhenOnAndDue_AndReportsWhetherAFeedWasAsked()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string first = InstallTree(root, Older, key);
            (FakeFeedClient client, _) = Published(root, Built, key);
            DateTimeOffset now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
            ExtensionUpdateService installed = new(root, [InstalledStatus(Id, Older, first)], ExtensionHost.Current,
                TrustPolicy.Nothing, client, FeedUrl, installTrust: new SignedTrustPolicy([publicKey]));
            ExtensionUpdateService nothingFirstParty = new(root, [], ExtensionHost.Current, TrustPolicy.Nothing,
                client, FeedUrl, installTrust: new SignedTrustPolicy([publicKey]));

            bool off = await installed.AutoStageAtLaunchAsync(false, null, now);
            int fetchesAfterOff = client.FeedFetches;
            bool withinTheHour = await installed.AutoStageAtLaunchAsync(true, now.AddMinutes(-30), now);
            int fetchesAfterWithinTheHour = client.FeedFetches;
            bool nothingToCheck = await nothingFirstParty.AutoStageAtLaunchAsync(true, null, now);
            int fetchesAfterNothing = client.FeedFetches;
            bool due = await installed.AutoStageAtLaunchAsync(true, now.AddHours(-1), now);

            using (Assert.Multiple())
            {
                await Assert.That(off).IsFalse();
                await Assert.That(fetchesAfterOff).IsEqualTo(0);
                await Assert.That(withinTheHour).IsFalse();
                await Assert.That(fetchesAfterWithinTheHour).IsEqualTo(0);
                await Assert.That(nothingToCheck).IsFalse().Because("nothing was asked, so the record must not move");
                await Assert.That(fetchesAfterNothing).IsEqualTo(0);
                await Assert.That(due).IsTrue();
                await Assert.That(client.FeedFetches).IsEqualTo(1);
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", Id, Built))).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Removal ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Remove_WritesTheTombstone_TheNextLaunchDeletesTheFolder_AndInstallClearsIt()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            string dir = InstallTree(root, Built, key);
            string extensions = Path.Combine(root, "extensions");
            string marker = ExtensionStaging.RemovalMarkerPath(extensions, Id);
            (FakeFeedClient client, FakeFeeds.Entry entry) = Published(root, Built, key);
            ExtensionUpdateService service = new(root, [InstalledStatus(Id, Built, dir)], ExtensionHost.Current,
                TrustPolicy.Nothing, client, FeedUrl, installTrust: new SignedTrustPolicy([publicKey]));

            bool removed = await service.RemoveInstalledAsync(Id);
            bool markerWritten = File.Exists(marker);
            bool stillDiscovered = ExtensionLoader.Discover(extensions).Candidates.Any(c => c.Directory == dir);

            ExtensionStartupResult nextLaunch = ExtensionStartup.Resolve(root, [], ExtensionHost.Current,
                TrustPolicy.Nothing, [publicKey], allowUnverified: false, safeMode: false);
            bool gone = !Directory.Exists(Path.Combine(extensions, Id));

            // Remove, then Install before the restart: the install clears the marker and the copy stays.
            Directory.CreateDirectory(Path.Combine(extensions, Id));
            File.WriteAllText(marker, string.Empty);
            StageResult reinstalled = await new ExtensionUpdateService(root, [], ExtensionHost.Current,
                    TrustPolicy.Nothing, client, FeedUrl, installTrust: new SignedTrustPolicy([publicKey]))
                .DownloadAndStageAsync(Feed(entry));
            ExtensionStaging.ApplyPendingRemovals(extensions);

            using (Assert.Multiple())
            {
                await Assert.That(removed).IsTrue();
                await Assert.That(markerWritten).IsTrue();
                await Assert.That(stillDiscovered).IsTrue().Because("the loaded copy stays until the restart");
                await Assert.That(nextLaunch.Statuses).IsEmpty();
                await Assert.That(nextLaunch.ExternalRejected).IsEmpty();
                await Assert.That(gone).IsTrue();
                await Assert.That(reinstalled.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(File.Exists(marker)).IsFalse();
                await Assert.That(Directory.Exists(reinstalled.Directory!)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Catalogue ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Catalogue_ListsStratBook_OnABuildWithoutIt_AndNothingWhenItShips()
    {
        string root = NewRoot();
        using ECDsa key = NewKey(out string publicKey);
        try
        {
            (FakeFeedClient client, FakeFeeds.Entry entry) = Published(root, Built, key);
            IReadOnlyList<InstallableExtension> without = await new ExtensionUpdateService(root, [],
                    ExtensionHost.Current, TrustPolicy.Nothing, client, FeedUrl,
                    installTrust: new SignedTrustPolicy([publicKey]), shippedIds: [])
                .CheckCatalogueAsync();
            IReadOnlyList<InstallableExtension> with = await new ExtensionUpdateService(root,
                    [FeaturePacks.Statuses.Single(s => s.Pack.Id == Id)], ExtensionHost.Current, TrustPolicy.Nothing,
                    client, FeedUrl)
                .CheckCatalogueAsync();

            using (Assert.Multiple())
            {
                InstallableExtension listed = without.Single();
                await Assert.That(listed.Id).IsEqualTo(Id);
                await Assert.That(listed.Name).IsEqualTo("Strat Book");
                await Assert.That(listed.Status).IsEqualTo(InstallableStatus.Available);
                await Assert.That(listed.Offered!.Version).IsEqualTo(SemVersion.Parse(Built));
                await Assert.That(listed.Offered.Size).IsEqualTo(entry.Bytes!.LongLength);
                await Assert.That(with).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────

    private static string Built => BuiltManifest().Version.ToString();

    private static ExtensionManifest BuiltManifest()
    {
        string shippedDll = typeof(StratBookPack).Assembly.Location;
        string path = Path.Combine(Path.GetDirectoryName(shippedDll)!, ExtensionManifest.FileName);
        if (!File.Exists(path))
        {
            throw new SkipTestException($"no {ExtensionManifest.FileName} beside {shippedDll}");
        }

        return ExtensionManifest.Parse(File.ReadAllText(path));
    }

    private static ECDsa NewKey(out string publicKeyBase64Spki)
    {
        ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        publicKeyBase64Spki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return key;
    }

    // The tree the release workflow zips: the extension assembly this test process runs, the stamped
    // manifest beside it (with the version rewritten when asked), and the signature over both.
    private static string WriteTree(string directory, string version, ECDsa? key)
    {
        string shippedDll = typeof(StratBookPack).Assembly.Location;
        string json = File.ReadAllText(Path.Combine(Path.GetDirectoryName(shippedDll)!, ExtensionManifest.FileName));
        ExtensionManifest built = ExtensionManifest.Parse(json);
        json = json.Replace($"\"{built.Version}\"", $"\"{version}\"", StringComparison.Ordinal);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ExtensionManifest.FileName), json);
        File.Copy(shippedDll, Path.Combine(directory, built.Assembly), overwrite: true);
        if (key is not null)
        {
            File.WriteAllText(Path.Combine(directory, ExtensionSignature.FileName), ExtensionSignature.Sign(directory, key));
        }

        return directory;
    }

    // A copy on disk where the loader reads, as an install leaves it.
    private static string InstallTree(string root, string version, ECDsa? key) =>
        WriteTree(Path.Combine(root, "extensions", Id, version), version, key);

    // A feed with one entry whose zip is the signed tree and whose manifest is the tree's own, the way the
    // release workflow publishes it, served by the fake client.
    private static (FakeFeedClient Client, FakeFeeds.Entry Entry) Published(string root, string version, ECDsa? key)
    {
        string tree = WriteTree(Path.Combine(root, "pack", version), version, key);
        byte[] zip = ZipDirectory(tree);
        FakeFeeds.Entry entry = new(version, zip);
        FakeFeedClient client = new()
        {
            Zips = { [entry.Url] = zip },
            Feeds = { [FeedUrl(Id)] = FeedJson(entry, File.ReadAllText(Path.Combine(tree, ExtensionManifest.FileName))) }
        };
        return (client, entry);
    }

    private static string FeedJson(FakeFeeds.Entry entry, string manifestJson) =>
        $$"""
          {
            "id": "{{Id}}",
            "entries": [
              {
                "version": "{{entry.Version}}",
                "manifest": {{manifestJson}},
                "url": "{{entry.Url}}",
                "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(entry.Bytes!))}}",
                "size": {{entry.Bytes!.LongLength}}
              }
            ]
          }
          """;

    private static ExtensionFeedEntry Feed(FakeFeeds.Entry entry)
    {
        string manifest = FakeFeeds.Manifest(Id, entry).Replace("\"name\": \"Fake\"", "\"name\": \"Strat Book\"",
            StringComparison.Ordinal);
        return ExtensionFeed.Parse(FeedJson(entry, manifest)).Entries.Single();
    }

    // The most permissive launch there is: unverified allowed, the developer opt-in set. None of it reaches
    // a reserved id this build does not ship.
    private static ExtensionStartupResult ResolveLenient(string root, string? publicKey = null) =>
        ExtensionStartup.Resolve(root, [], ExtensionHost.Current, TrustPolicy.ForLaunch(true, static _ => "1"),
            publicKey is null ? PublisherKeys.Current : [publicKey], allowUnverified: true, safeMode: false);

    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvinst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            // best-effort
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort
        }
    }
}
