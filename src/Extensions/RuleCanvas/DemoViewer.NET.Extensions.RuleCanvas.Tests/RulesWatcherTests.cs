#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     External edits: the watcher batches what the directory reports and the view model decides what a
///     batch means, by comparing against the corpus it has and the write time the session read at.
/// </summary>
public class RulesWatcherTests
{
    private static readonly Func<Action, Task> Inline = action =>
    {
        action();
        return Task.CompletedTask;
    };

    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(40);

    // Written as another editor would, with a write time the session cannot have read.
    private static void WriteExternally(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
    }

    private static string Labelled(string path) => RulesetEdits.SetField(File.ReadAllText(path), "round_kills", "label", "Kills!");

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan patience)
    {
        DateTime deadline = DateTime.UtcNow + patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(25);
        }

        return true;
    }

    [Test]
    public async Task Notify_Debounces_IntoOneBatch_ThroughTheMarshal()
    {
        using RulesTree rules = RulesTree.New();
        List<IReadOnlyList<string>> batches = [];
        int marshalled = 0;
        using RulesWatcher watcher = new(rules.UserDirectory, action =>
        {
            marshalled++;
            return Inline(action);
        }, Short);
        watcher.Changed += paths =>
        {
            lock (batches)
            {
                batches.Add(paths);
            }
        };

        watcher.Notify(rules.UserPath("a"));
        watcher.Notify(rules.UserPath("b"));
        watcher.Notify(rules.UserPath("a"));
        await Assert.That(await WaitFor(() =>
        {
            lock (batches)
            {
                return batches.Count > 0;
            }
        }, TimeSpan.FromSeconds(5))).IsTrue();
        await Task.Delay(Short * 3);
        using (Assert.Multiple())
        {
            await Assert.That(batches.Count).IsEqualTo(1).Because("three notices inside the debounce are one batch");
            await Assert.That(batches[0]).IsEquivalentTo([rules.UserPath("a"), rules.UserPath("b")]);
            await Assert.That(marshalled).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AWriteInTheDirectory_IsReported()
    {
        using RulesTree rules = RulesTree.New();
        List<string> seen = [];
        using RulesWatcher watcher = new(rules.UserDirectory, Inline, Short);
        watcher.Changed += paths =>
        {
            lock (seen)
            {
                seen.AddRange(paths);
            }
        };
        await Assert.That(watcher.IsWatching).IsTrue();

        string path = rules.User("other", "ruleset: other\nstats: {}\n");
        bool reported = await WaitFor(() =>
        {
            lock (seen)
            {
                return seen.Any(p => string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
            }
        }, TimeSpan.FromSeconds(10));
        await Assert.That(reported).IsTrue().Because("the platform reports the file written");
    }

    [Test]
    public async Task AnExternalWrite_ToTheOpenFile_ReloadsIt_WhenThePaneIsClean()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        int loads = 0;
        using CanvasTabViewModel vm = new(_ => true, () =>
        {
            loads++;
            return rules.Load();
        }, () => rules.OwnFolder);
        vm.OnActivated(null!);
        string path = rules.UserPath("player_round");
        string text = Labelled(path);
        WriteExternally(path, text);

        vm.FilesChanged([path]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.DocumentText).IsEqualTo(text);
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Label).IsEqualTo("Kills!");
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(loads).IsEqualTo(1).Because("a known file is re-read alone, not the corpus");
        }

        // The file is the session's again: the canvas may write it.
        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        await Assert.That(File.ReadAllText(path)).Contains("headshot");
    }

    [Test]
    public async Task AnExternalWrite_WhileThePaneIsDirty_IsAStatusLine_AndRevertLoadsIt()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        using CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        string path = rules.UserPath("player_round");
        string shown = vm.DocumentText;
        vm.SourceEdited(shown + "# typing\n");
        string text = Labelled(path);
        WriteExternally(path, text);

        vm.FilesChanged([path]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo(CanvasTabViewModel.ChangedOnDiskStatus);
            await Assert.That(vm.DocumentText).IsEqualTo(shown).Because("the pane's text is not thrown away");
            await Assert.That(vm.SourceDirty).IsTrue();
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Label).IsNotEqualTo("Kills!");
        }

        vm.RevertSourceCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.EditStatus).IsEqualTo("");
            await Assert.That(vm.DocumentText).IsEqualTo(text);
            await Assert.That(vm.Nodes.Single(n => n.Id == "round_kills").Label).IsEqualTo("Kills!");
        }
    }

    [Test]
    public async Task TheCanvasOwnWrite_IsNotLoadedTwice()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        int loads = 0;
        using CanvasTabViewModel vm = new(_ => true, () =>
        {
            loads++;
            return rules.Load();
        }, () => rules.OwnFolder);
        vm.OnActivated(null!);
        int replaced = 0;
        vm.GraphReplaced += () => replaced++;
        vm.Nodes.Single(n => n.Id == "round_kills").FacetToAdd = "headshot";
        await Assert.That(replaced).IsEqualTo(1);

        vm.FilesChanged([rules.UserPath("player_round")]);
        using (Assert.Multiple())
        {
            await Assert.That(replaced).IsEqualTo(1).Because("the file holds what the session wrote");
            await Assert.That(loads).IsEqualTo(1);
        }
    }

    [Test]
    public async Task AFileThatAppears_OrGoes_IsAFullLoad()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        int loads = 0;
        using CanvasTabViewModel vm = new(_ => true, () =>
        {
            loads++;
            return rules.Load();
        }, () => rules.OwnFolder);
        vm.OnActivated(null!);
        string other = rules.User("other", "ruleset: other\ntitle: Other\nstats:\n  other_kills:\n    count: kill\n    per: round\n");

        vm.FilesChanged([other]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Rulesets.Select(e => e.Id)).Contains("other");
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_round").Because("the selection is kept");
            await Assert.That(loads).IsEqualTo(2);
        }

        File.Delete(other);
        vm.FilesChanged([other]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.Rulesets.Select(e => e.Id)).DoesNotContain("other");
            await Assert.That(loads).IsEqualTo(3);
        }
    }

    [Test]
    public async Task AUseDependencyWrittenOutside_RedrawsTheOpenRuleset()
    {
        using RulesTree rules = RulesetCorpusReplaceTests.CorpusWithUser("kast");
        using CanvasTabViewModel vm = rules.NewViewModel();
        vm.PreferredRulesetId = "player_stats";
        vm.OnActivated(null!);
        await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_stats");
        string kast = rules.UserPath("kast");
        string kastNode = vm.Nodes.First(n => n.Ruleset == "kast").Id;
        WriteExternally(kast, RulesetEdits.SetField(File.ReadAllText(kast), kastNode, "label", "From kast!"));

        vm.FilesChanged([kast]);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Id).IsEqualTo("player_stats");
            await Assert.That(vm.Nodes.Single(n => n.Ruleset == "kast" && n.Id == kastNode).Label).IsEqualTo("From kast!");
        }
    }

    [Test]
    public async Task TheViewModel_WatchesWhileActive_AndStopsWhenDeactivated()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        using CanvasTabViewModel vm = new(_ => true, rules.Load, () => rules.OwnFolder, Inline) { WatcherDebounce = Short };
        await Assert.That(vm.IsWatching).IsFalse();
        vm.OnActivated(null!);
        await Assert.That(vm.IsWatching).IsTrue();

        string path = rules.UserPath("player_round");
        string text = Labelled(path);
        WriteExternally(path, text);
        bool reloaded = await WaitFor(() => vm.DocumentText == text, TimeSpan.FromSeconds(10));
        await Assert.That(reloaded).IsTrue().Because("the watcher's report reaches the view model inline");

        vm.OnDeactivated();
        await Assert.That(vm.IsWatching).IsFalse();
    }

    [Test]
    public async Task WithoutAMarshal_NothingIsWatched()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        using CanvasTabViewModel vm = rules.NewViewModel();
        vm.OnActivated(null!);
        await Assert.That(vm.IsWatching).IsFalse().Because("a report run off the editing thread would race it");
    }
}
