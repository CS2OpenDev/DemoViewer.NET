#region

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DemoViewer.NET.Services.Transfer;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The bundle manifest round-trips, and a strict read refuses anything a damaged, foreign or hostile bundle
///     could carry: a wrong format id, an unknown or missing member, a malformed hash, a path that escapes its
///     folder, two paths that differ only by case. The version check is exact.
/// </summary>
public class TransferManifestTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ShaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ShaC = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    private static TransferManifest Sample() => new()
    {
        FormatId = TransferManifest.FormatIdValue,
        AppVersion = "0.10.1+abc",
        CreatedUtc = new DateTime(2026, 11, 11, 15, 30, 0, DateTimeKind.Utc),
        SourceOs = "Microsoft Windows 10.0.26300",
        Options = new TransferManifestOptions(true, true, false),
        RulesetIdentities = ["core@1"],
        Extensions = [new TransferExtensionVersion("net.cs2opendev.stratbook", "1.2.0")],
        Demos =
        [
            new TransferDemo(ShaA, "navi vs faze.dem", 1000,
                [
                    new TransferFile(TransferLayout.CacheFile(ShaA, ShaA + ".json.gz"), 10, ShaB),
                    new TransferFile(TransferLayout.ExtensionFacetFile(ShaA, "net.ext", "facet", ShaA + ".json.gz"),
                        20, ShaC),
                    new TransferFile(TransferLayout.AnnotationsFile(ShaA), 5, ShaB)
                ],
                new TransferFingerprint(1000, 262144, ShaB, ShaC), "de_mirage",
                new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), new TransferDemFile(TransferLayout.DemFile(ShaA), 1000)),
            new TransferDemo(ShaB, "plain.dem", 2000, [])
        ],
        Shared = [new TransferFile(TransferLayout.SharedFile(TransferLayout.GraphBreakpointsFileName), 3, ShaA)]
    };

    private static JsonObject SampleJson() => JsonNode.Parse(Sample().ToJsonBytes())!.AsObject();

    private static string RefusalOf(JsonNode json)
    {
        try
        {
            TransferManifest.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
            return "";
        }
        catch (TransferManifestException ex)
        {
            return ex.Message;
        }
    }

    [Test]
    public async Task AManifest_RoundTripsThroughTheBundleFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvtransfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TransferManifest written = Sample();
            written.Write(root);
            TransferManifest read = TransferManifest.Read(root);

            using (Assert.Multiple())
            {
                await Assert.That(read.ToJsonBytes()).IsEquivalentTo(written.ToJsonBytes());
                await Assert.That(read.Demos[0].Fingerprint).IsEqualTo(written.Demos[0].Fingerprint);
                await Assert.That(read.Demos[1].Dem).IsNull();
                await Assert.That(read.CreatedUtc.Kind).IsEqualTo(DateTimeKind.Utc);
                await Assert.That(Directory.GetFiles(root).Select(f => Path.GetFileName(f)!))
                    .IsEquivalentTo([TransferLayout.ManifestFileName]);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheJson_IsCamelCase()
    {
        string json = Encoding.UTF8.GetString(Sample().ToJsonBytes());
        using (Assert.Multiple())
        {
            await Assert.That(json).Contains("\"formatId\": \"dv-transfer\"");
            await Assert.That(json).Contains("\"originalFileName\"");
            await Assert.That(json).Contains("\"windowBytes\"");
        }
    }

    [Test]
    public async Task AFolderWithNoManifest_IsRefusedAsIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), "dvtransfer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Assert.That(() => TransferManifest.Read(root)).Throws<TransferManifestException>()
                .WithMessageContaining("not a complete transfer bundle");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task TheVersionCheck_IsExact()
    {
        TransferManifest manifest = Sample();
        using (Assert.Multiple())
        {
            await Assert.That(TransferManifest.CheckAppVersion(manifest, "0.10.1+abc")).IsNull();
            await Assert.That(TransferManifest.CheckAppVersion(manifest, "0.10.1")).IsNotNull();
            await Assert.That(TransferManifest.CheckAppVersion(manifest, "0.10.2+abc")).Contains("0.10.1+abc");
            await Assert.That(TransferManifest.CheckAppVersion(manifest with { AppVersion = TransferManifest.CurrentAppVersion }))
                .IsNull();
        }
    }

    [Test]
    public async Task AWrongFormatId_IsRefused()
    {
        JsonObject json = SampleJson();
        json["formatId"] = "dv-something-else";
        await Assert.That(RefusalOf(json)).Contains("formatId");
    }

    [Test]
    public async Task AnUnknownMember_IsRefused()
    {
        JsonObject json = SampleJson();
        json["schemaVersion"] = 2;
        await Assert.That(RefusalOf(json)).Contains("damaged");
    }

    [Test]
    public async Task AMissingRequiredMember_IsRefused()
    {
        JsonObject json = SampleJson();
        json.Remove("demos");
        await Assert.That(RefusalOf(json)).Contains("damaged");
    }

    [Test]
    public async Task ANullWhereNoneIsAllowed_IsRefused()
    {
        JsonObject json = SampleJson();
        json["demos"]![0]!["originalFileName"] = null;
        await Assert.That(RefusalOf(json)).Contains("damaged");
    }

    [Test]
    [Arguments("data/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/../../../evil.json")]
    [Arguments("/etc/evil.json")]
    [Arguments("C:/Windows/evil.json")]
    [Arguments("data\\aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\\cache\\x.json")]
    [Arguments("data/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/cache/x.json")]
    [Arguments("shared/x.json")]
    [Arguments("")]
    public async Task AHostileDemoFilePath_IsRefused(string path)
    {
        JsonObject json = SampleJson();
        json["demos"]![0]!["files"]![0]!["path"] = path;
        await Assert.That(RefusalOf(json)).IsNotEmpty();
    }

    [Test]
    public async Task TwoPathsDifferingOnlyByCase_AreRefused()
    {
        JsonObject json = SampleJson();
        string first = (string)json["demos"]![0]!["files"]![0]!["path"]!;
        string upper = first[..^"json.gz".Length] + "JSON.GZ";
        json["demos"]![0]!["files"]![1]!["path"] = upper;
        await Assert.That(RefusalOf(json)).Contains("differing only by case");
    }

    [Test]
    [Arguments("sha256", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [Arguments("sha256", "../x")]
    [Arguments("sizeBytes", "-1")]
    public async Task AMalformedDemo_IsRefused(string member, string value)
    {
        JsonObject json = SampleJson();
        json["demos"]![0]![member] = member == "sizeBytes" ? JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture)) : value;
        await Assert.That(RefusalOf(json)).IsNotEmpty();
    }

    [Test]
    public async Task ADemListedAtAnotherPath_IsRefused()
    {
        JsonObject json = SampleJson();
        json["demos"]![0]!["dem"]!["path"] = TransferLayout.DemFile(ShaB);
        await Assert.That(RefusalOf(json)).Contains(".dem");
    }

    [Test]
    public async Task ADemoListedTwice_IsRefused()
    {
        JsonObject json = SampleJson();
        json["demos"]![1]!["sha256"] = ShaA;
        await Assert.That(RefusalOf(json)).Contains("listed twice");
    }

    [Test]
    public async Task TheLayout_BuildsPortablePaths_AndRefusesAnythingElse()
    {
        using (Assert.Multiple())
        {
            await Assert.That(TransferLayout.CacheFile(ShaA, ShaA + ".facts.core.json.gz"))
                .IsEqualTo($"data/{ShaA}/cache/{ShaA}.facts.core.json.gz");
            await Assert.That(TransferLayout.ExtensionStoreFile(ShaA, "net.ext", "tags\\demos/x.dvtag.json"))
                .IsEqualTo($"data/{ShaA}/ext/net.ext/stores/tags/demos/x.dvtag.json");
            await Assert.That(TransferLayout.DemFile(ShaA)).IsEqualTo($"dem/{ShaA}.dem");
            await Assert.That(TransferLayout.BundleFolderName(new DateTime(2026, 11, 11, 15, 30, 59)))
                .IsEqualTo("DemoViewer Transfer 2026-11-11 1530");
            await Assert.That(() => TransferLayout.CacheFile("ABC", "x")).Throws<ArgumentException>();
            await Assert.That(() => TransferLayout.CacheFile(ShaA, "../x")).Throws<ArgumentException>();
            await Assert.That(() => TransferLayout.CacheFile(ShaA, "naïve.json")).Throws<ArgumentException>();
            await Assert.That(() => TransferLayout.ExtensionStoreFile(ShaA, "net.ext", "a/../b"))
                .Throws<ArgumentException>();
            await Assert.That(() => TransferLayout.FullPath(Path.GetTempPath(), "../x")).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task ANewBundleFolder_SkipsOneAlreadyThere()
    {
        string parent = Path.Combine(Path.GetTempPath(), "dvtransfer_" + Guid.NewGuid().ToString("N"));
        DateTime at = new(2026, 11, 11, 15, 30, 0);
        Directory.CreateDirectory(Path.Combine(parent, TransferLayout.BundleFolderName(at)));
        try
        {
            await Assert.That(Path.GetFileName(TransferLayout.NewBundleFolder(parent, at)))
                .IsEqualTo("DemoViewer Transfer 2026-11-11 1530 (2)");
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }
}
