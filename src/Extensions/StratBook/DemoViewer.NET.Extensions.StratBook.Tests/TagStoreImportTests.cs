#region

using System.Text.Json;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using DemoViewer.NET.Playback2D.Annotations;
using static DemoViewer.NET.AppTests.TagTestData;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Library Transfer's tag import: the three conflict policies on the document and the verdicts, a demo
///     open in the tagger skipped, a file naming another demo refused, and a second import of the same
///     bundle changing nothing.
/// </summary>
[NotInParallel]
public class TagStoreImportTests
{
    private const string ProposalA = "a|1|100";
    private const string ProposalB = "b|2|200";
    private const string ProposalC = "c|3|300";

    private static SuggestionVerdict Verdict(string verdict) =>
        new() { Verdict = verdict, At = Created, Detector = "exec", Side = 3, TriggerTick = 100, FrameCount = 10 };

    // A machine's tags store: a document with the given instances and a verdict per proposal.
    private static TagStore Machine(string root, TagInstance[] instances, params string[] proposals)
    {
        TagStore store = new(PackStores.At(root));
        store.Save(Document(Sha, [.. instances.Select(i => i.Clone())]));
        foreach (string proposal in proposals)
        {
            store.RecordVerdict(Sha, proposal, Verdict(SuggestionVerdicts.Rejected));
        }

        return store;
    }

    private static string SidecarText(string root) => File.ReadAllText(TagStore.SidecarPathFor(root, Sha));

    private static string VerdictsText(string root) =>
        File.ReadAllText(Path.Combine(root, "verdicts", Sha + ".verdicts.json"));

    private static async Task WithRoots(Func<string, string, Task> body)
    {
        string local = TempRoot();
        string staged = TempRoot();
        try
        {
            await body(local, staged);
        }
        finally
        {
            DeleteQuietly(local);
            DeleteQuietly(staged);
        }
    }

    [Test]
    public Task KeepMine_KeepsTheLocalFilesWhole() => WithRoots(async (local, staged) =>
    {
        TagInstance shared = Instance("A execute", 100, 200);
        TagStore store = Machine(local, [shared], ProposalA);
        Machine(staged, [shared, Instance("Default", 300, 400)], ProposalB);
        string sidecar = SidecarText(local);
        string verdicts = VerdictsText(local);

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.KeepMine);

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.KeptMine);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.KeptMine);
            await Assert.That(result.Notes.Count).IsEqualTo(2);
            await Assert.That(SidecarText(local)).IsEqualTo(sidecar);
            await Assert.That(VerdictsText(local)).IsEqualTo(verdicts);
        }
    });

    [Test]
    public Task KeepMine_WritesTheImportedFiles_WhereThisMachineHasNone() => WithRoots(async (local, staged) =>
    {
        TagStore store = new(PackStores.At(local));
        Machine(staged, [Instance("A execute", 100, 200)], ProposalA);

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.KeepMine);

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Added);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.Added);
            await Assert.That(SidecarText(local)).IsEqualTo(SidecarText(staged));
            await Assert.That(VerdictsText(local)).IsEqualTo(VerdictsText(staged));
            await Assert.That(store.Index.Single().InstanceCount).IsEqualTo(1);
        }

        // The row reaches index.json through the store's own deferred save.
        store.SaveIndex();
        await Assert.That(new TagStore(PackStores.At(local)).Index.Single().Sha256).IsEqualTo(Sha);
    });

    [Test]
    public Task TakeImported_ReplacesTheLocalFiles() => WithRoots(async (local, staged) =>
    {
        TagStore store = Machine(local, [Instance("A execute", 100, 200)], ProposalA);
        Machine(staged, [Instance("Default", 300, 400), Instance("B split", 500, 600)], ProposalB);

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.TakeImported);

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Replaced);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.Replaced);
            await Assert.That(SidecarText(local)).IsEqualTo(SidecarText(staged));
            await Assert.That(VerdictsText(local)).IsEqualTo(VerdictsText(staged));
            await Assert.That(store.Index.Single().InstanceCount).IsEqualTo(2);
        }
    });

    [Test]
    public Task KeepBoth_UnionsInstancesById_AndVerdictsByKey_MineWinningAClash() => WithRoots(async (local, staged) =>
    {
        TagInstance shared = Instance("A execute", 100, 200);
        TagInstance mineOnly = Instance("Default", 300, 400);
        TagInstance theirsOnly = Instance("B split", 500, 600);
        TagStore store = Machine(local, [shared, mineOnly], ProposalA, ProposalB);

        // The same instance edited on the other machine: the clash keeps this machine's copy.
        TagInstance sharedEdited = shared.Clone();
        sharedEdited.Note = "theirs";
        TagStore other = Machine(staged, [sharedEdited, theirsOnly], ProposalC);
        other.RecordVerdict(Sha, ProposalB, Verdict(SuggestionVerdicts.Accepted));

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.KeepBoth);
        TagDocument merged = store.TryLoad(Sha)!;
        SuggestionVerdictDocument verdicts = store.LoadVerdicts(Sha)!;

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Merged);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.Merged);
            await Assert.That(merged.Instances.Select(i => i.Id))
                .IsEquivalentTo([shared.Id, mineOnly.Id, theirsOnly.Id]);
            await Assert.That(merged.Instances.Single(i => i.Id == shared.Id).Note).IsNull();
            await Assert.That(verdicts.Verdicts.Keys).IsEquivalentTo([ProposalA, ProposalB, ProposalC]);
            await Assert.That(verdicts.Verdicts[ProposalB].Verdict).IsEqualTo(SuggestionVerdicts.Rejected);
            await Assert.That(store.Index.Single().InstanceCount).IsEqualTo(3);
        }
    });

    [Test]
    public Task KeepBoth_KeepsMine_WhenTheImportedTicksAreFromAnotherParse() => WithRoots(async (local, staged) =>
    {
        TagStore store = Machine(local, [Instance("A execute", 100, 200)]);
        TagDocument theirs = Document(Sha, Instance("Default", 300, 400));
        theirs.Clock = TagClockHeader.From(Clock with { FrameCount = Clock.FrameCount + 1 });
        new TagStore(PackStores.At(staged)).Save(theirs);
        string before = SidecarText(local);

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.KeepBoth);

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.KeptMine);
            await Assert.That(result.Notes.Single()).Contains("different parse");
            await Assert.That(SidecarText(local)).IsEqualTo(before);
        }
    });

    [Test]
    public Task ADemoOpenInTheTagger_IsSkippedAndReported() => WithRoots(async (local, staged) =>
    {
        TagStore store = Machine(local, [Instance("A execute", 100, 200)], ProposalA);
        Machine(staged, [Instance("Default", 300, 400)], ProposalB);
        string sidecar = SidecarText(local);
        string verdicts = VerdictsText(local);
        using TagSession session = new(store);

        TagImportResult result;
        using (store.CheckOut(Sha, session))
        {
            result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.TakeImported);
        }

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.CheckedOut);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.CheckedOut);
            await Assert.That(result.Notes.Single()).Contains("Round Tagger");
            await Assert.That(SidecarText(local)).IsEqualTo(sidecar);
            await Assert.That(VerdictsText(local)).IsEqualTo(verdicts);
        }
    });

    [Test]
    public Task AFileNamingAnotherDemo_IsRefused_AndNothingIsWritten() => WithRoots(async (local, staged) =>
    {
        TagStore store = new(PackStores.At(local));
        string other = new('a', 64);
        string tags = JsonSerializer.Serialize(Document(other, Instance("A execute", 1, 2)),
            TagJsonContext.Default.TagDocument);
        string verdicts = JsonSerializer.Serialize(
            new SuggestionVerdictDocument { Demo = new TagDemoHeader { Sha256 = other } },
            TagJsonContext.Default.SuggestionVerdictDocument);

        TagImportResult result = store.Import(Sha, tags, verdicts, ImportConflictPolicy.TakeImported);

        using (Assert.Multiple())
        {
            await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Refused);
            await Assert.That(result.Verdicts).IsEqualTo(TagImportOutcome.Refused);
            await Assert.That(result.Notes.Count).IsEqualTo(2);
            await Assert.That(Directory.Exists(local)).IsFalse();
            await Assert.That(store.Index).IsEmpty();
        }
    });

    [Test]
    public async Task AHashThatIsNotOne_IsRefused_BeforeItBecomesAPath()
    {
        TagStore store = new(null);
        string tags = JsonSerializer.Serialize(Document(Sha, Instance("A execute", 1, 2)),
            TagJsonContext.Default.TagDocument);

        TagImportResult result = store.Import("../../" + Sha[6..], tags, null, ImportConflictPolicy.TakeImported);

        await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Refused);
        await Assert.That(store.Index).IsEmpty();
    }

    [Test]
    public Task AnUnreadableLocalFile_IsNeverOverwritten() => WithRoots(async (local, staged) =>
    {
        string path = TagStore.SidecarPathFor(local, Sha);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "{ hand-edited");
        TagStore store = new(PackStores.At(local));
        Machine(staged, [Instance("A execute", 100, 200)]);

        TagImportResult result = store.Import(Sha, PackStores.At(staged), ImportConflictPolicy.TakeImported);

        await Assert.That(result.Tags).IsEqualTo(TagImportOutcome.Refused);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("{ hand-edited");
    });

    [Test]
    [Arguments(ImportConflictPolicy.KeepMine)]
    [Arguments(ImportConflictPolicy.TakeImported)]
    [Arguments(ImportConflictPolicy.KeepBoth)]
    public Task ASecondImportOfTheSameBundle_ChangesNothing(ImportConflictPolicy policy) =>
        WithRoots(async (local, staged) =>
        {
            TagInstance shared = Instance("A execute", 100, 200);
            TagStore store = Machine(local, [shared, Instance("Default", 300, 400)], ProposalA);
            Machine(staged, [shared, Instance("B split", 500, 600)], ProposalB);

            store.Import(Sha, PackStores.At(staged), policy);
            string sidecar = SidecarText(local);
            string verdicts = VerdictsText(local);
            List<string?> changed = [];
            store.Changed += changed.Add;

            TagImportResult again = store.Import(Sha, PackStores.At(staged), policy);

            using (Assert.Multiple())
            {
                await Assert.That(again.Tags).IsEqualTo(policy == ImportConflictPolicy.KeepMine
                    ? TagImportOutcome.KeptMine
                    : TagImportOutcome.Unchanged);
                await Assert.That(again.Verdicts).IsEqualTo(policy == ImportConflictPolicy.KeepMine
                    ? TagImportOutcome.KeptMine
                    : TagImportOutcome.Unchanged);
                await Assert.That(SidecarText(local)).IsEqualTo(sidecar);
                await Assert.That(VerdictsText(local)).IsEqualTo(verdicts);
                await Assert.That(changed).IsEmpty();
            }
        });
}
