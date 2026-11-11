#region

using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A param's type, min and max in the Inspector: with the params node selected each param is a row with a
///     type picker and two bound editors, every write checked the way the default is (the type the value
///     takes, the bounds against each other and against the default) before it reaches the file.
/// </summary>
public class ParamInspectorTests
{
    private const string Doc = """
        ruleset: pr
        for: each_player
        params:
          min_kills: { type: int, default: 2, min: 1, max: 5 }
          ratio: { type: float, default: 0.5 }
          window: { type: duration, default: 10s }
          loud:
            type: bool
            default: true
        stats:
          big:
            flag:
              when: "kills >= params.min_kills"
            per: round
          kills:
            count: kill
            per: round
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm, InspectorViewModel Inspector) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("pr", Doc);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "pr");
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.IsParams));
        return (rules, vm, vm.Inspector);
    }

    private static ParamEntryViewModel Param(InspectorViewModel inspector, string name) => inspector.Params.Single(p => p.Name == name);

    [Test]
    public async Task TheParamsNode_ListsEachParam_WithItsTypeAndBounds()
    {
        (RulesTree rules, CanvasTabViewModel vm, InspectorViewModel inspector) = Open();
        using (rules)
        {
            using (Assert.Multiple())
            {
                await Assert.That(inspector.HasParams).IsTrue();
                await Assert.That(inspector.Params.Select(p => p.Name)).IsEquivalentTo(["min_kills", "ratio", "window", "loud"]);
                await Assert.That(Param(inspector, "min_kills").Type.Text).IsEqualTo("int");
                await Assert.That(Param(inspector, "min_kills").Type.Options).IsEquivalentTo(["int", "float", "bool", "string", "duration"]);
                await Assert.That(Param(inspector, "min_kills").Min.Text).IsEqualTo("1");
                await Assert.That(Param(inspector, "min_kills").Max.Text).IsEqualTo("5");
                await Assert.That(Param(inspector, "min_kills").Default).IsEqualTo("2");
                await Assert.That(Param(inspector, "ratio").Min.Text).IsEmpty();
                await Assert.That(Param(inspector, "window").Type.Text).IsEqualTo("duration");
                await Assert.That(Param(inspector, "loud").Type.Text).IsEqualTo("bool");
                await Assert.That(inspector.Params.All(p => p.IsEditable && p.Type.IsEditable && p.Min.IsEditable)).IsTrue();
                await Assert.That(inspector.NodeFields.All(f => !f.IsVisible)).IsTrue().Because("the defaults stay rows on the node");
            }

            vm.SelectedNodes.Clear();
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "kills" && !n.IsScaffold));
            await Assert.That(inspector.HasParams).IsFalse().Because("only the params node shows the section");
        }
    }

    [Test]
    public async Task BoundsAndType_AreWritten_AndRefusedLikeTheDefault()
    {
        (RulesTree rules, CanvasTabViewModel vm, InspectorViewModel inspector) = Open();
        using (rules)
        {
            ParamEntryViewModel kills = Param(inspector, "min_kills");
            kills.Max.Text = "4";
            kills.Max.Commit();
            string text = File.ReadAllText(rules.UserPath("pr"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  min_kills: { type: int, default: 2, min: 1, max: 4 }\n");
                await Assert.That(vm.Nodes.Single(n => n.IsParams).Fields.Single(f => f.Key == "param.min_kills").Label).IsEqualTo("min_kills (int 1..4)");
                await Assert.That(Param(inspector, "min_kills").Max.Text).IsEqualTo("4").Because("the Inspector is shown again from the file");
            }

            kills = Param(inspector, "min_kills");
            kills.Max.Text = "1";
            kills.Max.Commit();
            await Assert.That(kills.Max.Error).Contains("default 2");
            kills.Min.Text = "9";
            kills.Min.Commit();
            await Assert.That(kills.Min.Error).Contains("above max 4");
            kills.Min.Text = "x";
            kills.Min.Commit();
            await Assert.That(kills.Min.Error).Contains("whole number");
            await Assert.That(File.ReadAllText(rules.UserPath("pr"))).IsEqualTo(text).Because("every refusal left the file alone");

            kills.Min.Text = "";
            kills.Min.Commit();
            text = File.ReadAllText(rules.UserPath("pr"));
            await Assert.That(text).Contains("  min_kills: { type: int, default: 2, max: 4 }\n");

            Param(inspector, "min_kills").Type.Text = "bool";
            await Assert.That(Param(inspector, "min_kills").Type.Error).Contains("min or max");
            Param(inspector, "min_kills").Type.Text = "float";
            text = File.ReadAllText(rules.UserPath("pr"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  min_kills: { type: float, default: 2, max: 4 }\n");
                await Assert.That(Param(inspector, "min_kills").Type.Text).IsEqualTo("float");
            }

            Param(inspector, "ratio").Type.Text = "bool";
            await Assert.That(Param(inspector, "ratio").Type.Error).Contains("true or false").Because("0.5 is no bool");
            Param(inspector, "ratio").Min.Text = "0";
            Param(inspector, "ratio").Min.Commit();
            text = File.ReadAllText(rules.UserPath("pr"));
            await Assert.That(text).Contains("  ratio: { type: float, default: 0.5, min: 0 }\n");

            ParamEntryViewModel loud = Param(inspector, "loud");
            loud.Max.Text = "3";
            loud.Max.Commit();
            await Assert.That(loud.Max.Error).Contains("has no max");
            loud.Type.Text = "string";
            text = File.ReadAllText(rules.UserPath("pr"));
            await Assert.That(text).Contains("  loud:\n    type: string\n    default: true\n").Because("a block param is rewritten in place");

            ParamEntryViewModel window = Param(inspector, "window");
            window.Max.Text = "30";
            window.Max.Commit();
            await Assert.That(File.ReadAllText(rules.UserPath("pr"))).Contains("  window: { type: duration, default: 10s, max: 30 }\n");
            await Assert.That(vm.EditStatus).IsEmpty();
        }
    }

    [Test]
    public async Task ParamRefusals_NameTheRule()
    {
        ParamDef kills = new("min_kills", ParamType.Int, 2L, 1, 5, new SourcePosition(null, 1, 1));
        ParamDef flag = new("loud", ParamType.Bool, true, null, null, new SourcePosition(null, 1, 1));
        using (Assert.Multiple())
        {
            await Assert.That(CanvasEditSemantics.ParamBoundRefusal(kills, "max", "4")).IsNull();
            await Assert.That(CanvasEditSemantics.ParamBoundRefusal(kills, "max", "1")).Contains("default 2");
            await Assert.That(CanvasEditSemantics.ParamBoundRefusal(kills, "min", "6")).Contains("above max 5");
            await Assert.That(CanvasEditSemantics.ParamBoundRefusal(kills, "min", "1.5")).Contains("whole number");
            await Assert.That(CanvasEditSemantics.ParamBoundRefusal(flag, "min", "1")).Contains("has no min");
            await Assert.That(CanvasEditSemantics.ParamTypeRefusal(kills, "min_kills", "float")).IsNull();
            await Assert.That(CanvasEditSemantics.ParamTypeRefusal(kills, "min_kills", "bool")).Contains("min or max");
            await Assert.That(CanvasEditSemantics.ParamTypeRefusal(flag, "loud", "int")).Contains("whole number");
            await Assert.That(CanvasEditSemantics.ParamTypeRefusal(flag, "loud", "string")).IsNull();
            await Assert.That(CanvasEditSemantics.ParamTypeRefusal(null, "x", "decimal")).Contains("not 'decimal'");
        }
    }

    // Every shipped param takes a bound and gives it back byte-identically, and its type is rewritten in
    // place; the file loads in between.
    [Test]
    public async Task ParamFields_RoundTrip_OverTheShippedCorpus()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        int parameters = 0;
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            string text = File.ReadAllText(entry.Path);
            foreach (string name in RulesetEdits.ParamNames(text))
            {
                parameters++;
                string? had = RulesetEdits.ParamField(text, name, "max");
                string bounded = RulesetEdits.SetParamField(text, name, "max", "99999");
                string typed = RulesetEdits.SetParamField(text, name, "type", "float");
                using (Assert.Multiple())
                {
                    await Assert.That(RulesetEdits.ParamField(bounded, name, "max")).IsEqualTo("99999").Because($"{entry.Id}.{name}");
                    await Assert.That(RulesetEdits.ParamField(typed, name, "type")).IsEqualTo("float").Because($"{entry.Id}.{name}");
                    await Assert.That(RulesetCorpus.WhyNotLoadable(bounded, entry.Path, entry.Id)).IsNull().Because($"{entry.Id}.{name}");
                    await Assert.That(RulesetEdits.SetParamField(typed, name, "type", RulesetEdits.ParamField(text, name, "type")!)).IsEqualTo(text).Because($"{entry.Id}.{name} type");
                    await Assert.That(had is null ? RulesetEdits.RemoveParamField(bounded, name, "max") : RulesetEdits.SetParamField(bounded, name, "max", had))
                        .IsEqualTo(text).Because($"{entry.Id}.{name} max");
                }
            }
        }

        await Assert.That(parameters).IsGreaterThan(8);
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.RemoveParamField(Doc, "ratio", "min")).IsEqualTo(Doc).Because("nothing to remove");
            await Assert.That(() => RulesetEdits.RemoveParamField(Doc, "ratio", "type")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetParamField(Doc, "ratio", "step", "1")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.SetParamField(Doc, "nope", "min", "1")).Throws<InvalidOperationException>();
        }
    }
}
