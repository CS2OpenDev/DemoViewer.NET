#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A <c>use:</c> dependency is one node until it is expanded: player_stats draws kast as a DEPENDENCY node
///     with one output per entry it reads, the dependency's own chain stays out of the picture, and the
///     expansion lives in the sidecar beside the positions. A ruleset another one uses shows nothing extra.
/// </summary>
public class DependencyNodeTests
{
    private static readonly RulesetDocumentNodeKey _kast = RulesetDocumentNodeKey.Dependency("kast");
    private static readonly RulesetDocumentNodeKey _hltv = new("player_stats", "HLTV");

    private static RulesetCorpus.Composed Compose(string id) =>
        RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]).Compose(id);

    private static readonly HashSet<string> _none = new(StringComparer.Ordinal);

    [Test]
    public async Task PlayerStats_FoldsKastIntoOneNode_WithAnOutputPerEntryRead()
    {
        RulesetCorpus.Composed whole = Compose("player_stats");
        RulesetCorpus.Composed folded = DependencyFolding.Fold(whole, _none);
        RulesetChainGraph chain = folded.Chain;
        ChainNode dependency = chain.Nodes.Single(n => n.Kind == ChainNodeKind.Dependency);
        ChainEdge[] fromKast = [.. chain.Edges.Where(e => e.Source == _kast)];
        HashSet<RulesetDocumentNodeKey> reachable = Upstream(chain, "player_stats");
        Console.WriteLine("[dependency] outputs=" + string.Join(", ", dependency.Outputs) + " wires=" + string.Join(" | ", fromKast.Select(e => $"{e.Target} {e.Label} {e.Output} read={e.IsRead} gate={e.IsGate} {e.Effect}")));
        using (Assert.Multiple())
        {
            await Assert.That(whole.Graph.Nodes.Any(n => n.Key.Ruleset == "kast")).IsTrue().Because("the composition itself still carries the dependency");
            await Assert.That(dependency.Key).IsEqualTo(_kast);
            await Assert.That(dependency.Name).IsEqualTo("kast");
            await Assert.That(dependency.EntryCount).IsEqualTo(whole.Documents["kast"].Stats.Count + whole.Documents["kast"].Highlights.Count);
            await Assert.That(dependency.Outputs).Contains("kast_pct").Because("HLTV reads kast.kast_pct");
            await Assert.That(dependency.Outputs.Count).IsEqualTo(fromKast.Select(e => e.Output).Distinct().Count()).Because("one output per entry read");
            await Assert.That(chain.Nodes.Any(n => n.Key.Ruleset == "kast")).IsFalse().Because("kast's entries fold into the node");
            await Assert.That(folded.Graph.Nodes.Any(n => n.Key.Ruleset == "kast")).IsFalse();
            await Assert.That(folded.Graph.Nodes.Count).IsEqualTo(whole.Graph.Nodes.Count(n => n.Key.Ruleset == "player_stats"));
            await Assert.That(fromKast.Where(e => e.Target == _hltv).Select(e => (e.Label, e.Output ?? "", e.IsRead))).IsEquivalentTo([("kast_pct", "kast_pct", true)])
                .Because("the wire out of the dependency carries the entry name");
            await Assert.That(fromKast.All(e => e.Output is not null && e.Label.StartsWith(e.Output, StringComparison.Ordinal))).IsTrue();
            await Assert.That(chain.Edges.Any(e => e.Target.Ruleset == "kast")).IsFalse().Because("what fires kast's own stats is kast's picture");
            await Assert.That(chain.Nodes.Where(n => n.Kind != ChainNodeKind.Dependency).All(n => reachable.Contains(n.Key))).IsTrue()
                .Because("a scaffold only kast reaches goes with it: " + string.Join(", ", chain.Nodes.Where(n => !reachable.Contains(n.Key) && n.Kind != ChainNodeKind.Dependency).Select(n => n.Key)));
            await Assert.That(chain.Nodes.Any(n => n.Name == "player_death")).IsTrue().Because("an event both rulesets fire on stays");
            await Assert.That(chain.IsLowered).IsTrue();
            await Assert.That(chain.EngineEdges.ContainsKey(fromKast[0])).IsTrue().Because("the folded wire keeps the engine edge it stands for");
            await Assert.That(folded.DrawnKeys).Contains(_kast);
        }
    }

    [Test]
    public async Task AnExpandedDependency_IsLeftAsDrawn_AndAUsedRulesetShowsNothingExtra()
    {
        RulesetCorpus.Composed whole = Compose("player_stats");
        RulesetCorpus.Composed kast = Compose("kast");
        using (Assert.Multiple())
        {
            await Assert.That(DependencyFolding.Fold(whole, new HashSet<string>(["kast"], StringComparer.Ordinal))).IsSameReferenceAs(whole);
            await Assert.That(DependencyFolding.Fold(kast, _none)).IsSameReferenceAs(kast).Because("kast uses nothing; that player_stats uses it is player_stats' picture");
            await Assert.That(kast.Chain.Nodes.Any(n => n.Kind == ChainNodeKind.Dependency)).IsFalse();
        }
    }

    [Test]
    public async Task ABrokenDependency_StillFoldsToANode_CarryingItsDiagnostics()
    {
        using RulesTree rules = RulesTree.WithUser("player_stats");
        string kast = rules.UserPath("kast");
        File.WriteAllText(kast, File.ReadAllText(Path.Combine(RulesTree.RepoRulesDirectory, "kast.rules.yaml"))
            .Replace("kast_pct:\n    compute: \"", "kast_pct:\n    compute: \"no_such_stat + ", StringComparison.Ordinal));
        RulesetCorpus.Composed folded = DependencyFolding.Fold(rules.Load().Compose("player_stats"), _none);
        IReadOnlyList<NodeFace> faces = NodeFaceBuilder.Build(folded);
        DependencyFace face = faces.OfType<DependencyFace>().Single();
        using (Assert.Multiple())
        {
            await Assert.That(folded.Chain.IsLowered).IsFalse();
            await Assert.That(folded.Chain.Nodes.Any(n => n.Kind == ChainNodeKind.Dependency)).IsTrue();
            await Assert.That(face.EntryCount).IsGreaterThan(15);
            await Assert.That(face.Diagnostics.Any(d => d.Message.Contains("no_such_stat", StringComparison.Ordinal) && d.IsError)).IsTrue()
                .Because("the dependency's own diagnostics land on its node rather than on a blank canvas");
            await Assert.That(face.IsAuthorable).IsFalse();
            await Assert.That(face.IsScaffold).IsFalse();
            await Assert.That(face.IsDependency).IsTrue();
        }
    }

    [Test]
    public async Task TheFace_SaysTheCountAndTheCopy_AndTheNodeHasTheOutputs()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("kast");
        RulesetCorpus corpus = rules.Load();
        RulesetCorpus.Composed folded = DependencyFolding.Fold(corpus.Compose("player_stats"), _none);
        DependencyFace face = NodeFaceBuilder.Build(folded).OfType<DependencyFace>().Single();
        CanvasNodeViewModel node = CanvasNodeFactory.ReadOnly.Create(face);
        (IReadOnlyList<CanvasNodeViewModel> nodes, IReadOnlyList<CanvasConnectionViewModel> wires) = CanvasGraphBuilder.Build(folded);
        CanvasNodeViewModel drawn = nodes.Single(n => n.IsDependency);
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Overrides.Keys).IsEquivalentTo(["kast"]);
            await Assert.That(face.IsUserCopy).IsTrue();
            await Assert.That(face.UserPath).IsEqualTo(rules.UserPath("kast"));
            await Assert.That(face.ShippedPath).IsEqualTo(rules.ShippedPath("kast"));
            await Assert.That(face.Title).IsEqualTo("KAST Tracking");
            await Assert.That(node.Kind).IsEqualTo("dependency");
            await Assert.That(node.KindLabel).IsEqualTo("DEPENDENCY");
            await Assert.That(node.Title).IsEqualTo("kast");
            await Assert.That(node.Label).IsEqualTo("KAST Tracking");
            await Assert.That(node.Chips).IsEquivalentTo([face.EntryCountText, "your copy"]);
            await Assert.That(face.EntryCountText).EndsWith(" entries");
            await Assert.That(node.ChipTips["your copy"]).Contains(rules.UserPath("kast"));
            await Assert.That(node.ChipTips["your copy"]).Contains(rules.ShippedPath("kast"));
            await Assert.That(node.IsScaffold).IsFalse();
            await Assert.That(node.IsForeign).IsFalse();
            await Assert.That(node.ForeignRuleset).IsEqualTo("kast");
            await Assert.That(node.DependencyToggleText).IsEqualTo("Expand kast");
            await Assert.That(node.OpenRulesetText).IsEqualTo("Open kast");
            await Assert.That(drawn.Outputs.Select(o => o.Title)).IsEquivalentTo(face.Entries).Because("one output per entry read, titled by the entry");
            await Assert.That(drawn.Outputs.Select(o => o.Title)).Contains("kast_pct");
            await Assert.That(drawn.Outputs.All(o => o.Kind == PortKind.Value)).IsTrue();
            CanvasConnectionViewModel intoHltv = wires.Single(w => ReferenceEquals(w.Source.Owner, drawn) && w.Target.Owner?.Id == "HLTV");
            await Assert.That(intoHltv.Source.Title).IsEqualTo("kast_pct").Because("the wire leaves from the entry's own output");
            await Assert.That(intoHltv.CanGoToDefinition).IsTrue();
            await Assert.That(wires.Where(w => !ReferenceEquals(w.Source.Owner, drawn)).All(w => !w.CanGoToDefinition)).IsTrue();
            await Assert.That(NodeSizeEstimator.HeightOf(drawn)).IsGreaterThan(60);
        }
    }

    [Test]
    public async Task TheTab_DrawsTheDependencyFolded_ExpandsItInPlace_AndKeepsThatInTheSidecar()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        CanvasNodeViewModel dependency = vm.Nodes.Single(n => n.IsDependency);
        Point was = dependency.Location;
        int folded = vm.Nodes.Count;
        using (Assert.Multiple())
        {
            await Assert.That(dependency.Id).IsEqualTo("kast");
            await Assert.That(vm.Nodes.Any(n => n.IsForeign)).IsFalse();
            await Assert.That(vm.ExpandedDependencies).IsEmpty();
            await Assert.That(vm.Connections.Any(c => ReferenceEquals(c.Source.Owner, dependency) && c.Target.Owner?.Id == "HLTV")).IsTrue();
            await Assert.That(vm.Status).DoesNotContain("kast");
        }

        vm.ToggleDependency(dependency);
        List<CanvasNodeViewModel> drawn = [.. vm.Nodes.Where(n => n.IsForeign)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Any(n => n.IsDependency)).IsFalse();
            await Assert.That(drawn.Count).IsGreaterThan(15).Because("kast's entries are drawn read-only");
            await Assert.That(drawn.All(n => n.Ruleset == "kast" && !n.IsAuthorable)).IsTrue();
            await Assert.That(drawn.All(n => n.DependencyToggleText == "Collapse kast")).IsTrue();
            await Assert.That(vm.ExpandedDependencies).IsEquivalentTo(["kast"]);
            await Assert.That(vm.Nodes.Count).IsGreaterThan(folded);
            await Assert.That(drawn.Min(n => n.Location.X)).IsEqualTo(was.X).Because("the entries are tidied from the folded node's corner");
            await Assert.That(drawn.Min(n => n.Location.Y)).IsEqualTo(was.Y);
            await Assert.That(RulesetLayoutStore.Load(rules.UserPath("player_stats")).Expanded).IsEquivalentTo(["kast"]);
            await Assert.That(vm.HasSavedLayout).IsTrue();
            await Assert.That(vm.CanUndo).IsTrue();
        }

        CanvasTabViewModel again = rules.NewViewModel();
        again.PreferredRulesetId = "player_stats";
        again.OnActivated(null!);
        await Assert.That(again.Nodes.Any(n => n.IsForeign)).IsTrue().Because("the expansion comes back from the sidecar");

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Any(n => n.IsForeign)).IsFalse().Because("one Undo folds the dependency back and puts its node where it was");
            await Assert.That(vm.Nodes.Single(n => n.IsDependency).Location).IsEqualTo(was);
            await Assert.That(RulesetLayoutStore.Load(rules.UserPath("player_stats")).Expanded).IsEmpty();
        }

        vm.ExpandDependency("kast");
        vm.CollapseDependency("kast");
        await Assert.That(vm.Nodes.Count(n => n.IsDependency)).IsEqualTo(1);

        // A wire from the folded node itself has no entry to read; the refusal says what to do instead.
        string text = File.ReadAllText(rules.UserPath("player_stats"));
        vm.Connect(vm.Nodes.Single(n => n.IsDependency), vm.Nodes.Single(n => n.Id == "HLTV"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).Contains("Expand kast and wire from the entry itself");
            await Assert.That(File.ReadAllText(rules.UserPath("player_stats"))).IsEqualTo(text);
            await Assert.That(vm.Inspector.NodeNote).IsEmpty().Because("nothing is selected");
        }

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsDependency));
        await Assert.That(vm.Inspector.NodeNote).Contains("use: [ kast ] folded to one node");
    }

    [Test]
    public async Task AWireFromADependencyOutput_ReadsTheEntryItNames_AsTheExpandedEntryWould()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        string path = rules.UserPath("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        string before = File.ReadAllText(path);

        vm.ToggleDependency(vm.Nodes.Single(n => n.IsDependency));
        vm.Connect(vm.Nodes.Single(n => n.IsForeign && n.Id == "kast_pct"), vm.Nodes.Single(n => n.Id == "opening_kill"));
        string expanded = vm.EditStatus;
        vm.UndoCommand.Execute(null);
        await Assert.That(vm.Nodes.Any(n => n.IsDependency)).IsTrue();

        CanvasNodeViewModel dependency = vm.Nodes.Single(n => n.IsDependency);
        CanvasConnectorViewModel output = dependency.Outputs.Single(o => o.Entry == "kast_pct");
        CanvasNodeViewModel highlight = vm.Nodes.Single(n => n.Id == "opening_kill");
        vm.Pending.Source = output;
        vm.Pending.PreviewTarget = highlight;
        string preview = vm.Pending.PreviewText;
        vm.ConnectCommand.Execute(((object)output, (object?)highlight));
        using (Assert.Multiple())
        {
            await Assert.That(expanded).Contains("kast.kast_pct is another ruleset's stat");
            await Assert.That(vm.EditStatus).IsEqualTo(expanded).Because("the folded output reads the entry it names, as the drawn entry does");
            await Assert.That(preview).IsEqualTo(expanded);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(before).Because("a when: that reads another ruleset takes the ruleset out of the chain");
            await Assert.That(dependency.Outputs.All(o => o.Entry is not null)).IsTrue();
            await Assert.That(highlight.Outputs.All(o => o.Entry is null)).IsTrue();
        }

        // A compute reads its operands in its own text, folded or not; the body of the node still names no entry.
        vm.ConnectCommand.Execute(((object)output, (object?)vm.Nodes.Single(n => n.Id == "HLTV")));
        await Assert.That(vm.EditStatus).Contains("reads its operands in its own text");
        vm.ConnectCommand.Execute(((object)dependency, (object?)vm.Nodes.Single(n => n.Id == "HLTV")));
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).Contains("Expand kast and wire from the entry itself");
            await Assert.That(File.ReadAllText(path)).IsEqualTo(before);
        }
    }

    [Test]
    public async Task TheDependencyNode_CarriesTheLiveFiresOfTheEntriesRead()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("player_stats");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        List<LiveInstance> instances = [new LiveInstance(0, null, null), new LiveInstance(1, 0, "Alice")];
        Dictionary<(RulesetDocumentNodeKey, int), NodeTrack> tracks = new()
        {
            [(new RulesetDocumentNodeKey("kast", "kast_pct"), 1)] = new NodeTrack(12, 100, 900, "75", 75f, true),
            [(new RulesetDocumentNodeKey("kast", "kills"), 1)] = new NodeTrack(9, 100, 900, "9", 9f, true)
        };
        vm.ShowLiveRun(new LiveValuesRun("/demos/one.dem", "Cs2GotvProfile", 64, 0, 90000, instances, tracks, [],
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0));
        CanvasNodeViewModel dependency = vm.Nodes.Single(n => n.IsDependency);
        using (Assert.Multiple())
        {
            await Assert.That(dependency.LiveBadge).IsEqualTo("12 fires").Because("the fires of the entries read, not of every entry");
            await Assert.That(dependency.LiveTip).StartsWith("kast_pct: Alice: fired 12 times");
            await Assert.That(dependency.LiveTip).Contains("sum of fires over the read entries: 12");
            await Assert.That(dependency.HasLive).IsTrue();
        }

        vm.SelectedInstance = vm.Instances.Single(i => i.Slot is null);
        await Assert.That(dependency.HasLive).IsFalse().Because("the game instance has no track for a per-player entry");
    }

    [Test]
    public async Task TheLayout_RoundTripsTheExpandedDependencies_AndTheOldShapeIsUntouched()
    {
        RulesetNodeLayout plain = RulesetNodeLayout.Empty.With(_hltv, new Point(10, 20));
        RulesetNodeLayout expanded = plain.WithExpanded("kast", true);
        RulesetNodeLayout parsed = RulesetLayoutStore.Parse(RulesetLayoutStore.Serialize(expanded));
        using (Assert.Multiple())
        {
            await Assert.That(RulesetLayoutStore.Serialize(plain)).DoesNotContain("expanded").Because("absent rather than empty, as the groups are");
            await Assert.That(RulesetLayoutStore.Serialize(expanded)).Contains("\"expanded\": [");
            await Assert.That(parsed.Expanded).IsEquivalentTo(["kast"]);
            await Assert.That(parsed.IsExpanded("kast")).IsTrue();
            await Assert.That(parsed.PositionFor(_hltv)).IsEqualTo(new Point(10, 20));
            await Assert.That(expanded.WithExpanded("kast", true)).IsSameReferenceAs(expanded);
            await Assert.That(RulesetNodeLayout.Empty.WithExpanded("kast", true).IsEmpty).IsFalse().Because("an expansion alone is worth a sidecar");
            await Assert.That(RulesetNodeLayout.Empty.WithExpanded("kast", true).WithExpanded("kast", false)).IsSameReferenceAs(RulesetNodeLayout.Empty);
            await Assert.That(plain.Overlaid(expanded).Expanded).IsEquivalentTo(["kast"]).Because("what is on screen is the truth about the folds");
            await Assert.That(expanded.Overlaid(plain).Expanded).IsEmpty();
            await Assert.That(expanded.Renamed(_hltv, new RulesetDocumentNodeKey("player_stats", "Rating")).Expanded).IsEquivalentTo(["kast"]);
        }
    }

    // Every node reached upstream from the open document's own nodes, through folded entries or not.
    private static HashSet<RulesetDocumentNodeKey> Upstream(RulesetChainGraph chain, string ruleset)
    {
        HashSet<RulesetDocumentNodeKey> seen = [.. chain.Nodes.Where(n => n.Key.Ruleset == ruleset).Select(n => n.Key)];
        Queue<RulesetDocumentNodeKey> pending = new(seen);
        while (pending.Count > 0)
        {
            RulesetDocumentNodeKey at = pending.Dequeue();
            foreach (ChainEdge edge in chain.Edges.Where(e => e.Target == at))
            {
                if (seen.Add(edge.Source))
                {
                    pending.Enqueue(edge.Source);
                }
            }
        }

        return seen;
    }
}
