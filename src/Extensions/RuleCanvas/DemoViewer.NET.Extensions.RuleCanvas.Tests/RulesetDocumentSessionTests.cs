#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The session that owns the open file: edits land atomically and only in the user's own file, the
///     history belongs to the path and goes when another path opens, and a refused transform leaves both
///     stacks as they were.
/// </summary>
public class RulesetDocumentSessionTests
{
    private static RulesetCorpus.Entry UserEntry(TempRules rules, string id) =>
        rules.Load().Entries.Single(e => e.Id == id && e.Source == "user");

    [Test]
    public async Task AnEdit_WritesTheFile_AndUndoAndRedo_WalkTheHistory()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        string before = session.Text;

        EditOutcome written = session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", "false"), "set enemy");
        string after = File.ReadAllText(rules.UserPath("player_round"));
        using (Assert.Multiple())
        {
            await Assert.That(written.IsWritten).IsTrue();
            await Assert.That(after).Contains("match: { enemy: false }");
            await Assert.That(session.Text).IsEqualTo(after);
            await Assert.That(session.CanUndo).IsTrue();
            await Assert.That(session.CanRedo).IsFalse();
            await Assert.That(File.Exists(rules.UserPath("player_round") + ".tmp")).IsFalse().Because("the temp file moved into place");
        }

        await Assert.That(session.Undo().IsWritten).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
            await Assert.That(session.CanUndo).IsFalse();
            await Assert.That(session.CanRedo).IsTrue();
        }

        await Assert.That(session.Redo().IsWritten).IsTrue();
        using (Assert.Multiple())
        {
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(after);
            await Assert.That(session.CanUndo).IsTrue();
            await Assert.That(session.CanRedo).IsFalse();
        }
    }

    [Test]
    public async Task UndoAfterThePathChanged_DoesNotWrite()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        File.Copy(rules.UserPath("player_round"), rules.UserPath("other"));
        File.WriteAllText(rules.UserPath("other"), File.ReadAllText(rules.UserPath("other")).Replace("ruleset: player_round", "ruleset: other"));
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", "false"), "set enemy");
        string edited = File.ReadAllText(rules.UserPath("player_round"));

        session.Open(UserEntry(rules, "other"));
        string other = File.ReadAllText(rules.UserPath("other"));
        EditOutcome undone = session.Undo();
        using (Assert.Multiple())
        {
            await Assert.That(session.CanUndo).IsFalse().Because("the history belongs to the path that was open");
            await Assert.That(undone.IsRefused).IsTrue();
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(edited);
            await Assert.That(File.ReadAllText(rules.UserPath("other"))).IsEqualTo(other);
        }

        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(session.CanUndo).IsFalse();
    }

    [Test]
    public async Task ReopeningTheSamePath_KeepsTheHistory()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", "false"), "set enemy");

        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(session.CanUndo).IsTrue();
    }

    [Test]
    public async Task ARefusedTransform_LeavesTheStacksAlone()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", "false"), "set enemy");
        session.Undo();
        string text = File.ReadAllText(rules.UserPath("player_round"));

        EditOutcome refused = session.Apply(t => RulesetEdits.SetFacet(t, "no_such_stat", "enemy", "true"), "set no_such_stat.match.enemy");
        EditOutcome unchanged = session.Apply(t => t, "nothing");
        using (Assert.Multiple())
        {
            await Assert.That(refused.IsRefused).IsTrue();
            await Assert.That(refused.Message).StartsWith("Could not set no_such_stat.match.enemy:");
            await Assert.That(unchanged.Kind).IsEqualTo(EditOutcomeKind.Unchanged);
            await Assert.That(session.CanUndo).IsFalse();
            await Assert.That(session.CanRedo).IsTrue().Because("neither a refusal nor a no-op clears the redo");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(text);
        }
    }

    [Test]
    public async Task AShippedFile_IsNeverWritten_NotEvenBySave()
    {
        using TempRules rules = TempRules.WithShipped("player_round");
        RulesetDocumentSession session = new();
        session.Open(rules.Load().Entries.Single(e => e.Id == "player_round"));
        string before = File.ReadAllText(rules.ShippedPath("player_round"));

        EditOutcome edit = session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", "false"), "set enemy");
        EditOutcome save = session.Save(before + "\n# typed\n");
        using (Assert.Multiple())
        {
            await Assert.That(edit.IsRefused).IsTrue();
            await Assert.That(save.IsRefused).IsTrue();
            await Assert.That(session.CanUndo).IsFalse();
            await Assert.That(File.ReadAllText(rules.ShippedPath("player_round"))).IsEqualTo(before);
        }
    }

    [Test]
    public async Task Save_RefusesTextThatNoLongerLoads_WithTheReason()
    {
        using TempRules rules = TempRules.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        string before = session.Text;

        EditOutcome renamed = session.Save(before.Replace("ruleset: player_round", "ruleset: player_rnd"));
        using (Assert.Multiple())
        {
            await Assert.That(renamed.IsRefused).IsTrue();
            await Assert.That(renamed.Message).StartsWith("Not saved:");
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(before);
        }

        await Assert.That(session.Save(before + "# typed in the pane\n").IsWritten).IsTrue();
        await Assert.That(session.CanUndo).IsTrue();
    }
}
