#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The engine's economy and timeout reads (CS2DemoKit 0.15.0) as the host takes them: a ruleset reads them on
///     the forward build with no host wiring, the Rule Workbench offers them, and a collector may declare the
///     per-player ones as providers and the game-wide ones as game providers.
/// </summary>
public class EconomyReadsTests
{
    internal const string ProbeYaml = """
        ruleset: econ_probe
        title: econ probe
        summary: Reads the economy and timeout state at freeze end.
        for: each_player
        stats:
          round_no:
            capture: round.number
            on: raw.round_freeze_end
            per: round
          slot:
            capture: player.slot
            on: raw.round_freeze_end
            per: round
          helmet:
            capture: player.has_helmet
            on: raw.round_freeze_end
            per: round
          spent:
            capture: player.cash_spent
            on: raw.round_freeze_end
            per: round
          t_left:
            capture: match.t_timeouts_left
            on: raw.round_freeze_end
            per: round
          ct_left:
            capture: match.ct_timeouts_left
            on: raw.round_freeze_end
            per: round
        show:
          tables:
            econ_probe:
              per: player_round
              columns:
                - { stat: round_no, label: round }
                - { stat: slot, label: slot }
                - { stat: helmet, label: helmet }
                - { stat: spent, label: spent }
                - { stat: t_left, label: t_left }
                - { stat: ct_left, label: ct_left }
        """;

    // Every read once, through a role as well as the subject.
    private const string EveryReadYaml = """
        ruleset: econ_every
        title: econ every
        summary: Reads every economy and timeout path.
        for: each_player
        stats:
          kits:
            count: kill
            where: "player.has_defuser && player.start_money > 0 && victim.has_helmet"
            per: round
          paused:
            count: kill
            where: "match.ct_timeout_active || match.t_timeout_active || match.technical_timeout"
            per: round
        """;

    internal static RulesetDoc Load(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
        ?? throw new InvalidOperationException("the probe ruleset does not load");

    [Test]
    public async Task TheWorkbench_OffersTheEngineReads()
    {
        HashSet<string> offered =
        [
            .. WorkbenchCompletionSource.Build(CatalogResource.Load(), "ruleset: t\nfor: each_player\n")
                .Select(c => c.Text)
        ];
        using (Assert.Multiple())
        {
            await Assert.That(offered).Contains("player.has_helmet");
            await Assert.That(offered).Contains("match.t_timeouts_left");
            await Assert.That(offered.Any(o => o.Contains(".dv_", StringComparison.Ordinal))).IsFalse();
        }
    }

    [Test]
    public async Task Validation_AcceptsEveryRead()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([Load(ProbeYaml), Load(EveryReadYaml)]);
        await Assert.That(validated.Success).IsTrue()
            .Because(string.Join("; ", validated.Diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public async Task ForwardBuild_LowersTheReads_WithNoHostWiring()
    {
        RulesetDoc doc = Load(ProbeYaml);
        BuildResult build = ForwardDemoPass.Build(SyntheticParsedDemo.Create(), [doc], new HashSet<string> { "econ_probe" });
        using (Assert.Multiple())
        {
            await Assert.That(build.ExcludedRulesets.Select(r => r.Id)).IsEmpty()
                .Because(string.Join("; ", build.ExcludedRulesets.SelectMany(r => r.Diagnostics).Select(d => d.ToString())));
            await Assert.That(build.Outputs!.Select(o => o.Id)).Contains("econ_probe");
        }
    }

    [Test]
    public async Task Collector_ReadingTheEngineReads_IsAccepted_AndStampedWithItsGameProviders()
    {
        CollectorContribution reads = new("dev.example.kit", "dev.example.pass", 1,
            ["entity.pawn.has_defuser", "entity.controller.cash_spent"], [], () => throw new NotSupportedException())
        {
            GameProviders = ["entity.game.t_timeouts_left"]
        };
        CollectorContribution plain = new("dev.example.pos", "dev.example.pass", 1,
            ["entity.pawn.pos_x"], [], () => throw new NotSupportedException());
        CollectorContribution unknown = new("dev.example.bad", "dev.example.pass", 1,
            ["entity.dv.player.has_helmet"], [], () => throw new NotSupportedException());
        CollectorContribution unknownGame = new("dev.example.bad2", "dev.example.pass", 1,
            [], [], () => throw new NotSupportedException()) { GameProviders = ["entity.pawn.has_helmet"] };
        CollectorSet set = CollectorSet.Of([new RegisteredCollector(reads, null)]);
        using (Assert.Multiple())
        {
            await Assert.That(RegisteredCollector.Refusal(reads)).IsNull();
            await Assert.That(RegisteredCollector.Refusal(unknown)).IsNotNull();
            await Assert.That(RegisteredCollector.Refusal(unknownGame)).IsNotNull()
                .Because("a per-player provider is no game-wide one");
            await Assert.That(reads.Stamp).Contains(";gp=entity.game.t_timeouts_left;");
            await Assert.That(plain.Stamp).DoesNotContain(";gp=");
            await Assert.That(set.DeclareReads(new AnalysisOptions()).ObservedGameProviders)
                .IsEquivalentTo(["entity.game.t_timeouts_left"]);
        }
    }
}
