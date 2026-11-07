#region

using System.Diagnostics;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The single-file reload: after a write the corpus re-reads that file alone and keeps every other
///     document as it was, with the override by id coming out as a full load would have it.
/// </summary>
public class RulesetCorpusReplaceTests
{
    private const string Other = """
                                 ruleset: other
                                 title: Other
                                 stats:
                                   other_kills:
                                     count: kill
                                     per: round
                                 """;

    /// <summary>The whole shipped corpus as the shipped directory, with <paramref name="id" /> copied into the user directory.</summary>
    internal static RulesTree CorpusWithUser(string id)
    {
        RulesTree tree = RulesTree.New();
        foreach (string path in Directory.EnumerateFiles(RulesTree.RepoRulesDirectory, "*.rules.yaml"))
        {
            File.Copy(path, Path.Combine(tree.ShippedDirectory, Path.GetFileName(path)));
        }

        tree.UserCopyOf(id);
        return tree;
    }

    [Test]
    public async Task Replace_ReReadsOneFile_AndKeepsTheOthers()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        rules.User("other", Other);
        RulesetCorpus corpus = rules.Load();
        object otherBefore = corpus.Compose("other").Documents["other"];

        string path = rules.UserPath("player_round");
        File.WriteAllText(path, RulesetEdits.SetField(File.ReadAllText(path), "round_kills", "label", "Kills!"));
        RulesetCorpus? again = corpus.Replace(path);

        await Assert.That(again).IsNotNull();
        RulesetCorpus.Composed composed = again!.Compose("player_round");
        using (Assert.Multiple())
        {
            await Assert.That(composed.Graph.Nodes.Single(n => n.Id == "round_kills").Label).IsEqualTo("Kills!");
            await Assert.That(again.Compose("other").Documents["other"]).IsSameReferenceAs(otherBefore).Because("the other file was not re-read");
            await Assert.That(again.Entries.Select(e => e.Id)).IsEquivalentTo(corpus.Entries.Select(e => e.Id));
            await Assert.That(corpus.Compose("player_round").Graph.Nodes.Single(n => n.Id == "round_kills").Label).IsNotEqualTo("Kills!")
                .Because("the corpus replaced is untouched");
        }
    }

    [Test]
    public async Task Replace_OfAFileThatNoLongerLoads_MovesItToFailures_AndBackWhenItLoadsAgain()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus corpus = rules.Load();
        string path = rules.UserPath("player_round");
        string good = File.ReadAllText(path);

        File.WriteAllText(path, "stats: [\n");
        RulesetCorpus broken = corpus.Replace(path)!;
        using (Assert.Multiple())
        {
            await Assert.That(broken.Entries.Select(e => e.Id)).DoesNotContain("player_round");
            await Assert.That(broken.Failures.Select(f => f.Path)).Contains(path);
            await Assert.That(broken.Knows(path)).IsTrue().Because("a failed file is still one the corpus read");
        }

        File.WriteAllText(path, good);
        RulesetCorpus fixedUp = broken.Replace(path)!;
        using (Assert.Multiple())
        {
            await Assert.That(fixedUp.Entries.Select(e => e.Id)).Contains("player_round");
            await Assert.That(fixedUp.Failures).IsEmpty();
        }
    }

    [Test]
    public async Task Replace_OfAPathTheCorpusNeverRead_IsNull()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus corpus = rules.Load();
        string appeared = rules.User("other", Other);
        using (Assert.Multiple())
        {
            await Assert.That(corpus.Knows(appeared)).IsFalse();
            await Assert.That((object?)corpus.Replace(appeared)).IsNull().Because("a file that appeared needs a full load");
        }
    }

    [Test]
    public async Task Replace_KeepsTheOverrideById_AndHandsTheShippedEntryBack()
    {
        using RulesTree rules = RulesTree.WithShipped("player_round");
        string userPath = rules.UserCopyOf("player_round");
        RulesetCorpus corpus = rules.Load();
        await Assert.That(corpus.Entries.Single(e => e.Id == "player_round").Source).IsEqualTo("user");

        File.WriteAllText(userPath, File.ReadAllText(userPath).Replace("ruleset: player_round", "ruleset: player_round_mine"));
        RulesetCorpus renamed = corpus.Replace(userPath)!;
        using (Assert.Multiple())
        {
            await Assert.That(renamed.Entries.Single(e => e.Id == "player_round").Source).IsEqualTo("shipped")
                .Because("the user file no longer carries the shipped id");
            await Assert.That(renamed.Entries.Single(e => e.Id == "player_round_mine").Source).IsEqualTo("user");
        }

        File.WriteAllText(userPath, File.ReadAllText(userPath).Replace("ruleset: player_round_mine", "ruleset: player_round"));
        RulesetCorpus back = renamed.Replace(userPath)!;
        using (Assert.Multiple())
        {
            await Assert.That(back.Entries.Count(e => e.Id == "player_round")).IsEqualTo(1);
            await Assert.That(back.Entries.Single(e => e.Id == "player_round").Source).IsEqualTo("user");
        }
    }

    [Test]
    [NotInParallel]
    public async Task APlayerStatsEdit_ReloadsOneFile_NotTheCorpus()
    {
        using RulesTree rules = CorpusWithUser("player_stats");
        (string, string)[] dirs = [(rules.ShippedDirectory, "shipped"), (rules.UserDirectory, "user")];
        RulesetCorpus corpus = RulesetCorpus.Load(dirs);
        CanvasGraphBuilder.Build(corpus.Compose("player_stats"));

        string path = rules.UserPath("player_stats");
        File.WriteAllText(path, RulesetEdits.SetField(File.ReadAllText(path), "total_deaths", "label", "Deaths"));

        Stopwatch clock = Stopwatch.StartNew();
        RulesetCorpus full = RulesetCorpus.Load(dirs);
        long fullLoadMs = clock.ElapsedMilliseconds;
        clock.Restart();
        (IReadOnlyList<CanvasNodeViewModel> fullNodes, _) = CanvasGraphBuilder.Build(full.Compose("player_stats"));
        long fullDrawMs = clock.ElapsedMilliseconds;

        clock.Restart();
        RulesetCorpus replaced = corpus.Replace(path)!;
        long replaceLoadMs = clock.ElapsedMilliseconds;
        clock.Restart();
        (IReadOnlyList<CanvasNodeViewModel> replacedNodes, _) = CanvasGraphBuilder.Build(replaced.Compose("player_stats"));
        long replaceDrawMs = clock.ElapsedMilliseconds;

        string note = $"player_stats edit: full load {fullLoadMs}ms + compose and place {fullDrawMs}ms = {fullLoadMs + fullDrawMs}ms before; "
                      + $"replace {replaceLoadMs}ms + compose and place {replaceDrawMs}ms = {replaceLoadMs + replaceDrawMs}ms after "
                      + $"({replaced.Entries.Count} rulesets, {replacedNodes.Count} nodes)";
        Console.WriteLine("[canvas] " + note);
        using (Assert.Multiple())
        {
            await Assert.That(replacedNodes.Select(n => n.Id)).IsEquivalentTo(fullNodes.Select(n => n.Id)).Because(note);
            await Assert.That(replacedNodes.Single(n => n.Id == "total_deaths").Label).IsEqualTo("Deaths");
            // The untouched documents are the same objects: one file was re-read, not the corpus.
            await Assert.That(replaced.Compose("kast").Documents["kast"]).IsSameReferenceAs(corpus.Compose("kast").Documents["kast"]);
            await Assert.That(replaced.Compose("player_round").Documents["player_round"])
                .IsSameReferenceAs(corpus.Compose("player_round").Documents["player_round"]);
            await Assert.That(replaced.Compose("player_stats").Documents["player_stats"])
                .IsNotSameReferenceAs(corpus.Compose("player_stats").Documents["player_stats"]);
        }
    }
}
