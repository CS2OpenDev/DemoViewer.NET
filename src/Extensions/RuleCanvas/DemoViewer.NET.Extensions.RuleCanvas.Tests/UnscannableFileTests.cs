#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A user file the YAML scanner cannot read, such as an unclosed flow sequence: the loader throws
///     InvalidOperationException for it, not YamlException. It is a failing file of the corpus like any
///     other, on the first load and on a re-read, and the other rulesets still draw.
/// </summary>
public class UnscannableFileTests
{
    private const string Unscannable = "ruleset: [\nfor: each_player\nstats: {}\n";

    private const string Other = """
                                 ruleset: other
                                 title: Other
                                 stats:
                                   other_kills:
                                     count: kill
                                     per: round
                                 """;

    [Test]
    public async Task OpeningTheTab_ListsTheFileAsFailing_AndDrawsTheOthers()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string broken = rules.Broken("broken", Unscannable);
        CanvasTabViewModel vm = rules.NewViewModel();

        vm.OnActivated(null!);
        RulesetCorpus corpus = rules.Load();
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Failures.Select(f => Path.GetFileName(f.Path))).Contains(Path.GetFileName(broken));
            await Assert.That(vm.Rulesets.Select(r => r.Id)).Contains("player_round");
            await Assert.That(vm.Nodes).IsNotEmpty();
        }
    }

    [Test]
    public async Task Replace_OfAFileSavedUnscannable_MovesItToTheFailures()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.User("other", Other);
        RulesetCorpus corpus = rules.Load();

        File.WriteAllText(path, Unscannable);
        RulesetCorpus? again = corpus.Replace(path);

        using (Assert.Multiple())
        {
            await Assert.That(again).IsNotNull();
            await Assert.That(again!.Failures.Select(f => f.Path)).Contains(path);
            await Assert.That(again.Entries.Select(e => e.Id)).DoesNotContain("other");
            await Assert.That(again.Entries.Select(e => e.Id)).Contains("player_round");
        }
    }

    [Test]
    public async Task TheWatchersReport_OfAnotherFileSavedUnscannable_KeepsTheOpenRulesetDrawn()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.User("other", Other);
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        int nodes = vm.Nodes.Count;

        File.WriteAllText(path, Unscannable);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        vm.FilesChanged([path]);

        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_round");
            await Assert.That(vm.Nodes.Count).IsEqualTo(nodes);
            await Assert.That(vm.Rulesets.Select(r => r.Id)).DoesNotContain("other");
        }
    }

    // A watcher batch can still be debouncing, or held while the pane is dirty, when another file is saved broken.
    [Test]
    [Arguments("ruleset: [\nfor: each_player\nstats: {}\n")]
    [Arguments("ruleset: other\ndefine: [\n")]
    public async Task ARedraw_AfterAnotherUserFileBrokeOnDisk_DoesNotThrow_AndKeepsItsDefinesInTheGallery(string brokenText)
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        string path = rules.User("other", Other.Replace("stats:", "define:\n  armed:\n    on: kill\nstats:", StringComparison.Ordinal));
        CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        await Assert.That(vm.Gallery.SelectMany(g => g.Items).Select(i => i.Name)).Contains("armed");

        File.WriteAllText(path, brokenText);
        vm.FocusNode(vm.Nodes.Single(n => n.Id == "round_kills"));

        using (Assert.Multiple())
        {
            await Assert.That(vm.HasFocus).IsTrue();
            await Assert.That(vm.Gallery.SelectMany(g => g.Items).Select(i => i.Name)).Contains("armed")
                .Because("the gallery offers what the corpus loaded, until the watcher reports the change");
        }
    }
}
