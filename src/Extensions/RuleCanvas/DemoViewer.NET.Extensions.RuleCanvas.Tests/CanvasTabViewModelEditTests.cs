#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     Editing through the view model: a facet toggled on a node face reaches the file with its comments
///     intact, the redraw keeps the arrangement and the selection, undo puts the previous text back, and a
///     shipped ruleset refuses to be written until it is copied into the user's rules.
/// </summary>
public class CanvasTabViewModelEditTests
{
    [Test]
    public async Task TogglingAFacet_WritesTheFile_KeepsTheArrangement_AndUndoes()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.UserPath("player_round"));

        CanvasNodeViewModel kills = vm.Nodes.Single(n => n.Id == "round_kills");
        kills.Location = new Point(777, 88);
        vm.SelectedNodes.Add(kills);
        CanvasFacetViewModel enemy = kills.Facets.Single(f => f.Name == "enemy");
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsEditable).IsTrue();
            await Assert.That(kills.IsEditable).IsTrue();
            await Assert.That(enemy.IsEditable).IsTrue();
            await Assert.That(enemy.IsBool).IsTrue();
            await Assert.That(enemy.Toggle == true).IsTrue();
            await Assert.That(kills.AvailableFacets).Contains("headshot").Because("the kill view offers headshot and the file does not bind it");
        }

        enemy.Toggle = false;

        string after = File.ReadAllText(rules.UserPath("player_round"));
        CanvasNodeViewModel redrawn = vm.Nodes.Single(n => n.Id == "round_kills");
        using (Assert.Multiple())
        {
            await Assert.That(after).Contains("match: { enemy: false }");
            await Assert.That(after).Contains("# Player Round: one row per player per round").Because("the header comment survives the splice");
            await Assert.That(after.Length).IsEqualTo(before.Length + 1);
            await Assert.That(vm.DocumentText).IsEqualTo(after);
            await Assert.That(redrawn.Location).IsEqualTo(new Point(777, 88)).Because("a redraw after an edit keeps the arrangement");
            await Assert.That(vm.SelectedNodes).Contains(redrawn).Because("the selection follows the node by key");
            await Assert.That(redrawn.Facets.Single(f => f.Name == "enemy").Toggle == false).IsTrue();
            await Assert.That(vm.CanUndo).IsTrue();
            await Assert.That(vm.EditStatus).IsEmpty();
        }

        vm.UndoCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(vm.CanRedo).IsTrue();
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Facets.Single(f => f.Name == "enemy").Toggle == true).IsTrue();
        }
    }

    [Test]
    public async Task AddingAndRemovingAFacet_RoundTrips_ThroughTheNodeFace()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        string added = File.ReadAllText(rules.UserPath("player_round"));
        await Assert.That(added).Contains("match: { enemy: true, headshot: true }");

        vm.Nodes.Single(n => n.Id == "round_kills").Facets.Single(f => f.Name == "headshot").RemoveCommand.Execute(null);
        await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).Contains("match: { enemy: true }");
    }

    [Test]
    public async Task AddStatAndDelete_WriteThroughTheSplice()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.AddStatCommand.Execute("kill");
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Select(n => n.Id)).Contains("new_kill");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).Contains("new_kill:");
        }

        vm.SelectedNodes.Add(vm.Nodes.Single(n => n.Id == "new_kill"));
        vm.DeleteSelectedCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Nodes.Select(n => n.Id)).DoesNotContain("new_kill");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).DoesNotContain("new_kill:");
        }
    }

    [Test]
    public async Task AShippedRuleset_IsReadOnly_UntilCopied_AndTheCopyOverridesIt()
    {
        using TempRules rules = TempRules.WithShipped("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.IsShippedRuleset).IsTrue();
            await Assert.That(vm.IsEditable).IsFalse();
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").IsEditable).IsFalse();
        }

        vm.Nodes.Single(n => n.Id == "round_kills").Facets.Single(f => f.Name == "enemy").Toggle = false;
        await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).Contains("match: { enemy: true }")
            .Because("nothing writes into a shipped ruleset");

        vm.EditCopyCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.Exists(rules.UserPath("player_round"))).IsTrue();
            await Assert.That(vm.SelectedRuleset!.Source).IsEqualTo("user");
            await Assert.That(vm.Rulesets.Count(r => r.Id == "player_round")).IsEqualTo(1).Because("the copy overrides the shipped one by id");
            await Assert.That(vm.IsEditable).IsTrue();
        }
    }

    [Test]
    public async Task ADirtySourcePane_HoldsCanvasEdits_UntilSavedOrReverted()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        vm.SourceEdited(vm.DocumentText + "\n# typed in the pane\n");
        using (Assert.Multiple())
        {
            await Assert.That(vm.SourceDirty).IsTrue();
            await Assert.That(vm.IsEditable).IsFalse();
            await Assert.That(vm.Nodes[0].IsEditable).IsFalse();
        }

        vm.SaveSourceCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SourceDirty).IsFalse();
            await Assert.That(vm.IsEditable).IsTrue();
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).Contains("# typed in the pane");
            await Assert.That(vm.CanUndo).IsTrue();
        }
    }

    [Test]
    public async Task SavingTextThatNoLongerLoads_IsRefused_AndThePaneStaysDirty()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string before = File.ReadAllText(rules.UserPath("player_round"));

        vm.SourceEdited(vm.DocumentText.Replace("ruleset: player_round", "ruleset: player_rnd"));
        vm.SaveSourceCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before).Because("the file is left alone");
            await Assert.That(vm.SourceDirty).IsTrue();
            await Assert.That(vm.EditStatus).StartsWith("Not saved:");
            await Assert.That(vm.Rulesets.Select(r => r.Id)).Contains("player_round").Because("the ruleset never left the picker");
            await Assert.That(vm.Nodes.Count(n => n.IsAuthorable)).IsEqualTo(8);
        }

        vm.SourceEdited("stats:" + Environment.NewLine + "  - this is not a mapping" + Environment.NewLine);
        vm.SaveSourceCommand.Execute(null);
        await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
    }

    [Test]
    public async Task AFacetHoldingAListOrRange_IsRefused_NotDuplicated()
    {
        const string text = """
            ruleset: t
            for: each_player
            stats:
              kills:
                count: kill
                match: { hitgroup: [1..3] }
                per: round
            """;
        await Assert.That(() => RulesetEdits.SetFacet(text, "kills", "hitgroup", "2")).Throws<InvalidOperationException>();
    }

    /// <summary>A throwaway rules tree: a shipped directory, a user directory, and an own folder for sidecars.</summary>
    private sealed class TempRules : IDisposable
    {
        private readonly string _root;

        private TempRules(string root) => _root = root;

        private string Shipped => Path.Combine(_root, "shipped");

        private string User => Path.Combine(_root, "user");

        public string ShippedPath(string id) => Path.Combine(Shipped, id + ".rules.yaml");

        public string UserPath(string id) => Path.Combine(User, id + ".rules.yaml");

        public static TempRules WithUser(string id)
        {
            TempRules rules = New();
            File.Copy(Path.Combine(RepoRoot(), "rules", id + ".rules.yaml"), rules.UserPath(id));
            return rules;
        }

        public static TempRules WithShipped(string id)
        {
            TempRules rules = New();
            File.Copy(Path.Combine(RepoRoot(), "rules", id + ".rules.yaml"), rules.ShippedPath(id));
            return rules;
        }

        public CanvasTabViewModel NewViewModel() =>
            new(_ => true, () => RulesetCorpus.Load([(Shipped, "shipped"), (User, "user")]), () => Path.Combine(_root, "own"));

        private static TempRules New()
        {
            string root = Path.Combine(Path.GetTempPath(), "rulecanvas-edit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "shipped"));
            Directory.CreateDirectory(Path.Combine(root, "user"));
            return new TempRules(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is tolerable.
            }
        }

        private static string RepoRoot()
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DemoViewer.NET.slnx")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            throw new InvalidOperationException("repo root not found");
        }
    }
}
