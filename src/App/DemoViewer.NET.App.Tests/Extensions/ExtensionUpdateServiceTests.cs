#region

using System.IO.Compression;
using System.Net;
using DemoViewer.NET.Extensions.Loading;
using DemoViewer.NET.Extensions.Manifest;
using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.Updates;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.ViewModels.Settings;
using TUnit.Assertions.Enums;
using TUnit.Core.Exceptions;
using static DemoViewer.NET.AppTests.Extensions.UpdateFixtures;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     <see cref="ExtensionUpdateService" /> over a fake feed client and a temp
///     config root: every check outcome is a state, a download is verified before it is opened, unpacked
///     where nothing reads it, judged, and renamed into place only when every check passed; a refusal leaves
///     nothing behind; and every step runs as a user-priority queue item.
/// </summary>
public class ExtensionUpdateServiceTests
{
    private const string Id = FakeFeeds.Id;

    // ── Check ────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Check_UpToDate_Available_NeedsNewerApp_Unreachable_Invalid_Unknown()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new();
            ExtensionUpdateService service = NewService(root, client);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0"), new FakeFeeds.Entry("0.9.0"));
            ExtensionUpdateState upToDate = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.1"),
                new FakeFeeds.Entry("1.2.0", RequiresHost: "^3.0"));
            ExtensionUpdateState available = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.2.0", RequiresHost: "^3.0"));
            ExtensionUpdateState needsApp = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.1.0", MinAppVersion: "0.9.5"));
            ExtensionUpdateState needsRelease = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = "{ nope";
            ExtensionUpdateState invalid = await service.CheckAsync(Id);

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json("net.demoviewer.pack.other", new FakeFeeds.Entry("9.0.0"));
            ExtensionUpdateState otherFeed = await service.CheckAsync(Id);

            client.FeedFailure = new HttpRequestException("offline");
            ExtensionUpdateState unreachable = await service.CheckAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(upToDate.Status).IsEqualTo(ExtensionUpdateStatus.UpToDate);
                await Assert.That(upToDate.Installed).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(upToDate.Latest!.Version).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(upToDate.Offered).IsNull();
                await Assert.That(upToDate.Name).IsEqualTo("Fake");
                await Assert.That(upToDate.CheckedAt).IsNotNull();

                await Assert.That(available.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(available.Offered!.Version).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(available.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"))
                    .Because("the latest overall is reported beside the offered one");

                await Assert.That(needsApp.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(needsApp.Offered).IsNull();
                await Assert.That(needsApp.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(needsApp.LatestCompatibility).IsTypeOf<PackCompatibility.HostContractMismatch>();

                await Assert.That(needsRelease.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(needsRelease.LatestCompatibility).IsTypeOf<PackCompatibility.AppTooOld>();

                await Assert.That(invalid.Status).IsEqualTo(ExtensionUpdateStatus.FeedInvalid);
                await Assert.That(invalid.Error).IsEqualTo("the update feed could not be read");
                await Assert.That(invalid.LogDetail).Contains("not valid JSON");

                await Assert.That(otherFeed.Status).IsEqualTo(ExtensionUpdateStatus.FeedInvalid);
                await Assert.That(otherFeed.Error).IsEqualTo("the update feed is for another extension");

                await Assert.That(unreachable.Status).IsEqualTo(ExtensionUpdateStatus.FeedUnreachable);
                await Assert.That(unreachable.Error).IsEqualTo("the update feed could not be reached");
                await Assert.That(unreachable.LogDetail).IsEqualTo("offline");
                await Assert.That(unreachable.Installed).IsEqualTo(SemVersion.Parse("1.0.0"))
                    .Because("the running version is known even when the feed is not");

                await Assert.That(service.LastStates.Single().Status).IsEqualTo(ExtensionUpdateStatus.FeedUnreachable)
                    .Because("a per-pack check updates the remembered states");
            }

            ExtensionUpdateService noManifest = NewService(root, client, statuses: [StatusWithoutManifest(Id)]);
            IReadOnlyList<ExtensionUpdateState> all = await noManifest.CheckAsync();
            using (Assert.Multiple())
            {
                await Assert.That(all.Single().Status).IsEqualTo(ExtensionUpdateStatus.Unknown);
                await Assert.That(all.Single().Installed).IsNull();
                await Assert.That(all.Single().Name).IsEqualTo(Id);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The rolling release does not exist until the first extension release, so GitHub answers 404. That is
    // not an alarm, but it is not "up to date" either: a wrong feed address answers 404 too. Any other HTTP
    // failure stays unreachable.
    [Test]
    public async Task Check_FeedNotFound_IsNotPublished_OtherStatusesAreUnreachable()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new();
            ExtensionUpdateService service = NewService(root, client);

            client.FeedFailure = new HttpRequestException("404 (Not Found)", null, HttpStatusCode.NotFound);
            ExtensionUpdateState notPublished = await service.CheckAsync(Id);

            client.FeedFailure = new HttpRequestException("403 (Forbidden)", null, HttpStatusCode.Forbidden);
            ExtensionUpdateState forbidden = await service.CheckAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(notPublished.Status).IsEqualTo(ExtensionUpdateStatus.NotPublished);
                await Assert.That(notPublished.Error).IsNull();
                await Assert.That(ExtensionUpdateRow.Describe(notPublished))
                    .IsEqualTo("No updates have been published yet.");
                await Assert.That(notPublished.Latest).IsNull();
                await Assert.That(notPublished.Installed).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(notPublished.CheckedAt).IsNotNull();
                await Assert.That(client.FeedFetches).IsEqualTo(2).Because("a 404 is one request, not a retry loop");

                await Assert.That(forbidden.Status).IsEqualTo(ExtensionUpdateStatus.FeedUnreachable);
                await Assert.That(forbidden.Error).IsEqualTo("the update feed could not be reached");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // This is one predicate: the default is the compatibility check; either option replaces it here.
    [Test]
    public async Task Check_TheDecisionSixPredicate_IsTheOnlyGateOnWhatIsOffered()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new()
            {
                Feeds =
                {
                    [FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.1"),
                        new FakeFeeds.Entry("1.2.0", RequiresHost: "^3.0"))
                }
            };

            ExtensionUpdateState strict = await NewService(root, client, isOffered: (_, _) => false).CheckAsync(Id);
            ExtensionUpdateState loose = await NewService(root, client, isOffered: (_, _) => true).CheckAsync(Id);
            ExtensionUpdateState throwing =
                await NewService(root, client, isOffered: (_, _) => throw new InvalidOperationException("boom"))
                    .CheckAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(strict.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp);
                await Assert.That(strict.Latest!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(loose.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(loose.Offered!.Version).IsEqualTo(SemVersion.Parse("1.2.0"));
                await Assert.That(throwing.Status).IsEqualTo(ExtensionUpdateStatus.NeedsNewerApp)
                    .Because("a predicate that throws offers nothing");
                await Assert.That(ExtensionUpdateService.DefaultIsOffered(loose.Offered, Host)).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Download and stage ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Download_WrongShaOrSize_RefusedAndNothingStaged()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry wrongSha = new("1.0.1", zip, Sha256: new string('a', 64));
            FakeFeeds.Entry tooSmall = new("1.0.1", zip, Size: zip.Length - 1);
            FakeFeeds.Entry tooLarge = new("1.0.1", zip, Size: zip.Length + 1);
            FakeFeedClient client = new() { Zips = { [wrongSha.Url] = zip } };
            ExtensionUpdateService service = NewService(root, client);

            StageResult sha = await service.DownloadAndStageAsync(Entry(wrongSha));
            StageResult small = await service.DownloadAndStageAsync(Entry(tooSmall));
            StageResult large = await service.DownloadAndStageAsync(Entry(tooLarge));

            using (Assert.Multiple())
            {
                await Assert.That(sha.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(sha.Detail).IsEqualTo("the download does not match the feed's checksum");
                await Assert.That(small.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(small.Detail).IsEqualTo("the download failed");
                await Assert.That(large.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(large.Detail)
                    .IsEqualTo($"the download is {zip.Length} bytes; the feed said {zip.Length + 1}");
                await Assert.That(NothingStaged(root)).IsTrue();
                await Assert.That(service.PendingVersion(Id)).IsNull();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_ZipSlipEntry_Refused()
    {
        string root = NewRoot();
        try
        {
            string manifest = FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"));
            byte[][] evil =
            [
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("../escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("sub/../../escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("/etc/escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("..\\escaped.txt", "x")),
                Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("C:\\escaped.txt", "x"))
            ];

            using (Assert.Multiple())
            {
                foreach (byte[] zip in evil)
                {
                    FakeFeeds.Entry entry = new("1.0.1", zip);
                    FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };
                    StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));
                    await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                    await Assert.That(result.Detail).Contains("escaped.txt");
                    await Assert.That(result.Detail).DoesNotContain(root)
                        .Because("Settings shows the detail; a path belongs in the log only");
                    await Assert.That(NothingStaged(root)).IsTrue();
                }

                await Assert.That(File.Exists(Path.Combine(root, "escaped.txt"))).IsFalse();
                await Assert.That(File.Exists(Path.Combine(root, "extensions", "escaped.txt"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Defence in depth: ZipArchive would write a link entry as a file holding the target text, but a
    // Unix-made entry whose mode says symlink is refused before anything is written.
    [Test]
    public async Task Download_SymlinkEntry_OrTwoNamesDifferingOnlyByCase_Refused()
    {
        string root = NewRoot();
        try
        {
            string manifest = FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"));
            byte[] withLink;
            using (MemoryStream stream = new())
            {
                await using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    Write(archive, ExtensionManifest.FileName, manifest);
                    Write(archive, "Fake.dll", "x");
                    ZipArchiveEntry link = Write(archive, "link.dll", "../../outside.dll");
                    link.ExternalAttributes = unchecked((int)0xA1FF0000);
                }

                withLink = stream.ToArray();
            }

            byte[] caseTwins = Zip((ExtensionManifest.FileName, manifest), ("Fake.dll", "x"), ("fake.dll", "y"));

            using (Assert.Multiple())
            {
                foreach ((byte[] zip, string reason) in new[]
                         {
                             (withLink, "'link.dll' is a link"), (caseTwins, "differing only by case")
                         })
                {
                    FakeFeeds.Entry entry = new("1.0.1", zip);
                    FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };
                    StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));
                    await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                    await Assert.That(result.Detail).Contains(reason);
                    await Assert.That(NothingStaged(root)).IsTrue();
                }
            }
        }
        finally
        {
            Cleanup(root);
        }

        static ZipArchiveEntry Write(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream body = entry.Open();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
            body.Write(bytes, 0, bytes.Length);
            return entry;
        }
    }

    [Test]
    public async Task Download_ManifestMissingOrMismatched_Refused()
    {
        string root = NewRoot();
        try
        {
            byte[] wrongVersion =
                Zip((ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.2"))),
                    ("Fake.dll", "x"));
            byte[] wrongId =
                Zip(
                    (ExtensionManifest.FileName,
                        FakeFeeds.Manifest("net.demoviewer.pack.other", new FakeFeeds.Entry("1.0.1"))),
                    ("Fake.dll", "x"));
            byte[] noManifest = Zip(("Fake.dll", "x"));
            byte[] nested =
                Zip(("inner/" + ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"))),
                    ("inner/Fake.dll", "x"));
            byte[] noAssembly = Zip((ExtensionManifest.FileName, FakeFeeds.Manifest(Id, new FakeFeeds.Entry("1.0.1"))));
            byte[] notAZip = [.. "not a zip"u8];

            (byte[] Zip, string Reason)[] cases =
            [
                (wrongVersion, $"the archive is {Id} 1.0.2, not {Id} 1.0.1"),
                (wrongId, $"the archive is net.demoviewer.pack.other 1.0.1, not {Id} 1.0.1"),
                (noManifest, $"the archive has no {ExtensionManifest.FileName} at its root"),
                (nested, $"the archive has no {ExtensionManifest.FileName} at its root"),
                (noAssembly, "the archive has no 'Fake.dll'"),
                (notAZip, "the archive could not be unpacked")
            ];

            using (Assert.Multiple())
            {
                foreach ((byte[] zip, string reason) in cases)
                {
                    FakeFeeds.Entry entry = new("1.0.1", zip);
                    FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };
                    StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));
                    await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                    await Assert.That(result.Detail).Contains(reason);
                    await Assert.That(NothingStaged(root)).IsTrue();
                }
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_Untrusted_RefusedAndTheStagingDirectoryRemoved()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };
            RecordingTrust trust = new(false);
            ExtensionUpdateService service = NewService(root, client, trust);

            StageResult result = await service.DownloadAndStageAsync(Entry(entry));

            string extracted = Path.Combine(root, "extensions", ".staging", Id, "1.0.1");
            using (Assert.Multiple())
            {
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(result.Detail).IsEqualTo("the copy is not signed by this app's publisher");
                await Assert.That(trust.Asked.Count).IsEqualTo(1);
                await Assert.That(trust.Asked[0].Directory).IsEqualTo(extracted)
                    .Because("the policy judges the unpacked copy before it is installed");
                await Assert.That(trust.Asked[0].Manifest.Version).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(Directory.Exists(extracted)).IsFalse();
                await Assert.That(NothingStaged(root)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_Success_StagesUnderTheVersionFolder_KeepsEveryFile_AndPendingRestartIsTrue()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1", ("Fake.xml", "<doc />"), ("de/", null),
                ("de/Fake.resources.dll", "res"));
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new()
            {
                Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) }
            };
            RecordingTrust trust = new(true);
            ExtensionUpdateService service = NewService(root, client, trust);
            List<ExtensionDownloadProgress> progress = [];

            ExtensionUpdateState before = await service.CheckAsync(Id);
            StageResult result = await service.DownloadAndStageAsync(Entry(entry), new SyncProgress(progress));
            ExtensionUpdateState after = await service.CheckAsync(Id);

            string installed = Path.Combine(root, "extensions", Id, "1.0.1");
            using (Assert.Multiple())
            {
                await Assert.That(before.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(result.Directory).IsEqualTo(installed);
                await Assert.That(Directory.Exists(installed)).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, ExtensionManifest.FileName))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.dll"))).IsTrue();
                await Assert.That(File.Exists(Path.Combine(installed, "extension.sig"))).IsTrue()
                    .Because("the verified signature travels intact");
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.xml"))).IsTrue();
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(installed, "de", "Fake.resources.dll")))
                    .IsEqualTo("res");
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", ".staging", Id))).IsFalse()
                    .Because("the staging folder is gone after the rename");
                await Assert.That(trust.Asked.Single().Directory)
                    .IsEqualTo(Path.Combine(root, "extensions", ".staging", Id, "1.0.1"));
                await Assert.That(progress.Count).IsGreaterThanOrEqualTo(2);
                await Assert.That(progress[^1]).IsEqualTo(new ExtensionDownloadProgress(zip.Length, zip.Length));
                await Assert.That(service.PendingVersion(Id)).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(service.PendingRestart).IsTrue();
                await Assert.That(after.Status).IsEqualTo(ExtensionUpdateStatus.PendingRestart);
                await Assert.That(after.Pending).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(after.Offered).IsNull();
            }

            // The loader finds exactly what was staged.
            ExtensionLoader.Discovery discovered = ExtensionLoader.Discover(Path.Combine(root, "extensions"));
            using (Assert.Multiple())
            {
                await Assert.That(discovered.Candidates.Select(c => c.Directory))
                    .IsEquivalentTo([installed], CollectionOrdering.Matching);
                await Assert.That(discovered.Rejected).IsEmpty();
            }

            // A version newer than the staged one is offered over it.
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(entry, new FakeFeeds.Entry("1.0.2"));
            ExtensionUpdateState newer = await service.CheckAsync(Id);
            using (Assert.Multiple())
            {
                await Assert.That(newer.Status).IsEqualTo(ExtensionUpdateStatus.UpdateAvailable);
                await Assert.That(newer.Offered!.Version).IsEqualTo(SemVersion.Parse("1.0.2"));
                await Assert.That(newer.Pending).IsEqualTo(SemVersion.Parse("1.0.1"));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // End to end: the extension this test process runs, zipped the way the release workflow will and signed
    // with an ephemeral key, installs through SignedTrustPolicy; the same zip with one byte of the DLL
    // changed after signing is refused with the signature check's own reason.
    [Test]
    public async Task Download_SignedRealExtension_Installs_AndATamperedDllIsRefused()
    {
        string root = NewRoot();
        try
        {
            string shippedDll = typeof(StratBookPack).Assembly.Location;
            string shippedManifest = Path.Combine(Path.GetDirectoryName(shippedDll)!, ExtensionManifest.FileName);
            if (!File.Exists(shippedManifest))
            {
                throw new SkipTestException($"no {ExtensionManifest.FileName} beside {shippedDll}");
            }

            ExtensionManifest built = ExtensionManifest.Parse(await File.ReadAllTextAsync(shippedManifest));
            SemVersion next = new(built.Version.Major, built.Version.Minor, built.Version.Patch + 1);
            string json = (await File.ReadAllTextAsync(shippedManifest))
                .Replace($"\"{built.Version}\"", $"\"{next}\"", StringComparison.Ordinal);
            (string publicKey, System.Security.Cryptography.ECDsa privateKey) = NewKeyPair();
            using (privateKey)
            {
                string tree = Path.Combine(root, "tree");
                Directory.CreateDirectory(tree);
                await File.WriteAllTextAsync(Path.Combine(tree, ExtensionManifest.FileName), json);
                File.Copy(shippedDll, Path.Combine(tree, built.Assembly));
                await File.WriteAllTextAsync(Path.Combine(tree, ExtensionSignature.FileName),
                    ExtensionSignature.Sign(tree, privateKey));
                byte[] signed = ZipDirectory(tree);

                byte[] dll = await File.ReadAllBytesAsync(Path.Combine(tree, built.Assembly));
                dll[dll.Length / 2] ^= 0x01;
                await File.WriteAllBytesAsync(Path.Combine(tree, built.Assembly), dll);
                byte[] tampered = ZipDirectory(tree);

                FakeFeeds.Entry good = new(next.ToString(), signed);
                FakeFeedClient client = new() { Zips = { [good.Url] = signed } };
                PackStatus status = Status(built.Id, built.Version.ToString());
                SignedTrustPolicy policy = new([publicKey]);
                ExtensionUpdateService service = new(root, [status], Host, policy, client, FeedUrl);

                StageResult installed =
                    await service.DownloadAndStageAsync(ExtensionFeed.Parse(FakeFeeds.Json(built.Id, good)).Entries
                        .Single());

                string otherRoot = NewRoot();
                try
                {
                    client.Zips[good.Url] = tampered;
                    FakeFeeds.Entry bad = new(next.ToString(), tampered);
                    ExtensionUpdateService again = new(otherRoot, [status], Host, policy, client, FeedUrl);
                    StageResult refused =
                        await again.DownloadAndStageAsync(ExtensionFeed.Parse(FakeFeeds.Json(built.Id, bad)).Entries
                            .Single());

                    using (Assert.Multiple())
                    {
                        await Assert.That(installed.Outcome).IsEqualTo(StageOutcome.Installed);
                        await Assert.That(installed.Directory)
                            .IsEqualTo(Path.Combine(root, "extensions", built.Id, next.ToString()));
                        await Assert.That(File.Exists(Path.Combine(installed.Directory!, ExtensionSignature.FileName)))
                            .IsTrue();
                        await Assert.That(policy.Judge(installed.Directory!, built).Trusted).IsTrue()
                            .Because("what was installed still verifies in place");
                        await Assert.That(refused.Outcome).IsEqualTo(StageOutcome.Refused);
                        await Assert.That(refused.Detail).Contains("a file changed after signing");
                        await Assert.That(NothingStaged(otherRoot)).IsTrue();
                    }
                }
                finally
                {
                    Cleanup(otherRoot);
                }
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_ExistingTarget_IsANoOpSuccess()
    {
        string root = NewRoot();
        try
        {
            string installed = Path.Combine(root, "extensions", Id, "1.0.1");
            Directory.CreateDirectory(installed);
            await File.WriteAllTextAsync(Path.Combine(installed, "marker.txt"), "mine");
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };

            StageResult result = await NewService(root, client).DownloadAndStageAsync(Entry(entry));

            using (Assert.Multiple())
            {
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.AlreadyInstalled);
                await Assert.That(result.Directory).IsEqualTo(installed);
                await Assert.That(client.Downloads).IsEqualTo(0)
                    .Because("nothing is fetched for a version already on disk");
                await Assert.That(await File.ReadAllTextAsync(Path.Combine(installed, "marker.txt"))).IsEqualTo("mine");
                await Assert.That(File.Exists(Path.Combine(installed, "Fake.dll"))).IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Download_CancelledMidway_LeavesNoPartialFiles()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { HalfwayGate = new TaskCompletionSource(), Zips = { [entry.Url] = zip } };
            ExtensionUpdateService service = NewService(root, client);
            using CancellationTokenSource cts = new();

            Task<StageResult> pending = service.DownloadAndStageAsync(Entry(entry), ct: cts.Token);
            await client.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            string part = Path.Combine(root, "extensions", ".staging", Id, "1.0.1.zip.part");
            bool partExistedMidway = File.Exists(part);
            await cts.CancelAsync();
            StageResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

            using (Assert.Multiple())
            {
                await Assert.That(partExistedMidway).IsTrue()
                    .Because("the download was in flight when it was cancelled");
                await Assert.That(result.Outcome).IsEqualTo(StageOutcome.Cancelled);
                await Assert.That(File.Exists(part)).IsFalse();
                await Assert.That(NothingStaged(root)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task EveryStep_RunsThroughTheQueue_AtUserPriority()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new()
            {
                Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) }
            };
            RecordingQueue queue = new();
            ExtensionUpdateService service = NewService(root, client, queue: queue);

            await service.CheckAsync();
            await service.CheckAsync(Id);
            StageResult staged = await service.DownloadAndStageAsync(Entry(entry));
            await service.CleanupOnStartAsync();

            using (Assert.Multiple())
            {
                await Assert.That(staged.Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(queue.Requests.Count).IsEqualTo(4);
                await Assert.That(queue.Requests.Select(r => r.Title)).IsEquivalentTo(
                [
                    "Check for extension updates",
                    "Check for updates: Fake",
                    "Download extension update: Fake 1.0.1",
                    "Tidy staged extension updates"
                ], CollectionOrdering.Matching);
                await Assert.That(queue.Requests.Take(3).All(r => r.Priority == DemoJobPriority.UserRequested)).IsTrue()
                    .Because("the user pressed the button");
                await Assert.That(queue.Requests[3].Priority).IsEqualTo(DemoJobPriority.Background)
                    .Because("the startup sweep asked for nothing");
                await Assert.That(queue.Requests.All(r => r.Kind == QueueJobKind.ExtensionUpdate)).IsTrue();
                await Assert.That(queue.Requests.All(r => r.OwnerTag == ExtensionUpdateService.Owner)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Cleanup at start ─────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Cleanup_RemovesStagingAndSupersededVersions_KeepsTheRunningAndNewerOnes()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string staging = Path.Combine(extensions, ".staging", Id);
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "1.0.1.zip.part"), "half");
            Directory.CreateDirectory(Path.Combine(staging, "1.0.1"));
            await File.WriteAllTextAsync(Path.Combine(staging, "1.0.1", "x"), "x");
            string older = StageVersion(extensions, "0.9.0");
            string running = StageVersion(extensions, "1.0.0");
            string newer = StageVersion(extensions, "1.0.1");
            string incompatible = StageVersion(extensions, "1.2.0", requiresHost: "^3.0");
            string otherPack = StageVersion(extensions, "0.1.0", id: "net.demoviewer.pack.other");
            string stray = Path.Combine(extensions, Id, "not-a-version");
            Directory.CreateDirectory(stray);
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{}");

            ExtensionUpdateService service = NewService(root, new FakeFeedClient(),
                statuses: [Status(version: "1.0.0", source: new PackSource.Staged(running))]);
            service.Cleanup();

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(Path.Combine(extensions, ".staging"))).IsFalse();
                await Assert.That(Directory.Exists(older)).IsFalse().Because("older than the running copy");
                await Assert.That(Directory.Exists(running)).IsTrue().Because("the running copy loaded from here");
                await Assert.That(Directory.Exists(newer)).IsTrue();
                await Assert.That(Directory.Exists(incompatible)).IsTrue().Because("it may load after an app update");
                await Assert.That(Directory.Exists(otherPack)).IsTrue()
                    .Because("not a declared extension; not ours to remove");
                await Assert.That(Directory.Exists(stray)).IsTrue().Because("no manifest, so no version to compare");
                await Assert.That(File.Exists(Path.Combine(root, "settings.json"))).IsTrue();
            }

            // With the bundled copy running at 1.0.0, the equal staged copy is superseded too.
            ExtensionUpdateService bundled =
                NewService(root, new FakeFeedClient(), statuses: [Status(version: "1.0.0")]);
            bundled.Cleanup();
            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(running)).IsFalse();
                await Assert.That(Directory.Exists(newer)).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Installed first-party extensions: the catalogue, the install policy, removal ──────────────

    // The automatic stage covers a shipped extension as well as an installed one: a newer version the
    // install policy accepts is staged for the next start, and the shipped copy keeps running until then.
    [Test]
    public async Task AutoStage_StagesANewerVersionOfAShippedExtension()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { Zips = { [entry.Url] = zip }, Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) } };
            RecordingQueue queue = new();
            ExtensionUpdateService service = NewService(root, client, trust: TrustPolicy.Nothing, queue: queue,
                statuses: [Status(version: "1.0.0", source: PackSource.Bundled)]);

            IReadOnlyList<ExtensionUpdateState> states = await service.AutoStageAsync();

            using (Assert.Multiple())
            {
                await Assert.That(states.Single().Status).IsEqualTo(ExtensionUpdateStatus.PendingRestart);
                await Assert.That(states.Single().Pending).IsEqualTo(SemVersion.Parse("1.0.1"));
                await Assert.That(states.Single().Source).IsEqualTo(PackSource.Bundled);
                await Assert.That(Directory.Exists(Path.Combine(root, "extensions", Id, "1.0.1"))).IsTrue();
                await Assert.That(queue.Requests.Select(r => (r.Title, r.Priority)))
                    .IsEquivalentTo([("Update extensions", DemoJobPriority.Background)]);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // Safe mode leaves every shipped id without a status. The shipped list, not the statuses, is what keeps
    // such an id out of the catalogue and away from Remove, even with its folders on disk.
    [Test]
    public async Task Catalogue_NeverListsOrRemovesAShippedId_EvenWithNoStatusForIt()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.0");
            FakeFeedClient client = new() { Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0", zip)) } };
            string onDisk = StageVersion(Path.Combine(root, "extensions"), "1.0.0");
            ExtensionUpdateService service = NewService(root, client, statuses: [], catalogue: [Id], shippedIds: [Id]);

            IReadOnlyList<InstallableExtension> listed = await service.CheckCatalogueAsync();
            bool removed = await service.RemoveInstalledAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(listed).IsEmpty();
                await Assert.That(client.FeedFetches).IsEqualTo(0);
                await Assert.That(removed).IsFalse();
                await Assert.That(Directory.Exists(onDisk)).IsTrue();
                await Assert.That(File.Exists(ExtensionStaging.RemovalMarkerPath(Path.Combine(root, "extensions"), Id)))
                    .IsFalse();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A refused copy belongs to the catalogue row whose folder it sits in, since that folder is what Remove
    // deletes: a tree of this id moved under another id's folder leaves this row installable, while a tree
    // of another id under this folder makes it NotLoaded.
    [Test]
    public async Task Catalogue_MatchesARefusedCopyByItsFolder_NotByItsManifestId()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.0");
            FakeFeedClient client = new() { Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("1.0.0", zip)) } };
            string extensions = Path.Combine(root, "extensions");
            LoadOutcome underAnotherFolder = new(Path.Combine(extensions, "net.demoviewer.pack.other", "1.0.0"),
                FakeManifests.For(Id, "Fake", "1.0.0"), LoadFailure.FolderMismatch, "the folder is 'net.demoviewer.pack.other'",
                External: true);
            LoadOutcome underThisFolder = new(Path.Combine(extensions, Id, "1.0.0"),
                FakeManifests.For("net.demoviewer.pack.other", "Other", "1.0.0"), LoadFailure.FolderMismatch,
                $"the folder is '{Id}'", External: true);
            Directory.CreateDirectory(underAnotherFolder.Directory);
            Directory.CreateDirectory(underThisFolder.Directory);

            IReadOnlyList<InstallableExtension> elsewhere = await NewService(root, client, statuses: [], catalogue: [Id],
                externalRejected: [underAnotherFolder]).CheckCatalogueAsync();
            IReadOnlyList<InstallableExtension> here = await NewService(root, client, statuses: [], catalogue: [Id],
                externalRejected: [underThisFolder]).CheckCatalogueAsync();

            using (Assert.Multiple())
            {
                await Assert.That(elsewhere.Single().Status).IsEqualTo(InstallableStatus.Available);
                await Assert.That(elsewhere.Single().Rejected).IsNull();
                await Assert.That(here.Single().Status).IsEqualTo(InstallableStatus.NotLoaded);
                await Assert.That(here.Single().Rejected).IsEqualTo(underThisFolder);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The feed URL resolver is injected; one that throws is a feed failure on the row, never an exception out
    // of the queue item, for the update check and the catalogue alike.
    [Test]
    public async Task Check_AndCatalogue_ReportAResolverThatThrows_AsFeedInvalid()
    {
        string root = NewRoot();
        try
        {
            FakeFeedClient client = new();
            Func<string, Uri> broken = _ => throw new InvalidOperationException("no template");
            ExtensionUpdateService declared = new(root, [Status()], Host, TrustAll, client, broken);
            ExtensionUpdateService catalogue = new(root, [], Host, TrustAll, client, broken, catalogue: [Id],
                shippedIds: []);

            ExtensionUpdateState verdict = await declared.CheckAsync(Id);
            InstallableExtension listed = (await catalogue.CheckCatalogueAsync()).Single();

            using (Assert.Multiple())
            {
                await Assert.That(verdict.Status).IsEqualTo(ExtensionUpdateStatus.FeedInvalid);
                await Assert.That(verdict.Error).IsEqualTo("the update feed address could not be resolved");
                await Assert.That(verdict.LogDetail).IsEqualTo("no template");
                await Assert.That(listed.Status).IsEqualTo(InstallableStatus.FeedInvalid);
                await Assert.That(listed.Error).IsEqualTo("the update feed address could not be resolved");
                await Assert.That(listed.LogDetail).IsEqualTo("no template");
                await Assert.That(client.FeedFetches).IsEqualTo(0);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The catalogue lists an id this build neither ships nor has loaded; a declared status hides it; a copy
    // on disk is reported as pending or, when this launch refused it, as not loaded with the reason.
    [Test]
    public async Task Catalogue_ListsAnAbsentFirstPartyExtension_NotAShippedOne_AndReportsARejectedCopyAsNotLoaded()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.0");
            FakeFeeds.Entry entry = new("1.0.0", zip);
            FakeFeedClient client = new() { Feeds = { [FeedUrl(Id)] = FakeFeeds.Json(entry) } };

            IReadOnlyList<InstallableExtension> absent =
                await NewService(root, client, statuses: [], catalogue: [Id]).CheckCatalogueAsync();
            IReadOnlyList<InstallableExtension> shipped =
                await NewService(root, client, statuses: [Status()], catalogue: [Id]).CheckCatalogueAsync();

            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(new FakeFeeds.Entry("2.0.0", RequiresHost: "^3.0"));
            IReadOnlyList<InstallableExtension> tooNew =
                await NewService(root, client, statuses: [], catalogue: [Id]).CheckCatalogueAsync();

            client.FeedFailure = new HttpRequestException("404", null, HttpStatusCode.NotFound);
            IReadOnlyList<InstallableExtension> unpublished =
                await NewService(root, client, statuses: [], catalogue: [Id]).CheckCatalogueAsync();
            client.FeedFailure = null;

            string onDisk = StageVersion(Path.Combine(root, "extensions"), "1.0.0");
            client.Feeds[FeedUrl(Id)] = FakeFeeds.Json(entry);
            IReadOnlyList<InstallableExtension> pending =
                await NewService(root, client, statuses: [], catalogue: [Id]).CheckCatalogueAsync();
            LoadOutcome refused = new(onDisk, FakeManifests.For(Id, "Fake", "1.0.0"), LoadFailure.ReservedId,
                "the copy is not signed", External: true);
            ExtensionUpdateService rejecting = NewService(root, client, statuses: [], catalogue: [Id],
                externalRejected: [refused]);
            IReadOnlyList<InstallableExtension> notLoaded = await rejecting.CheckCatalogueAsync();

            using (Assert.Multiple())
            {
                InstallableExtension available = absent.Single();
                await Assert.That(available.Status).IsEqualTo(InstallableStatus.Available);
                await Assert.That(available.Id).IsEqualTo(Id);
                await Assert.That(available.Name).IsEqualTo("Fake");
                await Assert.That(available.Offered!.Version).IsEqualTo(SemVersion.Parse("1.0.0"));
                await Assert.That(available.Offered.Size).IsEqualTo(zip.LongLength);
                await Assert.That(available.Staged).IsNull();
                await Assert.That(available.Rejected).IsNull();

                await Assert.That(shipped).IsEmpty().Because("a declared extension has its own row");

                await Assert.That(tooNew.Single().Status).IsEqualTo(InstallableStatus.NeedsNewerApp);
                await Assert.That(tooNew.Single().LatestCompatibility).IsTypeOf<PackCompatibility.HostContractMismatch>();

                await Assert.That(unpublished.Single().Status).IsEqualTo(InstallableStatus.NotPublished);

                await Assert.That(pending.Single().Status).IsEqualTo(InstallableStatus.PendingRestart);
                await Assert.That(pending.Single().Staged).IsEqualTo(SemVersion.Parse("1.0.0"));

                await Assert.That(notLoaded.Single().Status).IsEqualTo(InstallableStatus.NotLoaded);
                await Assert.That(notLoaded.Single().Rejected).IsEqualTo(refused);
                await Assert.That(notLoaded.Single().Name).IsEqualTo("Fake");
                await Assert.That(rejecting.LastCatalogue).IsEqualTo(notLoaded);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // A reserved id this build does not ship is judged by the install policy, never by the shipped one,
    // whatever the user allowed; a declared shipped pack's update keeps the shipped policy.
    [Test]
    public async Task Download_UndeclaredReservedId_IsJudgedByInstallTrust_NotTheShippedPolicy()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { Zips = { [entry.Url] = zip } };
            RecordingTrust install = new(false);

            StageResult undeclared = await NewService(root, client, TrustAll, statuses: [], installTrust: install)
                .DownloadAndStageAsync(Entry(entry));
            RecordingTrust untouched = new(false);
            StageResult declared = await NewService(root, client, TrustAll, statuses: [Status()],
                installTrust: untouched).DownloadAndStageAsync(Entry(entry));

            using (Assert.Multiple())
            {
                await Assert.That(undeclared.Outcome).IsEqualTo(StageOutcome.Refused);
                await Assert.That(undeclared.Detail).IsEqualTo("the copy is not signed by this app's publisher");
                await Assert.That(install.Asked.Count).IsEqualTo(1);
                await Assert.That(declared.Outcome).IsEqualTo(StageOutcome.Installed)
                    .Because("an update of a shipped pack is the shipped policy's to judge");
                await Assert.That(untouched.Asked).IsEmpty();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // The launch auto-stage and a user's Update can ask for one entry at once; the second waits for the
    // first and finds the folder, never its half-written staging files.
    [Test]
    public async Task TwoConcurrentStagesOfOneEntry_OneInstalls_OneReportsAlreadyInstalled_NothingLeftInStaging()
    {
        string root = NewRoot();
        try
        {
            byte[] zip = ExtensionZip(Id, "1.0.1");
            FakeFeeds.Entry entry = new("1.0.1", zip);
            FakeFeedClient client = new() { HalfwayGate = new TaskCompletionSource(), Zips = { [entry.Url] = zip } };
            ExtensionUpdateService service = NewService(root, client);

            Task<StageResult> first = service.DownloadAndStageAsync(Entry(entry));
            await client.Halfway.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            Task<StageResult> second = service.DownloadAndStageAsync(Entry(entry));
            await Task.Delay(100);
            bool secondWaited = !second.IsCompleted;
            client.HalfwayGate.SetResult();
            StageResult[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

            using (Assert.Multiple())
            {
                await Assert.That(secondWaited).IsTrue().Because("stages run one at a time");
                await Assert.That(results[0].Outcome).IsEqualTo(StageOutcome.Installed);
                await Assert.That(results[1].Outcome).IsEqualTo(StageOutcome.AlreadyInstalled);
                await Assert.That(client.Downloads).IsEqualTo(1);
                await Assert.That(Directory.EnumerateFiles(Path.Combine(root, "extensions", ".staging"), "*",
                    SearchOption.AllDirectories)).IsEmpty();
                await Assert.That(File.Exists(Path.Combine(root, "extensions", Id, "1.0.1", "Fake.dll"))).IsTrue();
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task RemoveInstalled_MarksAnInstalledSource_DeletesAtOnceWhenNothingIsLoaded_RefusesAShippedId()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string loaded = StageVersion(extensions, "1.0.0");
            string marker = ExtensionStaging.RemovalMarkerPath(extensions, Id);
            FakeFeedClient client = new();

            bool marked = await NewService(root, client, statuses: [InstalledStatus(Id, "1.0.0", loaded)])
                .RemoveInstalledAsync(Id);
            bool markerWritten = File.Exists(marker);
            bool stillListed = ExtensionLoader.Discover(extensions).Candidates.Any(c => c.Directory == loaded);
            bool noRejection = ExtensionLoader.Discover(extensions).Rejected.Count == 0;
            File.Delete(marker);

            bool shipped = await NewService(root, client, statuses: [Status()]).RemoveInstalledAsync(Id);
            bool shippedUntouched = !File.Exists(marker) && Directory.Exists(loaded);

            bool absent = await NewService(root, client, statuses: [], catalogue: ["net.demoviewer.pack.other"])
                .RemoveInstalledAsync("net.demoviewer.pack.other");

            bool unloaded = await NewService(root, client, statuses: [], catalogue: [Id]).RemoveInstalledAsync(Id);

            using (Assert.Multiple())
            {
                await Assert.That(marked).IsTrue();
                await Assert.That(markerWritten).IsTrue();
                await Assert.That(stillListed).IsTrue().Because("the loaded copy stays until the restart");
                await Assert.That(noRejection).IsTrue().Because("the marker is a file, invisible to discovery");
                await Assert.That(shipped).IsFalse().Because("a shipped extension is turned off, never removed");
                await Assert.That(shippedUntouched).IsTrue();
                await Assert.That(absent).IsFalse().Because("nothing on disk to remove");
                await Assert.That(unloaded).IsTrue();
                await Assert.That(Directory.Exists(Path.Combine(extensions, Id))).IsFalse()
                    .Because("nothing of the id is loaded, so the folder goes at once");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Test]
    public async Task Cleanup_KeepsTheRunningInstalledDirectory_AndLeavesPendingRemovalsToTheNextStart()
    {
        string root = NewRoot();
        try
        {
            string extensions = Path.Combine(root, "extensions");
            string older = StageVersion(extensions, "0.9.0");
            string running = StageVersion(extensions, "1.0.0");
            string newer = StageVersion(extensions, "1.0.1");
            string marked = StageVersion(extensions, "0.1.0", id: "net.demoviewer.pack.other");
            ExtensionStaging.MarkForRemoval(extensions, "net.demoviewer.pack.other");

            NewService(root, new FakeFeedClient(), statuses: [InstalledStatus(Id, "1.0.0", running)]).Cleanup();

            using (Assert.Multiple())
            {
                await Assert.That(Directory.Exists(older)).IsFalse().Because("superseded by the running copy");
                await Assert.That(Directory.Exists(running)).IsTrue().Because("the running copy loaded from here");
                await Assert.That(Directory.Exists(newer)).IsTrue();
                await Assert.That(Directory.Exists(marked)).IsTrue()
                    .Because("the sweep runs after extensions loaded; marked folders go at the next start");
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static ExtensionUpdateService NewService(string root, FakeFeedClient client, ITrustPolicy? trust = null,
        IDemoProcessingQueue? queue = null, IReadOnlyList<PackStatus>? statuses = null,
        Func<ExtensionFeedEntry, ExtensionHostInfo, bool>? isOffered = null, ITrustPolicy? installTrust = null,
        IReadOnlyList<string>? catalogue = null, IReadOnlyList<LoadOutcome>? externalRejected = null,
        IReadOnlyList<string>? shippedIds = null) =>
        new(root, statuses ?? [Status()], Host, trust ?? TrustAll, client, FeedUrl, queue, isOffered,
            installTrust ?? TrustAll, catalogue ?? [], externalRejected ?? [], shippedIds ?? []);

    private static ExtensionFeedEntry Entry(FakeFeeds.Entry e) =>
        ExtensionFeed.Parse(FakeFeeds.Json(e)).Entries.Single();

    // The same ephemeral P-256 pair ExtensionSignatureTests generates: the public half as base64 SPKI.
    private static (string PublicKey, System.Security.Cryptography.ECDsa PrivateKey) NewKeyPair()
    {
        System.Security.Cryptography.ECDsa key =
            System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), key);
    }

    private static string StageVersion(string extensions, string version, string id = Id, string requiresHost = "^2.0")
    {
        string dir = Path.Combine(extensions, id, version);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ExtensionManifest.FileName),
            FakeFeeds.Manifest(id, new FakeFeeds.Entry(version, RequiresHost: requiresHost)));
        File.WriteAllText(Path.Combine(dir, "Fake.dll"), "x");
        return dir;
    }

    // No file anywhere under extensions/: a refusal or a cancellation leaves at most empty folders.
    private static bool NothingStaged(string root)
    {
        string extensions = Path.Combine(root, "extensions");
        return !Directory.Exists(extensions)
            || !Directory.EnumerateFiles(extensions, "*", SearchOption.AllDirectories).Any();
    }

    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dvupd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // best-effort
        }
    }

    // Progress<T> posts through the captured SynchronizationContext; this records inline.
    private sealed class SyncProgress(List<ExtensionDownloadProgress> sink) : IProgress<ExtensionDownloadProgress>
    {
        public void Report(ExtensionDownloadProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}
