#region

using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.StratBook.Modules.SuggestedTags;
using DemoViewer.NET.Extensions.StratBook.Services.Tags;
using static DemoViewer.NET.AppTests.SuggestedTagsReviewHarness;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Suggested Tags below the UI: the evaluator
///     writing proposals and stamping the record, <c>Wants</c> following the fingerprint, the gate and the
///     opt-in, accept, edit and reject writing the Tag Store with provenance and the verdicts file, a tuning
///     pass that cannot bring a rejection back, the re-match after a re-parse, and the in-memory mode.
/// </summary>
public class SuggestedTagsReviewTests
{
    [Test]
    public async Task Evaluate_WritesTheProposals_AndStampsTheRecordLast()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        ProposalDocument? document = h.Proposals.TryRead(DemoPath);
        await Assert.That(document).IsNotNull();
        using (Assert.Multiple())
        {
            await Assert.That(document!.OccupancySource).IsEqualTo(ProposalDocument.FromWalk);
            await Assert.That(document.Demo.Sha256).IsEqualTo(Sha);
            await Assert.That(document.Demo.StableKey).IsNull().Because("hashed: no path key in a file that travels");
            await Assert.That(document.Clock.FrameCount).IsEqualTo(2);
            await Assert.That(document.DetectorSet.Fingerprint)
                .IsEqualTo(h.Service.FingerprintFor(SuggestedTagsTestData.Map));
            await Assert.That(document.DetectorSet.Regions).IsEqualTo("learned:1");
            await Assert.That(document.Proposals.Select(p => p.Id)).Contains(ExecuteId);
            await Assert.That(document.Proposals.Single(p => p.Id == ExecuteId).RoundStartTick).IsEqualTo(FreezeEnd);
            await Assert.That(h.Proposals.Stamp(DemoPath)!.Fingerprint).IsEqualTo(document.DetectorSet.Fingerprint);
            await Assert.That(h.Proposals.Stamp(DemoPath)!.Count).IsEqualTo(document.Proposals.Count)
                .Because("nothing has a verdict yet, and the index mirrors the pending count");
            await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the stamp is current");
        }
    }

    [Test]
    [Arguments("{\"schemaVersion\":1,\"proposals\":null}")]
    [Arguments("{\"schemaVersion\":1,\"demo\":null}")]
    [Arguments("{\"schemaVersion\":1,\"clock\":null}")]
    [Arguments("{\"schemaVersion\":1,\"proposals\":[null]}")]
    [Arguments("{\"schemaVersion\":1,\"proposals\":[{\"factors\":null}]}")]
    [Arguments("{\"schemaVersion\":1,\"proposals\":[{\"evidence\":[null]}]}")]
    public async Task AStoredDocumentWithANullWhereTheReadersLook_ReadsAsAbsent(string json)
    {
        using SuggestedTagsReviewHarness h = new();
        h.Seed();
        h.Proposals.Data.Write(DemoPath,
            new DemoDataWrite(ProposalStore.Facet, ProposalStore.Schema, "bundle",
                System.Text.Encoding.UTF8.GetBytes(json)));

        using (Assert.Multiple())
        {
            await Assert.That(ProposalDocument.TryDeserialize(json)).IsNull();
            await Assert.That(h.Service.Load(DemoPath).Entries).IsEmpty()
                .Because("a file that does not read is absent, and the queue opens empty");
        }
    }

    [Test]
    [Arguments("{\"schemaVersion\":1,\"clock\":null}")]
    [Arguments("{\"schemaVersion\":1,\"events\":null}")]
    [Arguments("{\"schemaVersion\":1,\"events\":[null]}")]
    public async Task AStoredDetonationDocumentWithANull_ReadsAsAbsent(string json)
    {
        using SuggestedTagsReviewHarness h = new();
        h.Seed();
        DetonationStore store = new(h.Proposals.Data);
        store.Write(DemoPath, new DetonationDocument());
        h.Proposals.Data.Write(DemoPath,
            new DemoDataWrite(DetonationStore.Facet, DetonationDocument.CurrentSchemaVersion,
                store.Stamp(DemoPath)!.Fingerprint, System.Text.Encoding.UTF8.GetBytes(json)));

        await Assert.That(store.TryRead(DemoPath)).IsNull();
    }

    [Test]
    public async Task Wants_FollowsTheGate_TheOptIn_TheRows_AndAForcedRequest()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Seed();

        h.Background = false;
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the library sweep is off");
        await Assert.That(h.Service.PendingPaths()).IsEmpty();

        h.Service.Request(DemoPath);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue().Because("a forced request runs whatever the opt-in");

        h.Enabled = false;
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse().Because("the gate off stops the evaluator");

        h.Enabled = true;
        h.Background = true;
        h.Cache.Upsert(RoundIndexTestData.ParsedRecord(DemoPath, SuggestedTagsTestData.Map, Sha));
        await Assert.That(h.Service.Wants(DemoPath)).IsFalse()
            .Because("without Round Facts rows nothing can be detected");
        await Assert.That(h.Service.WantsAfterUpstream(DemoPath)).IsTrue()
            .Because("the proposals follow Round Facts on the same visit");
        await Assert.That(h.Service.CanDetect(DemoPath)).IsFalse();

        h.Background = false;
        await Assert.That(h.Service.WantsAfterUpstream(DemoPath)).IsTrue()
            .Because("the forced request above still stands with the sweep off");
        h.Seed();
        await Assert.That(h.Service.WantsAfterUpstream(DemoPath)).IsFalse()
            .Because("Wants answers once the rows are there");
    }

    [Test]
    public async Task TheOpenDemo_IsBuiltOnItsOwnParse_WithTheSweepOff()
    {
        using SuggestedTagsReviewHarness h = new();
        string? open = null;
        SuggestedTagsService service = new(h.Cache.Library(), h.Cache.RoundFacts(), h.Proposals, h.Tags, h.Regions,
            () => DetectorProfile.Default,
            () => true, () => false, openDemo: () => open, walk: _ => Walk(), utcNow: () => Now);
        h.Seed();

        await Assert.That(service.Wants(DemoPath)).IsFalse()
            .Because("with the sweep off a demo that is not open joins no visit");

        open = DemoPath;
        await Assert.That(service.Wants(DemoPath)).IsTrue()
            .Because("the open demo is built on the parse its open paid for");
        service.Evaluate(DemoPath, Parse());
        await Assert.That(h.Proposals.TryRead(DemoPath)).IsNotNull();
    }

    [Test]
    public async Task AChangedProfile_MarksTheProposalsStale()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        string before = h.Service.FingerprintFor(SuggestedTagsTestData.Map);

        h.Profile = DetectorProfile.Default.With("execute", "lag", 9);

        await Assert.That(h.Service.FingerprintFor(SuggestedTagsTestData.Map)).IsNotEqualTo(before);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue();
    }

    [Test]
    public async Task Accept_WritesAHumanNamespaceInstance_WithProvenance_ThenTheVerdict()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        int pendingBefore = h.Service.Load(DemoPath).Pending.Count;

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue();

        TagDocument? tags = h.Tags.TryLoad(Sha);
        await Assert.That(tags).IsNotNull();
        TagInstance instance = tags!.Instances.Single();
        SuggestionVerdict verdict = h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId];
        using (Assert.Multiple())
        {
            await Assert.That(instance.Code).IsEqualTo("execute");
            await Assert.That(instance.Source).IsEqualTo(TagSources.Suggested);
            await Assert.That(instance.Round).IsEqualTo(1);
            await Assert.That(instance.Labels.Any(l => l is { Group: "side", Value: "T" })).IsTrue();
            await Assert.That(instance.Labels.Any(l => l is { Group: "site", Value: "BombsiteA" })).IsTrue();
            await Assert.That((string?)instance.Provenance!["source"]).IsEqualTo(SuggestedTagsService.ProvenanceSource);
            await Assert.That((string?)instance.Provenance["detector"]).IsEqualTo("execute");
            await Assert.That((string?)instance.Provenance["proposalId"]).IsEqualTo(ExecuteId);
            await Assert.That((bool?)instance.Provenance["edited"]).IsFalse();
            await Assert.That((string?)instance.Provenance["detectorSetFingerprint"])
                .IsEqualTo(h.Service.FingerprintFor(SuggestedTagsTestData.Map));
            await Assert.That(instance.FactsStamp).IsNotNull().Because("the round's facts are stamped as it is made");
            await Assert.That(verdict.Verdict).IsEqualTo(SuggestionVerdicts.Accepted);
            await Assert.That(verdict.TagInstanceId).IsEqualTo(instance.Id);
            await Assert.That(verdict.FrameCount).IsEqualTo(2);
            await Assert.That(h.Service.Load(DemoPath).Pending.Any(e => e.Proposal.Id == ExecuteId)).IsFalse();
            await Assert.That(h.Proposals.Stamp(DemoPath)!.Count).IsEqualTo(pendingBefore - 1);
            await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsFalse().Because("it is no longer pending");
        }
    }

    [Test]
    public async Task AnEdit_AcceptsWithItsWindowAndLabels_MarkedEdited()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        List<TagLabel> labels = [new("site", "BombsiteA"), new("tempo", "slow")];

        await Assert.That(h.Service.Accept(DemoPath, ExecuteId, new TagInstanceEdit(1500, 2500, labels, "late")))
            .IsTrue();

        TagInstance instance = h.Tags.TryLoad(Sha)!.Instances.Single();
        using (Assert.Multiple())
        {
            await Assert.That(instance.FromTick).IsEqualTo(1500);
            await Assert.That(instance.ToTick).IsEqualTo(2500);
            await Assert.That(instance.Note).IsEqualTo("late");
            await Assert.That(instance.Labels.Any(l => l is { Group: "tempo", Value: "slow" })).IsTrue();
            await Assert.That(instance.Labels.Any(l => l.Group == "count")).IsFalse()
                .Because("the edit's labels replace the proposal's");
            await Assert.That((bool?)instance.Provenance!["edited"]).IsTrue();
            await Assert.That(h.Tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict)
                .IsEqualTo(SuggestionVerdicts.Edited);
        }
    }

    [Test]
    public async Task ARejection_SurvivesATuningPass_AndAnAcceptedInstanceIsUntouched()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string Other = DefaultId;
        await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();
        await Assert.That(h.Service.Accept(DemoPath, Other)).IsTrue();
        TagInstance accepted = h.Tags.TryLoad(Sha)!.Instances.Single();

        h.Profile = DetectorProfile.Default.With("execute", "lag", 9);
        await Assert.That(h.Service.Wants(DemoPath)).IsTrue();
        h.Service.Evaluate(DemoPath, Parse());

        ProposalSet set = h.Service.Load(DemoPath);
        using (Assert.Multiple())
        {
            await Assert.That(set.Entries.Any(e => e.Proposal.Id == ExecuteId)).IsTrue()
                .Because("the detector still finds it");
            await Assert.That(set.Pending.Any(e => e.Proposal.Id == ExecuteId)).IsFalse()
                .Because("the rejection stands");
            await Assert.That(set.Pending.Any(e => e.Proposal.Id == Other)).IsFalse();
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Single().Id).IsEqualTo(accepted.Id);
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Single().ToTick).IsEqualTo(accepted.ToTick);
        }
    }

    [Test]
    public async Task AReparseThatRenumbersRounds_ReMatchesTheVerdictByTriggerTick()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();

        // The same round, called round 2 by a parse with another frame count: the key changes, the
        // trigger does not. A new parse of the demo is a rebuild of its proposals.
        h.Proposals.Data.Invalidate(ProposalStore.Facet, DemoPath);
        h.Build(round: 2, frames: 3);

        ProposalSet set = h.Service.Load(DemoPath);
        ProposalEntry execute = set.Entries.Single(e => e.Proposal.Detector == "execute");
        using (Assert.Multiple())
        {
            await Assert.That(execute.Proposal.Id).IsEqualTo("exec|r2|T|BombsiteA|s=10");
            await Assert.That(execute.IsPending).IsFalse();
            await Assert.That(execute.VerdictKey).IsEqualTo(ExecuteId);
        }
    }

    [Test]
    public async Task TheReMatch_TakesTheNearestWithinFiveSeconds_AndOnlyAcrossParses()
    {
        TagProposal near = Proposal("exec|r2|T|BombsiteA|s=10", "execute", 2, 0, 100, 1000, 0.8);
        TagProposal far = Proposal("exec|r3|T|BombsiteA|s=10", "execute", 3, 0, 100, 5000, 0.8);
        Dictionary<string, SuggestionVerdict> verdicts = new(StringComparer.Ordinal)
        {
            ["exec|r1|T|BombsiteA|s=10"] =
                new SuggestionVerdict
                {
                    Verdict = SuggestionVerdicts.Rejected,
                    Detector = "execute",
                    Side = 2,
                    TriggerTick = 1000 + 3 * 64,
                    FrameCount = 2
                },
            ["exec|r3|T|BombsiteA|s=10"] = new SuggestionVerdict
            {
                Verdict = SuggestionVerdicts.Rejected,
                Detector = "execute",
                Side = 2,
                TriggerTick = 5000 + 6 * 64,
                FrameCount = 2
            }
        };

        IReadOnlyList<ProposalEntry> sameParse = ProposalVerdictMatch.Resolve([near, far], verdicts, 2, 64);
        IReadOnlyList<ProposalEntry> reparsed = ProposalVerdictMatch.Resolve([near, far], verdicts, 3, 64);

        using (Assert.Multiple())
        {
            await Assert.That(sameParse[0].IsPending).IsTrue().Because("same parse: keys only, and r2 has none");
            await Assert.That(sameParse[1].IsPending).IsFalse().Because("same parse, same key");
            await Assert.That(reparsed[0].VerdictKey).IsEqualTo("exec|r1|T|BombsiteA|s=10").Because("3 s away");
            await Assert.That(reparsed[1].IsPending).IsTrue().Because("6 s away is another proposal");
        }
    }

    [Test]
    public async Task WithNoRoots_ProposalsAndVerdicts_StayInMemory_AcrossAcceptAndReject()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        const string Other = DefaultId;

        await Assert.That(h.Service.IsPersistent).IsFalse();
        await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsTrue();
        await Assert.That(h.Service.Reject(DemoPath, Other)).IsTrue();

        ProposalSet set = h.Service.Load(DemoPath);
        using (Assert.Multiple())
        {
            await Assert.That(set.IsPersistent).IsFalse();
            await Assert.That(set.Entries.Single(e => e.Proposal.Id == ExecuteId).Verdict!.Verdict)
                .IsEqualTo(SuggestionVerdicts.Accepted);
            await Assert.That(set.Entries.Single(e => e.Proposal.Id == Other).Verdict!.Verdict)
                .IsEqualTo(SuggestionVerdicts.Rejected);
            await Assert.That(h.Proposals.IsPersistent).IsFalse();
            await Assert.That(h.Tags.VerdictsPathFor(Sha)).IsNull();
        }
    }

    [Test]
    public async Task OnDisk_TheProposalsAreTheDemosData_TheVerdictsUnderTags_AndOnlyTheProposalsLeaveWithTheDemo()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(Path.Combine(root, "cache"), Path.Combine(root, "tags"));
            h.Build();
            await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsTrue();

            string proposals = Directory
                .GetFiles(Path.Combine(root, "cache"), Sha + ".json.gz", SearchOption.AllDirectories).Single();
            string verdicts = Path.Combine(root, "tags", "verdicts", Sha + ".verdicts.json");
            await Assert.That(Path.GetFileName(Path.GetDirectoryName(proposals))).IsEqualTo(ProposalStore.Facet)
                .Because("the proposals are the extension's per-demo data, keyed by the demo's content");
            await Assert.That(File.Exists(verdicts)).IsTrue();
            await Assert.That(h.Proposals.TryRead(DemoPath)!.Proposals.Count)
                .IsEqualTo(h.Service.Load(DemoPath).Entries.Count);

            h.Cache.Remove(DemoPath);
            await Assert.That(File.Exists(proposals)).IsFalse()
                .Because("a derived file follows its demo out of the index");
            await Assert.That(File.Exists(verdicts)).IsTrue().Because("verdicts are user truth");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AnUnreadableVerdictsFile_OffersNothing_AndIsNeverOverwritten()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(tagsRoot: root);
            h.Build();
            string file = h.Tags.VerdictsPathFor(Sha)!;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{ not json");

            ProposalSet set = h.Service.Load(DemoPath);
            using (Assert.Multiple())
            {
                await Assert.That(set.VerdictsUnreadable).IsTrue();
                await Assert.That(set.Pending).IsEmpty();
                await Assert.That(h.Service.Reject(DemoPath, ExecuteId)).IsFalse();
                await Assert.That(h.Tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict())).IsFalse();
                await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo("{ not json");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AnAccept_WhoseVerdictCannotBeWritten_TakesItsTagBack()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(tagsRoot: root);
            h.Build();
            // A directory where the verdicts file goes: reads as no verdicts, and every write fails.
            Directory.CreateDirectory(h.Tags.VerdictsPathFor(Sha)!);

            await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsFalse();
            await Assert.That(h.Tags.TryLoad(Sha)?.Instances ?? []).IsEmpty()
                .Because("the proposal is still pending, so a kept tag would be written again on the next accept");
            await Assert.That(h.Service.Load(DemoPath).Pending.Select(e => e.Proposal.Id)).Contains(ExecuteId);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AnAccept_WhoseRollbackAlsoFails_IsHeld_SoNoSecondTagIsWritten()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dv-st-{Guid.NewGuid():N}");
        try
        {
            using SuggestedTagsReviewHarness h = new(tagsRoot: root);
            h.Build();
            string verdicts = h.Tags.VerdictsPathFor(Sha)!;
            Directory.CreateDirectory(verdicts);
            h.Service.RollbackOverride = (_, _) => false;

            await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsFalse();
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Count).IsEqualTo(1).Because("the rollback failed");

            Directory.Delete(verdicts);
            await Assert.That(h.Service.Accept(DemoPath, ExecuteId)).IsFalse()
                .Because("the proposal is held for the session");
            await Assert.That(h.Tags.TryLoad(Sha)!.Instances.Count).IsEqualTo(1);
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
    }

    [Test]
    public async Task AVerdictKey_KeepsItsFirstVerdict()
    {
        TagStore tags = new(null);

        await Assert
            .That(tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict { Verdict = SuggestionVerdicts.Rejected }))
            .IsTrue();
        await Assert
            .That(tags.RecordVerdict(Sha, ExecuteId, new SuggestionVerdict { Verdict = SuggestionVerdicts.Accepted }))
            .IsFalse();
        await Assert.That(tags.LoadVerdicts(Sha)!.Verdicts[ExecuteId].Verdict).IsEqualTo(SuggestionVerdicts.Rejected);
    }

    [Test]
    public async Task AFileBuiltForOtherBytes_IsNotOffered()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();

        h.Seed(sha: new string('b', 64));

        await Assert.That(h.Service.Load(DemoPath).Entries).IsEmpty();
    }

    // The build counts the pending proposals against the verdicts, then writes the count with the proposals; a
    // verdict from the queue between the two must not leave the stamp one high.
    [Test]
    public async Task AVerdictBetweenARebuildsCountAndItsWrite_IsCountedOnTheStamp()
    {
        using SuggestedTagsReviewHarness h = new();
        h.Build();
        HookedData data = new(h.Cache.Data());
        SuggestedTagsService service = new(h.Cache.Library(), h.Cache.RoundFacts(), new ProposalStore(data), h.Tags,
            h.Regions, () => h.Profile, () => true, () => true, walk: _ => Walk(), utcNow: () => Now);
        await Assert.That(service.Load(DemoPath).Pending.Count).IsGreaterThan(1);

        Thread? verdict = null;
        data.BeforeWrite = write =>
        {
            if (write.Facet != ProposalStore.Facet)
            {
                return;
            }

            data.BeforeWrite = null;
            verdict = new Thread(() => service.Reject(DemoPath, ExecuteId));
            verdict.Start();
            // Long enough for an unguarded verdict to finish; a guarded one waits for the build instead.
            verdict.Join(TimeSpan.FromSeconds(1));
        };
        service.Request(DemoPath);
        service.Evaluate(DemoPath, Parse());
        await Assert.That(verdict).IsNotNull();
        await Assert.That(verdict!.Join(TimeSpan.FromSeconds(10))).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(service.Load(DemoPath).Pending.Any(e => e.Proposal.Id == ExecuteId)).IsFalse();
            await Assert.That(new ProposalStore(data).Stamp(DemoPath)!.Count)
                .IsEqualTo(service.Load(DemoPath).Pending.Count);
        }
    }

    private sealed class HookedData(IExtensionDemoData inner) : IExtensionDemoData
    {
        public Action<DemoDataWrite>? BeforeWrite { get; set; }

        public bool IsAvailable => inner.IsAvailable;

        public event Action<string?>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public DemoDataStamp? Stamp(string demoPath, string facet) => inner.Stamp(demoPath, facet);

        public IReadOnlyList<DemoDataStamp> Stamps(string facet) => inner.Stamps(facet);

        public byte[]? Read(string demoPath, string facet, int schema, string? fingerprint, string? part = null) =>
            inner.Read(demoPath, facet, schema, fingerprint, part);

        public DemoDataRecord? ReadAny(string demoPath, string facet, string? part = null) =>
            inner.ReadAny(demoPath, facet, part);

        public DemoDataStamp? Write(string demoPath, DemoDataWrite write)
        {
            BeforeWrite?.Invoke(write);
            return inner.Write(demoPath, write);
        }

        public void MarkFailed(string demoPath, string facet) => inner.MarkFailed(demoPath, facet);

        public void ClearFailed(string demoPath, string facet) => inner.ClearFailed(demoPath, facet);

        public void Invalidate(string facet, string? demoPath = null) => inner.Invalidate(facet, demoPath);

        public void SetCount(string demoPath, string facet, int count) => inner.SetCount(demoPath, facet, count);

        public void Delete(string demoPath, string facet) => inner.Delete(demoPath, facet);
    }
}
