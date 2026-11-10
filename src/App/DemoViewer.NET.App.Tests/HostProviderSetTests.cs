#region

using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using DemoViewer.NET.Extensions.Sdk;
using DemoViewer.NET.Modules.RuleWorkbench;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The host's <c>entity.dv.*</c> providers reach every build: the catalog rows a ruleset resolves them by, both
///     registries, the collector check, and the stamps that keep outputs read through them from going stale.
/// </summary>
public class HostProviderSetTests
{
    internal const string ProbeYaml = """
        ruleset: dv_probe
        title: dv probe
        summary: Reads the host providers at freeze end.
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
            capture: player.dv_has_helmet
            on: raw.round_freeze_end
            per: round
          spent:
            capture: player.dv_cash_spent_round
            on: raw.round_freeze_end
            per: round
          t_left:
            capture: match.dv_t_timeouts_left
            on: raw.round_freeze_end
            per: round
          ct_left:
            capture: match.dv_ct_timeouts_left
            on: raw.round_freeze_end
            per: round
        show:
          tables:
            dv_probe:
              per: player_round
              columns:
                - { stat: round_no, label: round }
                - { stat: slot, label: slot }
                - { stat: helmet, label: helmet }
                - { stat: spent, label: spent }
                - { stat: t_left, label: t_left }
                - { stat: ct_left, label: ct_left }
        """;

    // Every host path once, through a role as well as the subject.
    private const string EveryReadYaml = """
        ruleset: dv_every
        title: dv every
        summary: Reads every host path.
        for: each_player
        stats:
          kits:
            count: kill
            where: "player.dv_has_defuser && player.dv_start_account > 0 && victim.dv_has_helmet"
            per: round
          paused:
            count: kill
            where: "match.dv_ct_timeout_active || match.dv_t_timeout_active || match.dv_technical_timeout"
            per: round
        """;

    private const string NoHostReadYaml = """
        ruleset: dv_none
        title: dv none
        summary: Reads no host path.
        for: each_player
        stats:
          kills:
            count: kill
            per: round
        """;

    internal static RulesetDoc Load(string yaml) =>
        RulesetDocumentLoader.Load(yaml, "probe.rules.yaml").Doc
        ?? throw new InvalidOperationException("the probe ruleset does not load");

    [Test]
    public async Task Catalog_CarriesEveryHostProvider_UnderItsRulesPath()
    {
        Dictionary<string, CatalogProvider> rows =
            CatalogResource.Load().Providers.ToDictionary(p => p.Name, StringComparer.Ordinal);
        using (Assert.Multiple())
        {
            await Assert.That(HostProviderSet.CatalogRefusal).IsNull();
            foreach (string name in HostProviders.All)
            {
                await Assert.That(rows.ContainsKey(name)).IsTrue().Because(name);
                await Assert.That(rows[name].V2Name).IsEqualTo(HostProviders.RulesPath(name));
                await Assert.That(rows[name].Scope)
                    .IsEqualTo(name.StartsWith("entity.dv.match.", StringComparison.Ordinal) ? "singleton" : "perPlayer");
            }

            // The Rule Workbench offers what the catalog holds.
            HashSet<string> offered =
            [
                .. WorkbenchCompletionSource.Build(CatalogResource.Load(), "ruleset: t\nfor: each_player\n")
                    .Select(c => c.Text)
            ];
            await Assert.That(offered).Contains("player.dv_has_helmet");
            await Assert.That(offered).Contains("match.dv_t_timeouts_left");

            await Assert.That(HostProviders.RulesPath(HostProviders.HasHelmet)).IsEqualTo("player.dv_has_helmet");
            await Assert.That(HostProviders.RulesPath(HostProviders.TTimeoutsLeft)).IsEqualTo("match.dv_t_timeouts_left");
        }
    }

    [Test]
    public async Task Wire_RegistersTheHostProviders_AndAddsToARegistryTheCallerSet()
    {
        EntityValueProviderRegistry callers = EntityValueProviderRegistry.CreateDefault();
        AnalysisOptions wired = HostProviderSet.Wire(new AnalysisOptions { EntityProviders = callers });
        using (Assert.Multiple())
        {
            await Assert.That(wired.EntityProviders).IsSameReferenceAs(callers);
            foreach (string name in HostProviders.All)
            {
                await Assert.That(wired.PerPlayerEntityProviders!.Get(name)).IsNotNull().Because(name);
            }

            foreach (string name in HostProviders.All.Where(n => n.StartsWith("entity.dv.match.", StringComparison.Ordinal)))
            {
                await Assert.That(wired.EntityProviders!.Get(name)).IsNotNull().Because(name);
            }

            // The engine's own stay.
            await Assert.That(wired.PerPlayerEntityProviders!.Get("entity.pawn.health")).IsNotNull();
            await Assert.That(wired.EntityProviders!.Get("entity.game.freeze_period")).IsNotNull();
        }
    }

    [Test]
    public async Task Validation_AcceptsEveryHostPath()
    {
        RulesetValidationResult validated = DemoAnalysis.ValidateRulesets([Load(ProbeYaml), Load(EveryReadYaml)]);
        await Assert.That(validated.Success).IsTrue()
            .Because(string.Join("; ", validated.Diagnostics.Select(d => d.ToString())));
    }

    [Test]
    public async Task ForwardBuild_PassesTheRegistry_AndAnUnwiredBuildCannotLowerTheRead()
    {
        RulesetDoc doc = Load(ProbeYaml);
        BuildResult wired = ForwardDemoPass.Build(SyntheticParsedDemo.Create(), [doc], new HashSet<string> { "dv_probe" });
        BuildResult unwired = DemoAnalysis.Build(SyntheticParsedDemo.Create(), [doc]);
        using (Assert.Multiple())
        {
            await Assert.That(wired.ExcludedRulesets.Select(r => r.Id)).IsEmpty()
                .Because(string.Join("; ", wired.ExcludedRulesets.SelectMany(r => r.Diagnostics).Select(d => d.ToString())));
            await Assert.That(wired.Outputs!.Select(o => o.Id)).Contains("dv_probe");
            await Assert.That(unwired.ExcludedRulesets.Select(r => r.Id)).Contains("dv_probe")
                .Because("without the host registry the engine has no provider behind the catalog row");
        }
    }

    [Test]
    public async Task Collector_ReadingAHostProvider_IsAccepted_AndStampedWithTheHostVersion()
    {
        CollectorContribution reads = new("dev.example.kit", "dev.example.pass", 1,
            [HostProviders.HasDefuser, HostProviders.TTimeoutsLeft], [], () => throw new NotSupportedException());
        CollectorContribution plain = new("dev.example.pos", "dev.example.pass", 1,
            ["entity.pawn.pos_x"], [], () => throw new NotSupportedException());
        CollectorContribution unknown = new("dev.example.bad", "dev.example.pass", 1,
            ["entity.dv.player.nope"], [], () => throw new NotSupportedException());
        using (Assert.Multiple())
        {
            await Assert.That(RegisteredCollector.Refusal(reads)).IsNull();
            await Assert.That(RegisteredCollector.Refusal(unknown)).IsNotNull();
            await Assert.That(reads.Stamp).Contains($";dv={HostProviders.Version};");
            await Assert.That(plain.Stamp).DoesNotContain(";dv=");
        }
    }

    [Test]
    public async Task Stamp_MovesOnlyForARulesetReadingAHostPath()
    {
        string shippedDir = RuleSetLocator.ResolveShippedRulesDirectory();
        IReadOnlyList<RulesetDoc> shipped = YamlConfigLoader.LoadWithOverlay(shippedDir, null).Rulesets;
        using (Assert.Multiple())
        {
            await Assert.That(HostProviderSet.StampFor([Load(NoHostReadYaml)])).IsEqualTo("");
            await Assert.That(HostProviderSet.StampFor(shipped)).IsEqualTo("")
                .Because("no shipped ruleset reads a host path, so no stored stamp moves");
            await Assert.That(HostProviderSet.StampFor([Load(NoHostReadYaml), Load(ProbeYaml)]))
                .IsEqualTo($"dv={HostProviders.Version}");
            await Assert.That(HostProviderSet.StampFor([Load(EveryReadYaml)])).IsEqualTo($"dv={HostProviders.Version}");
        }
    }
}
