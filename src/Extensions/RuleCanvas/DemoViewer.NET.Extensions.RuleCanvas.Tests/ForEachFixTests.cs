#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A <c>for_each:</c> on an entry whose id has no <c>{key}</c> hole used to be refused and left there.
///     The Inspector now offers the rename that gives the id its hole as the fix, and the fix renames the
///     entry and writes the axes as one edit and one Undo. An id that already holds a different hole is
///     still refused, and an entry a sibling or <c>show:</c> names cannot become a template.
/// </summary>
public class ForEachFixTests
{
    private const string Doc = """
        ruleset: fe
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          deaths:
            count: death
            per: round
          kd:
            compute: "deaths + 1"
            per: match
          shots_{w}:
            count: shot
            per: round
            for_each: { w: [ak47, awp] }
        """;

    private static (RulesTree Rules, CanvasTabViewModel Vm) Open()
    {
        RulesTree rules = RulesTree.New();
        rules.User("fe", Doc);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "fe");
        return (rules, vm);
    }

    private static CanvasNodeViewModel Select(CanvasTabViewModel vm, string id)
    {
        CanvasNodeViewModel node = vm.Nodes.Single(n => n.Id == id && !n.IsScaffold);
        vm.SelectedNodes.Clear();
        vm.SelectedNodes.Add(node);
        return node;
    }

    [Test]
    public async Task ForEachIdFix_AppendsTheMissingHoles_AndRefusesAForeignOne()
    {
        using (Assert.Multiple())
        {
            await Assert.That(RulesetEdits.ForEachIdFix("kills", ["weapon"])).IsEqualTo("kills_{weapon}");
            await Assert.That(RulesetEdits.ForEachIdFix("kills", ["weapon", "side"])).IsEqualTo("kills_{weapon}_{side}");
            await Assert.That(RulesetEdits.ForEachIdFix("kills_{weapon}", ["weapon", "side"])).IsEqualTo("kills_{weapon}_{side}");
            await Assert.That(RulesetEdits.ForEachIdFix("kills_{weapon}", ["weapon"])).IsNull().Because("nothing to fix");
            await Assert.That(() => RulesetEdits.ForEachIdFix("kills_{weapon}", ["side"])).Throws<InvalidOperationException>()
                .WithMessageContaining("{side}");
            await Assert.That(RulesetEdits.HolesOf("a_{x}_{y}")).IsEquivalentTo(["x", "y"]);
            await Assert.That(RulesetEdits.HolesOf("plain")).IsEmpty();
            await Assert.That(RulesetEdits.IsValidTemplateId("kills_{weapon}")).IsTrue();
            await Assert.That(RulesetEdits.IsValidTemplateId("kills")).IsTrue();
            await Assert.That(RulesetEdits.IsValidTemplateId("kills_{1x}")).IsFalse();
            await Assert.That(RulesetEdits.IsValidTemplateId("kills_{weapon")).IsFalse();
            await Assert.That(RulesetEdits.IsValidTemplateId("{weapon}_kills")).IsTrue();
            await Assert.That(RulesetEdits.IsValidId("kills_{weapon}")).IsFalse().Because("the plain rename still refuses a template");
        }
    }

    [Test]
    public async Task RenameEntry_TakesATemplateId_AndRoundTrips()
    {
        string renamed = RulesetEdits.RenameEntry(Doc, EntrySection.Stat, "kills", "kills_{weapon}");
        string expanded = RulesetEdits.SetForEach(renamed, EntrySection.Stat, "kills_{weapon}", RulesetEdits.ParseForEach("weapon: ak47, awp"));
        using (Assert.Multiple())
        {
            await Assert.That(renamed).Contains("  kills_{weapon}:\n    count: kill\n    per: round\n");
            await Assert.That(expanded).Contains("  kills_{weapon}:\n    count: kill\n    per: round\n    for_each: { weapon: [ak47, awp] }\n");
            await Assert.That(RulesetEdits.RenameEntry(RulesetEdits.RemoveForEach(expanded, EntrySection.Stat, "kills_{weapon}"), EntrySection.Stat, "kills_{weapon}", "kills"))
                .IsEqualTo(Doc);
            await Assert.That(() => RulesetEdits.RenameStat(Doc, "kills", "kills_{weapon}")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.RenameEntry(Doc, EntrySection.Stat, "kills", "kills_{1}")).Throws<InvalidOperationException>();
            await Assert.That(() => RulesetEdits.RenameEntry(Doc, EntrySection.Stat, "deaths", "deaths_{w}")).Throws<InvalidOperationException>()
                .WithMessageContaining("read by kd");
            await Assert.That(RulesetEdits.RenameEntry(Doc, EntrySection.Stat, "deaths", "died")).Contains("compute: \"died + 1\"")
                .Because("a plain rename still rewrites the reader");
        }
    }

    [Test]
    public async Task TheInspector_OffersTheRename_AndTheFixExpandsTheEntryInOneUndo()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            CanvasNodeViewModel kills = Select(vm, "kills");
            kills.Location = new Avalonia.Point(777, 333);
            vm.SaveLayoutCommand.Execute(null);
            await Assert.That(inspector.HasForEachFix).IsFalse();

            inspector.ForEach.Text = "weapon: ak47, awp";
            inspector.ForEach.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(inspector.ForEach.Error).Contains("{weapon}");
                await Assert.That(inspector.HasForEachFix).IsTrue();
                await Assert.That(inspector.ForEachFixLabel).IsEqualTo("Rename id to kills_{weapon}");
                await Assert.That(File.ReadAllText(rules.UserPath("fe"))).IsEqualTo(Doc).Because("nothing is written until the fix is taken");
            }

            inspector.ApplyForEachFixCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("fe"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  kills_{weapon}:\n    count: kill\n    per: round\n    for_each: { weapon: [ak47, awp] }\n");
                await Assert.That(text).DoesNotContain("\n  kills:\n");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("kills_ak47");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("kills_awp");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Id).IsEqualTo("kills_ak47").Because("the Inspector follows the first expansion");
                await Assert.That(vm.Nodes.Single(n => n.Id == "kills_ak47").Location).IsEqualTo(new Avalonia.Point(777, 333)).Because("the arrangement follows it too");
                await Assert.That(inspector.HasForEachFix).IsFalse();
                await Assert.That(inspector.ForEach.Text).IsEqualTo("weapon: ak47, awp");
                await Assert.That(inspector.ForEach.Error).IsEmpty();
                await Assert.That(vm.EditStatus).IsEmpty();
            }

            vm.UndoCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(File.ReadAllText(rules.UserPath("fe"))).IsEqualTo(Doc).Because("the rename and the for_each are one edit");
                await Assert.That(vm.Nodes.Any(n => n.Id == "kills" && !n.IsScaffold)).IsTrue();
            }
        }
    }

    [Test]
    public async Task TheFix_IsRefused_WhenASiblingReadsTheEntry_AndADifferentHole_OffersNoFix()
    {
        (RulesTree rules, CanvasTabViewModel vm) = Open();
        using (rules)
        {
            InspectorViewModel inspector = vm.Inspector;
            Select(vm, "deaths");
            inspector.ForEach.Text = "weapon: ak47";
            inspector.ForEach.Commit();
            await Assert.That(inspector.ForEachFixLabel).IsEqualTo("Rename id to deaths_{weapon}");

            inspector.ApplyForEachFixCommand.Execute(null);
            using (Assert.Multiple())
            {
                await Assert.That(inspector.ForEach.Error).Contains("read by kd");
                await Assert.That(File.ReadAllText(rules.UserPath("fe"))).IsEqualTo(Doc);
                await Assert.That(vm.Nodes.Any(n => n.Id == "deaths" && !n.IsScaffold)).IsTrue();
            }

            Select(vm, "shots_ak47");
            inspector.ForEach.Text = "side: t, ct";
            inspector.ForEach.Commit();
            using (Assert.Multiple())
            {
                await Assert.That(inspector.ForEach.Error).Contains("{w}");
                await Assert.That(inspector.ForEach.Error).Contains("{side}");
                await Assert.That(inspector.HasForEachFix).IsFalse().Because("an id holding a hole no axis names is refused outright");
                await Assert.That(File.ReadAllText(rules.UserPath("fe"))).IsEqualTo(Doc);
            }

            // Adding an axis to a template keeps the hole it has and offers the one it lacks.
            inspector.ForEach.Text = "w: ak47, awp; side: t, ct";
            inspector.ForEach.Commit();
            await Assert.That(inspector.ForEachFixLabel).IsEqualTo("Rename id to shots_{w}_{side}");
            inspector.ApplyForEachFixCommand.Execute(null);
            string text = File.ReadAllText(rules.UserPath("fe"));
            using (Assert.Multiple())
            {
                await Assert.That(text).Contains("  shots_{w}_{side}:\n    count: shot\n    per: round\n    for_each: { w: [ak47, awp], side: [t, ct] }\n");
                await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("shots_awp_ct");
                await Assert.That(vm.SelectedNodes.OfType<CanvasNodeViewModel>().Single().Id).IsEqualTo("shots_ak47_t");
            }
        }
    }

    // Every shipped stat nobody reads or shows can become a one-axis template and come back byte-identical,
    // and the expansion loads; one a sibling or show: names is refused, since a template has no name to read.
    [Test]
    public async Task RenameToATemplateAndSetForEach_RoundTrip_OverTheShippedCorpus()
    {
        RulesetCorpus corpus = RulesetCorpus.Load([(RulesTree.RepoRulesDirectory, "shipped")]);
        int entries = 0, loadable = 0, refused = 0;
        foreach (RulesetCorpus.Entry entry in corpus.Entries)
        {
            string text = File.ReadAllText(entry.Path);
            ShowSurface surface = ShowSurface.Of(corpus.Compose(entry.Id).Open);
            bool checkedLoad = false;
            // One refusal per ruleset: the splice is the same for every read stat, and a parse per stat adds up.
            int read = 0;
            foreach (string id in RulesetEdits.StatIds(text))
            {
                if (id.Contains('{') || RulesetEdits.ForEachOf(text, EntrySection.Stat, id).Count > 0)
                {
                    continue;
                }

                if (RulesetEdits.SiblingReaders(text, id).Count > 0 || surface.ReferencesTo(id).Count > 0)
                {
                    if (read++ > 0)
                    {
                        continue;
                    }

                    refused++;
                    string refusal = "";
                    try
                    {
                        RulesetEdits.RenameEntry(text, EntrySection.Stat, id, id + "_{w}");
                    }
                    catch (InvalidOperationException ex)
                    {
                        refusal = ex.Message;
                    }

                    await Assert.That(refusal).Contains("a template has no name").Because($"{entry.Id}.{id}: {refusal}");
                    continue;
                }

                entries++;
                string template = RulesetEdits.ForEachIdFix(id, ["w"])!;
                string expanded = RulesetEdits.SetForEach(RulesetEdits.RenameEntry(text, EntrySection.Stat, id, template), EntrySection.Stat, template, [("w", ["a", "b"])]);
                using (Assert.Multiple())
                {
                    await Assert.That(RulesetEdits.StatIds(expanded)).Contains(template).Because($"{entry.Id}.{id}");
                    await Assert.That(RulesetEdits.RenameEntry(RulesetEdits.RemoveForEach(expanded, EntrySection.Stat, template), EntrySection.Stat, template, id))
                        .IsEqualTo(text).Because($"{entry.Id}.{id} round-trips");
                }

                if (!checkedLoad)
                {
                    checkedLoad = true;
                    loadable++;
                    await Assert.That(RulesetCorpus.WhyNotLoadable(expanded, entry.Path, entry.Id)).IsNull().Because($"{entry.Id}.{id}");
                }
            }
        }

        Console.WriteLine($"[for_each] {entries} unread shipped stats became a template and came back, {loadable} expansions loaded, {refused} read stats refused");
        using (Assert.Multiple())
        {
            await Assert.That(entries).IsGreaterThan(5);
            await Assert.That(loadable).IsGreaterThan(2);
            await Assert.That(refused).IsGreaterThan(10);
        }
    }
}
