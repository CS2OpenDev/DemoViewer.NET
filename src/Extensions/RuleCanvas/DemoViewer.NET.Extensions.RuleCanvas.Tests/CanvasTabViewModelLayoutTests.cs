#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The arrangement round trip through the view model: a moved node is written on drag completion, comes
///     back on the next open, and goes when reset. A user ruleset keeps its sidecar beside the file; a
///     shipped one keeps it under the extension's own folder, since the shipped directory is not ours to
///     write.
/// </summary>
public class CanvasTabViewModelLayoutTests
{
    private const string Ruleset = "player_round";

    [Test]
    public async Task AUserRuleset_KeepsItsArrangementBesideTheFile_AndResetDropsIt()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel first = tree.NewViewModel();
        first.OnActivated(null!);
        CanvasNodeViewModel moved = first.Nodes.Single(n => n.Id == "round_kills");
        Point computed = moved.Location;
        moved.Location = new Point(1234, 567);
        first.SaveLayoutCommand.Execute(null);

        string sidecar = RulesetLayoutStore.SidecarPathFor(tree.UserPath(Ruleset));
        await Assert.That(File.Exists(sidecar)).IsTrue().Because("a user ruleset's arrangement travels with the file");
        await Assert.That(first.HasSavedLayout).IsTrue();

        CanvasTabViewModel second = tree.NewViewModel();
        second.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(second.Nodes.Single(n => n.Id == "round_kills").Location).IsEqualTo(new Point(1234, 567));
            await Assert.That(second.Nodes.Single(n => n.Id == "round_deaths").Location)
                .IsNotEqualTo(new Point(1234, 567)).Because("only the moved node left its computed place");
            await Assert.That(second.HasSavedLayout).IsTrue();
        }

        second.ResetLayoutCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(sidecar)).IsFalse().Because("an empty arrangement is the absence of a file");
            await Assert.That(second.HasSavedLayout).IsFalse();
            await Assert.That(second.Nodes.Single(n => n.Id == "round_kills").Location).IsEqualTo(computed);
        }
    }

    [Test]
    public async Task ResetWhileFocused_IsRefused_AndTheWholeChainsArrangementStays()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        CanvasTabViewModel vm = tree.NewViewModel();
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").Location = new Point(1234, 567);
        vm.SaveLayoutCommand.Execute(null);
        string sidecar = RulesetLayoutStore.SidecarPathFor(tree.UserPath(Ruleset));
        string saved = File.ReadAllText(sidecar);

        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_deaths"));
        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsTrue();
            await Assert.That(vm.HasSavedLayout).IsTrue();
            await Assert.That(vm.CanResetLayout).IsFalse().Because("the menu item is off while a slice is focused");
        }

        vm.ResetLayoutCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Toast?.Message).IsEqualTo(CanvasTabViewModel.ResetNeedsWholeChain);
            await Assert.That(File.Exists(sidecar)).IsTrue();
            await Assert.That(File.ReadAllText(sidecar)).IsEqualTo(saved);
            await Assert.That(vm.HasFocus).IsTrue();
        }

        vm.ClearFocusCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Location).IsEqualTo(new Point(1234, 567));
            await Assert.That(vm.CanResetLayout).IsTrue();
        }
    }

    [Test]
    public async Task AShippedRuleset_KeepsItsArrangementInTheExtensionsOwnFolder()
    {
        using RulesTree tree = RulesTree.WithShipped(Ruleset);
        CanvasTabViewModel vm = tree.NewViewModel();
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").Location = new Point(10, 20);
        vm.SaveLayoutCommand.Execute(null);

        string expected = Path.Combine(tree.OwnFolder, "layouts", Ruleset + RulesetLayoutStore.SidecarSuffix);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SidecarPath).IsEqualTo(expected);
            await Assert.That(File.Exists(expected)).IsTrue();
            await Assert.That(File.Exists(RulesetLayoutStore.SidecarPathFor(tree.ShippedPath(Ruleset)))).IsFalse()
                .Because("nothing is written beside a shipped ruleset");
        }
    }

    [Test]
    public async Task WithNoOwnFolder_AShippedRuleset_IsNotSavedAndDoesNotThrow()
    {
        using RulesTree tree = RulesTree.WithShipped(Ruleset);
        CanvasTabViewModel vm = new(_ => true, tree.Load, () => null);
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").Location = new Point(10, 20);
        vm.SaveLayoutCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SidecarPath).IsNull();
            await Assert.That(vm.HasSavedLayout).IsFalse();
            await Assert.That(Directory.EnumerateFiles(tree.ShippedDirectory, "*.layout.json")).IsEmpty();
        }
    }

    [Test]
    public async Task AWrite_DoesNotAskForAFit_AndAFreshDrawDoes()
    {
        using RulesTree tree = RulesTree.WithUser(Ruleset);
        tree.User("other", "ruleset: other\ntitle: Other\nstats:\n  other_kills:\n    count: kill\n    per: round\n");
        CanvasTabViewModel vm = tree.NewViewModel();
        vm.OnActivated(null!);
        int fits = 0;
        vm.GraphReplaced += () => fits++;

        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        await Assert.That(fits).IsEqualTo(0).Because("a write keeps the arrangement and the viewport with it");
        vm.UndoCommand.Execute(null);
        await Assert.That(fits).IsEqualTo(0);

        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));
        await Assert.That(fits).IsEqualTo(1).Because("a slice is placed fresh");
        vm.ClearFocusCommand.Execute(null);
        await Assert.That(fits).IsEqualTo(2);
        vm.SelectedRuleset = vm.Rulesets.Single(r => r.Id == "other");
        await Assert.That(fits).IsEqualTo(3).Because("another ruleset is drawn fresh");
    }
}
