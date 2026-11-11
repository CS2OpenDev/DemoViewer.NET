#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     A user rules directory moved by <c>CS2DEMOKIT_USER_RULES_DIR</c> is labelled <c>testbed</c> in the
///     picker, and is still the user's directory in every other respect: editable, the target of a copy, and
///     the side of an override.
/// </summary>
public class TestbedSourceTests
{
    [Test]
    public async Task TheUserTag_IsTestbed_OnlyWhenAnEnvironmentVariableMovesTheDirectory()
    {
        static Func<string, string?> Env(params (string Name, string Value)[] set) =>
            name => set.FirstOrDefault(s => s.Name == name).Value;

        using (Assert.Multiple())
        {
            await Assert.That(RulesetCorpus.UserSourceTag(Env())).IsEqualTo("user");
            await Assert.That(RulesetCorpus.UserSourceTag(Env(("CS2DEMOKIT_USER_RULES_DIR", "/tmp/tb")))).IsEqualTo("testbed");
            await Assert.That(RulesetCorpus.UserSourceTag(Env(("DEMOVIEWER_USER_RULES_DIR", "/tmp/tb")))).IsEqualTo("testbed");
            await Assert.That(RulesetCorpus.UserSourceTag(Env(("CS2DEMOKIT_USER_RULES_DIR", "")))).IsEqualTo("user");
            await Assert.That(RulesetCorpus.UserSourceTag(Env(("CS2DEMOKIT_RULES_DIR", "/tmp/rules")))).IsEqualTo("user")
                .Because("moving the shipped rules is not a testbed");
        }
    }

    [Test]
    public async Task ATestbedDirectory_IsEditable_TakesCopies_AndOverridesByID()
    {
        using RulesTree rules = RulesTree.New();
        rules.ShippedCopyOf("player_round");
        rules.ShippedCopyOf("kast");
        rules.UserCopyOf("player_round");
        RulesetCorpus corpus = RulesetCorpus.Load([(rules.ShippedDirectory, "shipped"), (rules.UserDirectory, RulesetCorpus.TestbedSource)]);
        RulesetCorpus.Entry round = corpus.Entries.Single(e => e.Id == "player_round");
        using (Assert.Multiple())
        {
            await Assert.That(round.Source).IsEqualTo("testbed");
            await Assert.That(round.IsUser).IsTrue();
            await Assert.That(corpus.UserDirectory).IsEqualTo(rules.UserDirectory);
            await Assert.That(corpus.Overrides.Keys).IsEquivalentTo(["player_round"]);
            await Assert.That(corpus.CopyToUser(corpus.Entries.Single(e => e.Id == "kast")).IsRefused).IsFalse();
            await Assert.That(File.Exists(rules.UserPath("kast"))).IsTrue();
        }

        CanvasTabViewModel vm = new(_ => true, () => RulesetCorpus.Load([(rules.ShippedDirectory, "shipped"), (rules.UserDirectory, RulesetCorpus.TestbedSource)]),
            () => rules.OwnFolder) { PreferredRulesetId = "player_round" };
        vm.OnActivated(null!);
        using (Assert.Multiple())
        {
            await Assert.That(vm.SelectedRuleset?.Source).IsEqualTo("testbed").Because("the picker shows the source as the label");
            await Assert.That(vm.IsUserRuleset).IsTrue();
        }
    }

    [Test]
    public async Task AFailedTestbedFile_ReadAgain_KeepsItsTag()
    {
        using RulesTree rules = RulesTree.New();
        rules.ShippedCopyOf("kast");
        string path = rules.Broken("tb_x", "not: [yaml");
        RulesetCorpus corpus = RulesetCorpus.Load([(rules.ShippedDirectory, "shipped"), (rules.UserDirectory, RulesetCorpus.TestbedSource)]);
        File.WriteAllText(path, "ruleset: tb_x\nfor: each_player\nstats:\n  k:\n    count: kill\n    per: round\n");
        RulesetCorpus? again = corpus.Replace(path);
        await Assert.That(again?.Entries.Single(e => e.Id == "tb_x").Source).IsEqualTo("testbed");
    }
}
