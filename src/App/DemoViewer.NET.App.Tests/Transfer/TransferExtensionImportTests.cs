#region

using System.Text;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Transfer;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The extension stores step of an import: a store the extension leaves to the host is merged file by file under
///     each policy, the rest reaches the extension's own merge as read-only views laid out like its stores, a merge
///     that throws is a failed line in the summary, and an extension that is missing or off is skipped and named in
///     the preview.
/// </summary>
public class TransferExtensionImportTests
{
    private const string ExtensionId = "dev.example.transfer";
    private const string Sha = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string PalettesPath = ExtensionId + "-palettes";
    private const string NotesPath = ExtensionId + "-notes";
    private const string TagsPath = ExtensionId + "-tags";

    private static readonly StoreDescriptor _palettes =
        new("palettes", "Palettes", StoreRoot.Config, [PalettesPath], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, PortableExcludes = ["index.json"]
        };

    private static readonly StoreDescriptor _notes =
        new("notes", "Notes", StoreRoot.Config, [NotesPath], IsUserWork: true)
        {
            Portability = StorePortability.LibraryWide, ExtensionMerges = true
        };

    private static readonly StoreDescriptor _tags =
        new("tags", "Tags", StoreRoot.Config, [TagsPath], IsUserWork: true)
        {
            Portability = StorePortability.PerDemo, ExtensionMerges = true,
            DemoKeyedPaths = [TagsPath + "/{sha256}.tag.json"]
        };

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static string Palette(string config, string name) => Path.Combine(config, PalettesPath, name);

    // A bundle with three palettes against this machine's two, an excluded index, a file outside the store, notes
    // and a demo's tags for the extension's own merge, and two own files of which one was declared.
    private static void WriteBundle(string root)
    {
        BundleBuilder bundle = new(root);
        foreach ((string name, string text) in (ValueTuple<string, string>[])
                 [("a.json", "theirs"), ("b.json", "new"), ("c.json", "same"), ("index.json", "derived")])
        {
            bundle.SharedAt(TransferLayout.SharedExtensionStoreFile(ExtensionId, "palettes", PalettesPath + "/" + name),
                Text(text));
        }

        bundle.SharedAt(TransferLayout.SharedExtensionStoreFile(ExtensionId, "palettes", "elsewhere/x.json"),
            Text("outside"));
        bundle.SharedAt(TransferLayout.SharedExtensionStoreFile(ExtensionId, "notes", NotesPath + "/n.json"),
            Text("notes"));
        bundle.SharedAt(TransferLayout.SharedExtensionOwnFile(ExtensionId, StoreRoot.Config, "state.json"),
            Text("state"));
        bundle.SharedAt(TransferLayout.SharedExtensionOwnFile(ExtensionId, StoreRoot.Config, "other.json"),
            Text("undeclared"));
        bundle.Demo(Sha, "match.dem", 4096,
        [
            bundle.File(TransferLayout.ExtensionStoreFile(Sha, ExtensionId, "tags", TagsPath + "/" + Sha + ".tag.json"),
                Text("tags")),
            bundle.File(TransferLayout.ExtensionStoreFile(Sha, ExtensionId, "tags", TagsPath + "/other.tag.json"),
                Text("not this demo's"))
        ]);
        bundle.Write();
    }

    private sealed class Machine : IDisposable
    {
        private readonly TempFolders _temp = new();

        public Machine(Func<IExtensionImport, Task<ExtensionImportResult>>? merge = null, bool installed = true)
        {
            Bundle = _temp.New();
            Config = _temp.New();
            Cache = _temp.New();
            WriteBundle(Bundle);
            Portability = new Merge(merge ?? (_ => Task.FromResult(new ExtensionImportResult(0, 0, 0, 0))));
            Pack = new Pack([_palettes, _notes, _tags], Portability);
            Guard = ExtensionGuard.Standalone(Pack);
            Contributions = new PackContributions(Pack, () => null!, guard: Guard);
            Pack.Contribute(Contributions, new ServiceCollection().BuildServiceProvider());
            Contributions.DataImported(() => Imported++);
            Installed = installed;
            Directory.CreateDirectory(Path.Combine(Config, PalettesPath));
            File.WriteAllText(Palette(Config, "a.json"), "mine");
            File.WriteAllText(Palette(Config, "c.json"), "same");
        }

        public string Bundle { get; }
        public string Config { get; }
        public string Cache { get; }
        public Pack Pack { get; }
        public Merge Portability { get; }
        public ExtensionGuard Guard { get; }
        public PackContributions Contributions { get; }
        public bool Installed { get; }
        public bool Enabled { get; set; } = true;
        public int Imported { get; private set; }

        public TransferImporter Importer() =>
            new(new DemoCacheStore(null), extensions: new TransferExtensionHost(
                () => Installed ? [new HostDataTransfer(Contributions)] : [],
                featureId => Enabled && featureId == Pack.FeatureId, Config, Cache));

        public async Task<(ImportPreview Preview, TransferSummary Summary)> Import(
            ConflictPolicy policy = ConflictPolicy.KeepMine)
        {
            TransferImporter importer = Importer();
            ImportPreview preview = await importer.PreviewAsync(Bundle, CancellationToken.None);
            TransferSummary summary = await importer.RunAsync(ImportRequest.DataOnly(Bundle, policy), preview, null,
                CancellationToken.None);
            return (preview, summary);
        }

        public void Dispose() => _temp.Dispose();
    }

    private static TransferSummaryItem ExtensionLine(TransferSummary summary) =>
        summary.Items.Single(i => i.Sha256 is null);

    [Test]
    [Arguments(ConflictPolicy.KeepMine, "mine", null)]
    [Arguments(ConflictPolicy.TakeImported, "theirs", null)]
    [Arguments(ConflictPolicy.KeepBoth, "mine", "theirs")]
    public async Task AStoreTheHostMerges_CopiesWhatIsNew_SkipsTheSame_AndFollowsThePolicyOnAConflict(
        ConflictPolicy policy, string a, string? beside)
    {
        using Machine machine = new();

        (_, TransferSummary summary) = await machine.Import(policy);

        string besidePath = Palette(machine.Config, "a (imported).json");
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(Palette(machine.Config, "a.json"))).IsEqualTo(a);
            await Assert.That(File.Exists(besidePath) ? File.ReadAllText(besidePath) : null).IsEqualTo(beside);
            await Assert.That(File.ReadAllText(Palette(machine.Config, "b.json"))).IsEqualTo("new");
            await Assert.That(File.ReadAllText(Palette(machine.Config, "c.json"))).IsEqualTo("same");
            await Assert.That(File.Exists(Palette(machine.Config, "index.json"))).IsFalse()
                .Because("the store excludes it");
            await Assert.That(File.Exists(Path.Combine(machine.Config, "elsewhere", "x.json"))).IsFalse()
                .Because("a file outside the store's paths is never written");
            await Assert.That(machine.Imported).IsEqualTo(1);
            await Assert.That(ExtensionLine(summary).Outcome).IsNotEqualTo(TransferOutcome.Failed);
        }

        // Again: nothing new, and keep both finds the copy it made instead of making another.
        (_, TransferSummary again) = await machine.Import(policy);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(Palette(machine.Config, "a (imported 2).json"))).IsFalse();
            await Assert.That(File.ReadAllText(Palette(machine.Config, "a.json"))).IsEqualTo(a);
            await Assert.That(ExtensionLine(again).Reason).Contains("0 added, 0 merged");
        }
    }

    [Test]
    public async Task TheExtensionsMerge_SeesTheBundleAsItsStores_ReadOnly_AndOnlyWhatItDeclared()
    {
        List<string> seen = [];
        Exception? write = null;
        using Machine machine = new(import =>
        {
            IExtensionFolder notes = import.Staged("notes")!;
            IExtensionFolder tags = import.Staged("tags")!;
            IExtensionFolder own = import.StagedOwn(StoreRoot.Config)!;
            seen.Add(Encoding.UTF8.GetString(notes.Read(NotesPath + "/n.json")!));
            seen.Add(Encoding.UTF8.GetString(tags.Read(TagsPath + "/" + Sha + ".tag.json")!));
            seen.Add(Encoding.UTF8.GetString(own.Read("state.json")!));
            seen.AddRange(tags.EnumerateFiles(TagsPath, "*.json").Select(f => Path.GetFileName(f)));
            seen.AddRange(notes.EnumerateDirectories("").Select(f => Path.GetFileName(f)));
            seen.Add($"other own:{own.FileExists("other.json")}");
            seen.Add($"palettes:{import.Staged("palettes") is null}");
            seen.Add($"cache:{import.StagedOwn(StoreRoot.Cache) is null}");
            seen.Add($"policy:{import.Policy}");
            seen.Add($"demos:{string.Join(",", import.DemoSha256s)}");
            try
            {
                notes.WriteAtomic(NotesPath + "/n.json", "x"u8);
            }
            catch (InvalidOperationException e)
            {
                write = e;
            }

            import.Report("merged the notes");
            return Task.FromResult(new ExtensionImportResult(2, 1, 0, 0));
        });

        (_, TransferSummary summary) = await machine.Import(ConflictPolicy.TakeImported);

        TransferSummaryItem line = ExtensionLine(summary);
        using (Assert.Multiple())
        {
            await Assert.That(seen).IsEquivalentTo(
            [
                "notes", "tags", "state", Sha + ".tag.json", NotesPath, "other own:False", "palettes:True",
                "cache:True", "policy:TakeImported", "demos:" + Sha
            ]).Because("a tag file not named for its demo is not the store's, and an own file not declared stays out");
            await Assert.That(write).IsNotNull();
            await Assert.That(line.Outcome).IsEqualTo(TransferOutcome.Merged);
            await Assert.That(line.Reason).Contains("merged the notes");
            await Assert.That(machine.Imported).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AMergeThatThrows_IsAFailedLine_AndTheHostsOwnMergeStillLands()
    {
        using Machine machine = new(_ => throw new InvalidOperationException("notes are corrupt"));

        (_, TransferSummary summary) = await machine.Import();

        TransferSummaryItem line = ExtensionLine(summary);
        using (Assert.Multiple())
        {
            await Assert.That(line.Outcome).IsEqualTo(TransferOutcome.Failed);
            await Assert.That(line.Reason).Contains("notes are corrupt");
            await Assert.That(machine.Guard.Faults.StateOf(machine.Pack.FeatureId).Count).IsEqualTo(1)
                .Because("the throw is reported against the extension");
            await Assert.That(File.ReadAllText(Palette(machine.Config, "b.json"))).IsEqualTo("new");
            await Assert.That(machine.Imported).IsEqualTo(1)
                .Because("the stores re-read whatever the merge wrote before it stopped");
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnExtensionSwitchedOffOrMissing_IsSkipped_AndThePreviewSaysSo(bool installed)
    {
        bool merged = false;
        using Machine machine = new(_ =>
        {
            merged = true;
            return Task.FromResult(new ExtensionImportResult(1, 0, 0, 0));
        }, installed) { Enabled = false };

        (ImportPreview preview, TransferSummary summary) = await machine.Import();

        using (Assert.Multiple())
        {
            await Assert.That(preview.Warnings.Count(w => w.Kind == TransferWarningKind.ExtensionUnavailable))
                .IsEqualTo(1);
            await Assert.That(preview.CanRun).IsTrue().Because("the rest of the bundle still imports");
            await Assert.That(ExtensionLine(summary).Outcome).IsEqualTo(TransferOutcome.Skipped);
            await Assert.That(merged).IsFalse();
            await Assert.That(File.Exists(Palette(machine.Config, "b.json"))).IsFalse();
            await Assert.That(machine.Imported).IsEqualTo(0);
        }
    }

    internal sealed class Merge(Func<IExtensionImport, Task<ExtensionImportResult>> merge)
        : IExtensionDataPortability
    {
        public string FeatureId => "pack." + ExtensionId;

        public IReadOnlyList<PortableOwnPath> OwnPaths => [new(StoreRoot.Config, "state.json")];

        public Task<ExtensionImportResult> MergeAsync(IExtensionImport import, CancellationToken ct) => merge(import);
    }

    internal sealed class Pack(IEnumerable<StoreDescriptor> stores, IExtensionDataPortability? portability)
        : IExtension
    {
        public string Id => ExtensionId;
        public string FeatureId => "pack." + ExtensionId;
        public IEnumerable<ExtensionFeature> Features => [];
        public IEnumerable<StoreDescriptor> Stores => stores;

        public void Register(IServiceCollection services)
        {
        }

        public void Contribute(IExtensionContributions contributions, IServiceProvider services)
        {
            if (portability is not null)
            {
                contributions.DataPortability(portability);
            }
        }
    }
}
