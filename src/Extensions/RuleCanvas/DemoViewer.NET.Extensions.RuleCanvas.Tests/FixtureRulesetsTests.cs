#region

using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The testbed fixtures under <c>src/Extensions/RuleCanvas/testbed/rules</c> are what the manual
///     verification scripts open, so each one must draw the structure its header comment promises and say
///     nothing else. Loaded beside the shipped corpus, as the launcher has the app load them.
/// </summary>
public class FixtureRulesetsTests
{
    private static readonly string[] _fixtures =
    [
        "tb_counts", "tb_defines", "tb_defines_warn", "tb_events", "tb_flags", "tb_foreach", "tb_gates", "tb_highlights",
        "tb_incomplete", "tb_kinds", "tb_params_use", "tb_show"
    ];

    internal static string TestbedDirectory => Path.Combine(Path.GetDirectoryName(RulesTree.RepoRulesDirectory)!,
        "src", "Extensions", "RuleCanvas", "testbed", "rules");

    private static RulesetCorpus Load() =>
        RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped"), (TestbedDirectory, "user")]);

    [Test]
    public async Task EveryFixture_Loads_AndNoOtherFileIsInTheDirectory()
    {
        RulesetCorpus corpus = Load();
        string[] files = [.. Directory.EnumerateFiles(TestbedDirectory, "*.rules.yaml").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];
        using (Assert.Multiple())
        {
            await Assert.That(files).IsEquivalentTo(_fixtures.Select(f => f + ".rules.yaml"));
            await Assert.That(corpus.Failures.Where(f => f.Path.StartsWith(TestbedDirectory, StringComparison.Ordinal))).IsEmpty();
            await Assert.That(corpus.Entries.Where(e => e.Source == "user").Select(e => e.Id)).IsEquivalentTo(_fixtures);
        }
    }

    [Test]
    [Arguments("tb_counts")]
    [Arguments("tb_defines")]
    [Arguments("tb_events")]
    [Arguments("tb_flags")]
    [Arguments("tb_foreach")]
    [Arguments("tb_gates")]
    [Arguments("tb_highlights")]
    [Arguments("tb_kinds")]
    [Arguments("tb_params_use")]
    [Arguments("tb_show")]
    public async Task AWholeFixture_ComposesWithNothingSaid(string id)
    {
        RulesetCorpus.Composed composed = Load().Compose(id);
        foreach (string line in composed.Diagnostics)
        {
            Console.WriteLine($"[{id}] {line}");
        }

        using (Assert.Multiple())
        {
            await Assert.That(composed.Diagnostics).IsEmpty();
            await Assert.That(composed.Chain.Nodes).IsNotEmpty().Because("the chain built");
        }
    }

    [Test]
    public async Task TheDefinesWarnFixture_SaysOnlyThatNothingFiresOnItsDefine()
    {
        RulesetCorpus.Composed composed = Load().Compose("tb_defines_warn");
        using (Assert.Multiple())
        {
            await Assert.That(composed.Report.Select(r => (r.Message, r.Weight))).IsEquivalentTo(
            [
                ("nothing fires on define unused_kill: set a stat's trigger to it, or drop it on a trigger wire", DiagnosticWeight.Warning)
            ]);
            await Assert.That(composed.Chain.Nodes).IsNotEmpty();
        }
    }

    [Test]
    public async Task TheIncompleteFixture_LoadsItsWholeEntry_AndSaysWhatEachOtherLacks()
    {
        RulesetCorpus.Composed composed = Load().Compose("tb_incomplete");
        string[] lines = File.ReadAllLines(Path.Combine(TestbedDirectory, "tb_incomplete.rules.yaml"));
        int LineOf(string text) => Array.IndexOf(lines, text) + 1;
        foreach (string line in composed.Diagnostics)
        {
            Console.WriteLine($"[tb_incomplete] {line}");
        }

        using (Assert.Multiple())
        {
            await Assert.That(composed.Report.Select(r => (r.Message, r.Weight, r.Line))).IsEquivalentTo(
            [
                ("no_trigger has no trigger: add on: with a view, such as damage_dealt", DiagnosticWeight.Warning, LineOf("  no_trigger:")),
                ("blank_when has a blank when: type the test, or wire a stat into its when port", DiagnosticWeight.Warning, LineOf("  blank_when:")),
                ("stat 'bad' must be a map with a kind key", DiagnosticWeight.Warning, LineOf("  bad: 5")),
                ("unknown key 'frobnicate' in stat 'odd_key'", DiagnosticWeight.Warning, LineOf("    frobnicate: 1"))
            ]);
            await Assert.That(composed.Graph.Nodes.Select(n => n.Key.Id)).Contains("ok_kills");
            await Assert.That(composed.Graph.Nodes.Select(n => n.Key.Id)).DoesNotContain("bad");
            await Assert.That(composed.Chain.Edges.Any(e => e.Target.Id == "ok_kills" && !e.IsRead)).IsTrue().Because("the whole stat still lowers and is wired");
        }
    }

    [Test]
    public async Task TheGatesFixture_DrawsAMatchLiveContextNode()
    {
        RulesetChainGraph chain = Load().Compose("tb_gates").Chain;
        ChainNode live = chain.Nodes.Single(n => n.Kind == ChainNodeKind.Context && n.Name == "MatchLive");
        await Assert.That(chain.Edges.Any(e => e.Source == live.Key && e.Target.Id == "live_deaths")).IsTrue();
    }

    [Test]
    public async Task TheEventsFixture_HasAnEventNodeCarryingMoreThanOneWireEvent()
    {
        RulesetChainGraph chain = Load().Compose("tb_events").Chain;
        ChainNode[] merged = [.. chain.Nodes.Where(n => n.Kind == ChainNodeKind.Event && n.Events.Count > 1)];
        Console.WriteLine("[tb_events] " + string.Join("; ", merged.Select(n => n.Name + ": " + string.Join(", ", n.Events))));
        await Assert.That(merged.Length).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    public async Task TheShowFixture_HasATableOfFourColumns_AndAScoreboardOfThree()
    {
        RulesetCorpus.Composed composed = Load().Compose("tb_show");
        ChainNode table = composed.Chain.Nodes.Single(n => n.Kind == ChainNodeKind.Show && n.Name == "tb_show_lines");
        ChainNode board = composed.Chain.Nodes.Single(n => n.Kind == ChainNodeKind.Show && n.Key == RulesetDocumentNodeKey.Scoreboard("tb_show"));
        using (Assert.Multiple())
        {
            await Assert.That(composed.Open!.Show!.Tables.Single().Columns.Count).IsEqualTo(4);
            await Assert.That(composed.Chain.Edges.Count(e => e.Target == table.Key)).IsEqualTo(4).Because("one labelled read wire per column");
            await Assert.That(composed.Chain.Edges.Count(e => e.Target == board.Key)).IsEqualTo(3);
        }
    }

    /// <summary>
    ///     The launcher hands the testbed to the app as its user rules, so the app's analysis composes the
    ///     fixtures beside the shipped set. A scoreboard label clash rejects the whole directory and one
    ///     composition diagnostic fails the highlight fingerprint, hence tb_incomplete's <c>enabled: false</c>.
    /// </summary>
    [Test]
    public async Task TheFixtures_ComposeAsTheAppsUserOverlay_WithNothingSaid()
    {
        RuleConfigLoadResult result = YamlConfigLoader.LoadWithOverlay(RulesTree.RepoRulesDirectory, TestbedDirectory);
        RulesetComposition.Result composed = RulesetComposition.Compose(result.Rulesets, EmbeddedCatalog.Adapter, 64.0,
            DemoSourceProfileRegistry.DefaultFallback.GetType().Name);
        using (Assert.Multiple())
        {
            await Assert.That(result.Errors.Select(e => e.ToString()).Where(e => !e.StartsWith("tb_incomplete.rules.yaml", StringComparison.Ordinal)))
                .IsEmpty();
            await Assert.That(result.Rulesets.Select(r => r.Id).Where(id => id.StartsWith("tb_", StringComparison.Ordinal)))
                .IsEquivalentTo(_fixtures.Where(f => f != "tb_incomplete")).Because("the app's analysis leaves out only the disabled file");
            await Assert.That(composed.Diagnostics.Select(d => d.ToString())).IsEmpty();
            await Assert.That(composed.Excluded).IsEmpty();
        }
    }
}
