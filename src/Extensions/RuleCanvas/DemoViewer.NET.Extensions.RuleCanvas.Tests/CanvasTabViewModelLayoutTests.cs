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
        using TempCorpus corpus = TempCorpus.OfShipped(Ruleset, "user");
        CanvasTabViewModel first = NewViewModel(corpus, "user");
        first.OnActivated(null!);
        CanvasNodeViewModel moved = first.Nodes.Single(n => n.Id == "round_kills");
        Point computed = moved.Location;
        moved.Location = new Point(1234, 567);
        first.SaveLayoutCommand.Execute(null);

        string sidecar = RulesetLayoutStore.SidecarPathFor(corpus.RulesetPath);
        await Assert.That(File.Exists(sidecar)).IsTrue().Because("a user ruleset's arrangement travels with the file");
        await Assert.That(first.HasSavedLayout).IsTrue();

        CanvasTabViewModel second = NewViewModel(corpus, "user");
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
    public async Task AShippedRuleset_KeepsItsArrangementInTheExtensionsOwnFolder()
    {
        using TempCorpus corpus = TempCorpus.OfShipped(Ruleset, "shipped");
        CanvasTabViewModel vm = NewViewModel(corpus, "shipped");
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").Location = new Point(10, 20);
        vm.SaveLayoutCommand.Execute(null);

        string expected = Path.Combine(corpus.OwnFolder, "layouts", Ruleset + RulesetLayoutStore.SidecarSuffix);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SidecarPath).IsEqualTo(expected);
            await Assert.That(File.Exists(expected)).IsTrue();
            await Assert.That(File.Exists(RulesetLayoutStore.SidecarPathFor(corpus.RulesetPath))).IsFalse()
                .Because("nothing is written beside a shipped ruleset");
        }
    }

    [Test]
    public async Task WithNoOwnFolder_AShippedRuleset_IsNotSavedAndDoesNotThrow()
    {
        using TempCorpus corpus = TempCorpus.OfShipped(Ruleset, "shipped");
        CanvasTabViewModel vm = new(_ => true, () => corpus.Load("shipped"), () => null);
        vm.OnActivated(null!);
        vm.Nodes.Single(n => n.Id == "round_kills").Location = new Point(10, 20);
        vm.SaveLayoutCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SidecarPath).IsNull();
            await Assert.That(vm.HasSavedLayout).IsFalse();
            await Assert.That(Directory.EnumerateFiles(corpus.Directory, "*.layout.json")).IsEmpty();
        }
    }

    private static CanvasTabViewModel NewViewModel(TempCorpus corpus, string source) =>
        new(_ => true, () => corpus.Load(source), () => corpus.OwnFolder);

    /// <summary>One shipped ruleset copied into a throwaway directory, with an own folder beside it.</summary>
    private sealed class TempCorpus : IDisposable
    {
        private TempCorpus(string root, string rulesetPath)
        {
            Root = root;
            RulesetPath = rulesetPath;
        }

        public string Root { get; }

        public string Directory => Path.GetDirectoryName(RulesetPath)!;

        public string RulesetPath { get; }

        public string OwnFolder => Path.Combine(Root, "own");

        public static TempCorpus OfShipped(string id, string source)
        {
            string root = Path.Combine(Path.GetTempPath(), "rulecanvas-layout-" + Guid.NewGuid().ToString("N"));
            string dir = Path.Combine(root, source);
            System.IO.Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, id + ".rules.yaml");
            File.Copy(Path.Combine(RepoRoot(), "rules", id + ".rules.yaml"), target);
            return new TempCorpus(root, target);
        }

        public RulesetCorpus Load(string source) => RulesetCorpus.Load([(Directory, source)]);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is tolerable; a failing cleanup is not a failing test.
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
