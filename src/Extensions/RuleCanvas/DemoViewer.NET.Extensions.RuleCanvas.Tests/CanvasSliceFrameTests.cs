#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Frames on a focused slice: a saved frame whose members are all in the slice is drawn around the
///     slice's own positions and cannot be changed; one with a member outside the slice is hidden; and
///     grouping while focused does nothing but say why.
/// </summary>
public class CanvasSliceFrameTests
{
    private static void Frame(CanvasTabViewModel vm, params string[] ids)
    {
        vm.SelectedNodes.Clear();
        foreach (string id in ids)
        {
            vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == id));
        }

        vm.GroupSelectionCommand.Execute(null);
        vm.SelectedNodes.Clear();
    }

    [Test]
    public async Task AFrameInsideTheSlice_IsDrawnAroundTheSlicesPositions_AndOneReachingOutsideIsHidden()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        Frame(vm, "round_kills", "round_kast_assists");
        Frame(vm, "round_damage", "round_deaths");
        await Assert.That(vm.Groups.Count).IsEqualTo(2);
        string sidecar = RulesetLayoutStore.SidecarPathFor(rules.UserPath("player_round"));
        string saved = File.ReadAllText(sidecar);

        // round_kast's slice holds round_kills and round_kast_assists, not round_damage.
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kast"));
        CanvasGroupViewModel frame = vm.Groups.Single();
        Rect kills = new(vm.Nodes.Single(n => n.Id == "round_kills").Location, vm.Nodes.Single(n => n.Id == "round_kills").EstimatedSize);
        Rect assists = new(vm.Nodes.Single(n => n.Id == "round_kast_assists").Location, vm.Nodes.Single(n => n.Id == "round_kast_assists").EstimatedSize);
        using (Assert.Multiple())
        {
            await Assert.That(frame.Members.Select(m => m.Id)).IsEquivalentTo(["round_kills", "round_kast_assists"]);
            await Assert.That(frame.IsReadOnly).IsTrue();
            await Assert.That(frame.Rect.Contains(kills.TopLeft) && frame.Rect.Contains(kills.BottomRight)).IsTrue()
                .Because($"the frame {frame.Rect} holds round_kills at {kills} where the slice placed it");
            await Assert.That(frame.Rect.Contains(assists.TopLeft) && frame.Rect.Contains(assists.BottomRight)).IsTrue();
            await Assert.That(vm.Items.OfType<CanvasGroupViewModel>().Count()).IsEqualTo(1).Because("the frame reaching round_damage is hidden");
            await Assert.That(vm.Items.IndexOf(frame)).IsEqualTo(0).Because("frames are drawn under the nodes");
        }

        // Nothing on the slice is written: the frame's title stays as it is and the sidecar is untouched.
        frame.BeginEditTitleCommand.Execute(null);
        await Assert.That(frame.IsEditingTitle).IsFalse();
        vm.SaveLayoutCommand.Execute(null);
        await Assert.That(File.ReadAllText(sidecar)).IsEqualTo(saved);

        vm.ClearFocusCommand.Execute(null);
        await Assert.That(vm.Groups.Count).IsEqualTo(2).Because("the whole chain draws both frames from the sidecar again");
    }

    [Test]
    public async Task GroupingWhileFocused_DoesNothing_AndSaysWhy()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kast"));
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kills"));
        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "round_kast_assists"));

        vm.GroupSelectionCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Groups).IsEmpty();
            await Assert.That(vm.Toast?.Message).IsEqualTo(CanvasTabViewModel.FramesReadOnlyWhileFocused);
            await Assert.That(File.Exists(RulesetLayoutStore.SidecarPathFor(rules.UserPath("player_round")))).IsFalse();
        }
    }
}
