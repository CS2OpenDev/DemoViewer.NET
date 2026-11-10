#region

using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The ownership guard on every write path: a file another writer touched since the session read it
///     is not written over, not by an edit, a save, an undo or a redo, and the history survives the refusal.
/// </summary>
public class RulesetDocumentSessionGuardTests
{
    private static RulesetCorpus.Entry UserEntry(RulesTree rules, string id) =>
        rules.Load().Entries.Single(e => e.Id == id && e.Source == "user");

    private static EditOutcome SetEnemy(RulesetDocumentSession session, string value) =>
        session.Apply(text => RulesetEdits.SetFacet(text, "round_kills", "enemy", value), "set enemy");

    // Written as another editor would, with a write time the session cannot have read.
    private static void WriteExternally(string path, string text)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
    }

    [Test]
    public async Task AnEdit_AfterAnExternalWrite_IsRefused_WithTheMessage()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        string path = rules.UserPath("player_round");
        string external = session.Text + "# theirs\n";
        WriteExternally(path, external);

        EditOutcome edit = SetEnemy(session, "false");
        using (Assert.Multiple())
        {
            await Assert.That(session.ChangedOnDisk).IsTrue();
            await Assert.That(edit.IsRefused).IsTrue();
            await Assert.That(edit.Message).IsEqualTo(RulesetDocumentSession.ChangedOnDiskMessage);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(external);
            await Assert.That(session.CanUndo).IsFalse();
        }

        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue().Because("re-reading the file makes it the session's again");
    }

    [Test]
    public async Task Undo_AfterAnExternalWrite_IsRefused_AndKeepsTheHistory()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue();
        string path = rules.UserPath("player_round");
        string external = session.Text + "# theirs\n";
        WriteExternally(path, external);

        EditOutcome undone = session.Undo();
        using (Assert.Multiple())
        {
            await Assert.That(undone.IsRefused).IsTrue();
            await Assert.That(undone.Message).IsEqualTo(RulesetDocumentSession.ChangedOnDiskMessage);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(external);
            await Assert.That(session.CanUndo).IsTrue().Because("the refused undo is still there to take once the file is re-read");
            await Assert.That(session.CanRedo).IsFalse();
        }
    }

    [Test]
    public async Task Redo_AfterAnExternalWrite_IsRefused_AndKeepsTheHistory()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue();
        await Assert.That(session.Undo().IsWritten).IsTrue();
        string path = rules.UserPath("player_round");
        string external = session.Text + "# theirs\n";
        WriteExternally(path, external);

        EditOutcome redone = session.Redo();
        using (Assert.Multiple())
        {
            await Assert.That(redone.IsRefused).IsTrue();
            await Assert.That(redone.Message).IsEqualTo(RulesetDocumentSession.ChangedOnDiskMessage);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(external);
            await Assert.That(session.CanRedo).IsTrue();
            await Assert.That(session.CanUndo).IsFalse();
        }
    }

    [Test]
    public async Task Save_AfterAnExternalWrite_IsRefused()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        string path = rules.UserPath("player_round");
        string mine = session.Text + "# mine\n";
        string external = session.Text + "# theirs\n";
        WriteExternally(path, external);

        EditOutcome saved = session.Save(mine);
        using (Assert.Multiple())
        {
            await Assert.That(saved.IsRefused).IsTrue();
            await Assert.That(saved.Message).IsEqualTo(RulesetDocumentSession.ChangedOnDiskMessage);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(external);
        }
    }

    [Test]
    public async Task Redo_AfterThePathChanged_DoesNotWrite()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        File.WriteAllText(rules.UserPath("other"), File.ReadAllText(rules.UserPath("player_round")).Replace("ruleset: player_round", "ruleset: other"));
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue();
        await Assert.That(session.Undo().IsWritten).IsTrue();
        string playerRound = File.ReadAllText(rules.UserPath("player_round"));

        session.Open(UserEntry(rules, "other"));
        string other = File.ReadAllText(rules.UserPath("other"));
        EditOutcome redone = session.Redo();
        using (Assert.Multiple())
        {
            await Assert.That(session.CanRedo).IsFalse().Because("the history belongs to the path that was open");
            await Assert.That(redone.IsRefused).IsTrue();
            await Assert.That(File.ReadAllText(rules.UserPath("player_round"))).IsEqualTo(playerRound);
            await Assert.That(File.ReadAllText(rules.UserPath("other"))).IsEqualTo(other);
        }
    }

    [Test]
    public async Task AWrite_AfterTheFileWentAway_IsRefused_AndNothingIsCreated()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetDocumentSession session = new();
        session.Open(UserEntry(rules, "player_round"));
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue();
        string path = rules.UserPath("player_round");
        File.Delete(path);

        EditOutcome edit = SetEnemy(session, "true");
        EditOutcome undone = session.Undo();
        using (Assert.Multiple())
        {
            await Assert.That(edit.IsRefused).IsTrue();
            await Assert.That(edit.Message).Contains("no longer on disk");
            await Assert.That(undone.IsRefused).IsTrue();
            await Assert.That(File.Exists(path)).IsFalse().Because("a refused write does not bring the file back");
            await Assert.That(session.CanUndo).IsTrue();
        }
    }

    // FileShare.None is how another process holds a file mid-save; .NET enforces it within this process on every OS.
    [Test]
    public async Task AReadThatFailed_LeavesTheSessionUnwritable_UntilAReopenReads()
    {
        using RulesTree rules = RulesTree.WithUser("player_round");
        RulesetCorpus.Entry entry = UserEntry(rules, "player_round");
        string path = rules.UserPath("player_round");
        string original = File.ReadAllText(path);
        RulesetDocumentSession session = new();
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            session.Open(entry);
        }

        await Assert.That(session.Text).IsEmpty().Because("the read failed, which the rest of the test depends on");
        EditOutcome edit = session.Apply(text => RulesetEdits.AddDefine(text, "armed", "on: kill"), "add define armed");
        using (Assert.Multiple())
        {
            await Assert.That(edit.IsRefused).IsTrue();
            await Assert.That(edit.Message).IsNotNull();
            await Assert.That(File.ReadAllText(path)).IsEqualTo(original);
            await Assert.That(session.Save("ruleset: player_round\n").IsRefused).IsTrue();
            await Assert.That(File.ReadAllText(path)).IsEqualTo(original);
        }

        session.Open(entry);
        await Assert.That(SetEnemy(session, "false").IsWritten).IsTrue().Because("a read that succeeds makes the file the session's again");
    }
}
