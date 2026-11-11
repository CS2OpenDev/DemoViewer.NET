#region

using DemoViewer.NET.Extensions.StratBook;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Services.DemoCache;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     Scouting Report Editing And Export: the exporter's two forms and two files, the
///     notes store's persistence, the editor's star, rewrite, leave-out and note actions, and the tab
///     collecting its sections' lines, with the one-pager as the default export.
/// </summary>
[NotInParallel]
public class ScoutingReportEditingTests
{
    private static readonly Func<Action, Task> _inline = a =>
    {
        a();
        return Task.CompletedTask;
    };

    private static readonly byte[] _png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    private static ScoutingReportDocument Doc(params ScoutingReportFinding[] findings) => new()
    {
        TeamName = "Falcons <B>",
        SampleLine = "6 demos across 2 maps",
        Summary = "They default A on pistols.",
        Findings = findings
    };

    private static ScoutingReportFinding Line(string key, string section, string text, bool starred = false) =>
        new()
        {
            Key = key,
            Section = section,
            Text = text,
            IsStarred = starred
        };

    private const string NukeKey = "de_nuke||pool|||record";
    private const string MirageKey = "de_mirage||pool|||record";
    private const string SetupKey = "de_nuke|CT|setups|Full||read";

    private static ScoutingReportFindingSource[] Sources() =>
    [
        new(NukeKey, ScoutingReportEditorViewModel.MapPoolSection, "de_nuke: 2 played, 1-1"),
        new(MirageKey, ScoutingReportEditorViewModel.MapPoolSection, "de_mirage: 1 played, 0-1"),
        new(SetupKey, "de_nuke CT · Setups", "Full buy: BombsiteA held over 10 sampled rounds.", () => _png)
    ];

    [Test]
    public async Task TheOnePager_PrintsOnlyStarredLines_AndIsTheDefaultForm()
    {
        ScoutingReportDocument doc = Doc(
            Line("a", "Map Pool Record", "de_nuke: 2 played", starred: true),
            Line("b", "Map Pool Record", "de_mirage: 1 played"),
            Line("c", "Opening Tendencies", "de_nuke T · site A: 4 of 10 rounds"));

        string md = ScoutingReportExporter.Markdown(doc);
        string longMd = ScoutingReportExporter.Markdown(doc, ScoutingReportForm.LongForm);

        using (Assert.Multiple())
        {
            await Assert.That(md).StartsWith("# Falcons <B> one-pager\n");
            await Assert.That(md).Contains("**Sample:** 6 demos across 2 maps");
            await Assert.That(md).Contains("They default A on pistols.");
            await Assert.That(md).Contains("## Map Pool Record\n\n- Nuke: 2 played\n");
            await Assert.That(md).DoesNotContain("de_mirage");
            await Assert.That(md).DoesNotContain("Opening Tendencies")
                .Because("a section with nothing starred is left off the one-pager");

            await Assert.That(longMd).StartsWith("# Falcons <B> scouting report\n");
            await Assert.That(longMd).Contains("- ★ Nuke: 2 played\n- Mirage: 1 played\n");
            await Assert.That(longMd).Contains("## Opening Tendencies");
        }
    }

    [Test]
    public async Task TheOnePager_WithNothingStarred_SaysHowToBuildIt()
    {
        ScoutingReportDocument doc = Doc(Line("a", "Map Pool Record", "de_nuke: 2 played"));

        using (Assert.Multiple())
        {
            await Assert.That(ScoutingReportExporter.Markdown(doc)).Contains(ScoutingReportExporter.NothingStarredLine);
            await Assert.That(ScoutingReportExporter.Html(doc)).Contains(ScoutingReportExporter.NothingStarredLine);
        }
    }

    [Test]
    public async Task TheHtml_IsSelfContained_AndEscaped()
    {
        ScoutingReportDocument doc = Doc(
            Line("h", "Setup Heatmaps By Buy", "de_nuke CT full buy: <10> rounds", starred: true),
            Line("b", "Map Pool Record", "de_mirage: 1 played"));

        string html = ScoutingReportExporter.Html(doc);
        string longHtml = ScoutingReportExporter.Html(doc, ScoutingReportForm.LongForm);

        using (Assert.Multiple())
        {
            await Assert.That(html).StartsWith("<!doctype html>");
            await Assert.That(html).Contains("<title>Falcons &lt;B&gt; one-pager</title>");
            await Assert.That(html).Contains("Nuke CT full buy: &lt;10&gt; rounds");
            await Assert.That(html).DoesNotContain("de_mirage");
            await Assert.That(html).DoesNotContain("http")
                .Because("no external reference: the emailed file renders alone");
            await Assert.That(html).DoesNotContain("<script");
            await Assert.That(html).DoesNotContain("<link");
            await Assert.That(longHtml).Contains("<p class=\"read starred\">");
            await Assert.That(longHtml).Contains("Mirage");
        }
    }

    // Spec Q5: the "no rating, no grade, no win probability" rule is dropped. The roster line prints each
    // player's composite rating in both forms and both files; the exporter still writes no verdict of its own,
    // so a document with no roster and no such words in its lines prints none.
    [Test]
    public async Task TheExport_PrintsTheRosterRating_AndAddsNoVerdictOfItsOwn()
    {
        ScoutingReportFinding line = Line("a", "Map Pool Record", "de_nuke: 2 played, 1-1", starred: true);
        ScoutingReportDocument rated = new()
        {
            TeamName = "Falcons",
            Roster = [new ScoutingReportRosterEntry("m0NESY", "AWP", "1.21"), new("kyxsan", "IGL", "—")],
            RosterScope = "every map",
            Findings = [line]
        };
        ScoutingReportDocument bare = Doc(line);

        foreach (ScoutingReportForm form in (ScoutingReportForm[])[ScoutingReportForm.OnePager, ScoutingReportForm.LongForm])
        {
            foreach (string text in (string[])[ScoutingReportExporter.Markdown(rated, form), ScoutingReportExporter.Html(rated, form)])
            {
                using (Assert.Multiple())
                {
                    // A table: a Markdown row or an HTML one, name, role and rating side by side.
                    static bool Row(string text, string name, string role, string rating) =>
                        text.Contains($"| {name} | {role} | {rating} |", StringComparison.Ordinal)
                        || text.Contains($"<td>{name}</td><td>{role}</td><td class=\"v\">{rating}</td>", StringComparison.Ordinal);
                    await Assert.That(text).Contains("Roster · role, rating over every map");
                    await Assert.That(Row(text, "m0NESY", "AWP", "1.21")).IsTrue();
                    await Assert.That(Row(text, "kyxsan", "IGL", "—")).IsTrue();
                }
            }

            foreach (string text in (string[])[ScoutingReportExporter.Markdown(bare, form), ScoutingReportExporter.Html(bare, form)])
            {
                string lower = text.ToLowerInvariant();
                using (Assert.Multiple())
                {
                    await Assert.That(lower).DoesNotContain("rating");
                    await Assert.That(lower).DoesNotContain("grade");
                    await Assert.That(lower).DoesNotContain("probability");
                    await Assert.That(lower).DoesNotContain("chance");
                }
            }
        }
    }

    [Test]
    public async Task TheStore_PersistsStarsEditsHiddenNotesAndSummary_BesideTeamsJson()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scouting-report-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid team = Guid.NewGuid();
            ScoutingReportNotesStore store = new(StoredFile.At(Path.Combine(root, ScoutingReportNotesStore.FileName)));
            int changes = 0;
            store.Changed += () => changes++;
            store.SetStarred(team, "map|de_nuke", true);
            store.SetStarred(team, "map|de_nuke", true);
            store.SetEdit(team, "map|de_mirage", "  mirage is their comfort pick ", "de_mirage: 1 played");
            store.SetEdit(team, "map|de_inferno", "de_inferno: 3 played", "de_inferno: 3 played");
            store.SetHidden(team, "heatmap|x", true);
            string? note = store.AddNote(team, "  watch the B apps smoke  ");
            store.SetSummary(team, "  Aggressive CT.  ");

            await Assert.That(changes).IsEqualTo(5)
                .Because("a repeat star and an edit equal to the generated text change nothing");
            await Assert.That(File.Exists(Path.Combine(root, ScoutingReportNotesStore.FileName))).IsTrue();

            ScoutingReportTeamNotes reloaded =
                new ScoutingReportNotesStore(StoredFile.At(Path.Combine(root, ScoutingReportNotesStore.FileName))).For(team);
            using (Assert.Multiple())
            {
                await Assert.That(reloaded.Starred).IsEquivalentTo(["map|de_nuke"], CollectionOrdering.Matching);
                await Assert.That(reloaded.Edits["map|de_mirage"]).IsEqualTo("mirage is their comfort pick");
                await Assert.That(reloaded.Edits.ContainsKey("map|de_inferno")).IsFalse();
                await Assert.That(reloaded.Hidden).IsEquivalentTo(["heatmap|x"], CollectionOrdering.Matching);
                await Assert.That(reloaded.Notes.Single().Text).IsEqualTo("watch the B apps smoke");
                await Assert.That(ScoutingReportNotesStore.NoteKey(reloaded.Notes.Single().Id)).IsEqualTo(note);
                await Assert.That(reloaded.Summary).IsEqualTo("Aggressive CT.");
            }

            // A file at a newer schema is refused and never overwritten.
            string path = Path.Combine(root, ScoutingReportNotesStore.FileName);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\": 99, \"teams\": []}");
            ScoutingReportNotesStore refused = new(StoredFile.At(Path.Combine(root, ScoutingReportNotesStore.FileName)));
            refused.SetSummary(team, "overwrite?");
            using (Assert.Multiple())
            {
                await Assert.That(refused.FileProblem).IsNotNull();
                await Assert.That(await File.ReadAllTextAsync(path)).Contains("99");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TheStore_ReadsTheOldDossierFile_WhenTheNewOneIsMissing_AndWritesTheNewOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scouting-report-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid team = Guid.NewGuid();
            string oldPath = Path.Combine(root, ScoutingReportNotesStore.LegacyFileName);
            string newPath = Path.Combine(root, ScoutingReportNotesStore.FileName);
            new ScoutingReportNotesStore(StoredFile.At(oldPath)).SetSummary(team, "Aggressive CT.");
            string oldBytes = await File.ReadAllTextAsync(oldPath);

            ScoutingReportNotesStore migrated = new(StoredFile.At(newPath), StoredFile.At(oldPath));
            using (Assert.Multiple())
            {
                await Assert.That(migrated.FileProblem).IsNull();
                await Assert.That(migrated.For(team).Summary).IsEqualTo("Aggressive CT.");
                await Assert.That(File.Exists(newPath)).IsTrue().Because("the new file is written at once");
                await Assert.That(await File.ReadAllTextAsync(oldPath)).IsEqualTo(oldBytes)
                    .Because("the old file is left alone");
            }

            // A later save goes to the new file only.
            migrated.SetSummary(team, "Passive T.");
            using (Assert.Multiple())
            {
                await Assert.That(await File.ReadAllTextAsync(oldPath)).IsEqualTo(oldBytes);
                await Assert.That(new ScoutingReportNotesStore(StoredFile.At(newPath)).For(team).Summary)
                    .IsEqualTo("Passive T.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TheStore_PrefersTheNewFile_WhenBothExist_AndRefusesAnUnreadableOldOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scouting-report-both-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid team = Guid.NewGuid();
            string oldPath = Path.Combine(root, ScoutingReportNotesStore.LegacyFileName);
            string newPath = Path.Combine(root, ScoutingReportNotesStore.FileName);
            new ScoutingReportNotesStore(StoredFile.At(oldPath)).SetSummary(team, "old");
            new ScoutingReportNotesStore(StoredFile.At(newPath)).SetSummary(team, "new");

            await Assert.That(new ScoutingReportNotesStore(StoredFile.At(newPath), StoredFile.At(oldPath)).For(team).Summary)
                .IsEqualTo("new");

            File.Delete(newPath);
            await File.WriteAllTextAsync(oldPath, "{not json");
            ScoutingReportNotesStore refused = new(StoredFile.At(newPath), StoredFile.At(oldPath));
            refused.SetSummary(team, "overwrite?");
            using (Assert.Multiple())
            {
                await Assert.That(refused.FileProblem).IsNotNull();
                await Assert.That(File.Exists(newPath)).IsFalse();
                await Assert.That(await File.ReadAllTextAsync(oldPath)).IsEqualTo("{not json");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task TheEditor_StarsRewritesLeavesOutAndAddsNotes_AndKeepsThemAcrossARebuild()
    {
        ScoutingReportNotesStore store = new(null);
        ScoutingReportEditorViewModel editor = new(store);
        Guid team = Guid.NewGuid();
        editor.Load(team, "Falcons", "3 demos across 2 maps", Sources());

        using (Assert.Multiple())
        {
            await Assert.That(editor.Findings.Count).IsEqualTo(3);
            await Assert.That(editor.Findings[0].ShowsSectionHeader).IsTrue();
            await Assert.That(editor.Findings[1].ShowsSectionHeader).IsFalse();
            await Assert.That(editor.Findings[2].ShowsSectionHeader).IsTrue();
            await Assert.That(editor.ExportForm).IsEqualTo(ScoutingReportForm.OnePager)
                .Because("the short form is the default export");
            await Assert.That(editor.ExportFormat).IsEqualTo(ScoutingReportFormat.Html);
            await Assert.That(editor.ExportLabel).IsEqualTo("Export one-pager · 0 starred");
            await Assert.That(editor.CountsLine).IsEqualTo("3 findings · 0 starred");
        }

        ScoutingReportFindingViewModel nuke = editor.Findings[0];
        editor.ToggleStarCommand.Execute(nuke);
        nuke.BeginEditCommand.Execute(null);
        await Assert.That(nuke.EditText).IsEqualTo("de_nuke: 2 played, 1-1");
        nuke.EditText = "de_nuke: their best map";
        editor.CommitEditCommand.Execute(nuke);
        editor.RemoveCommand.Execute(editor.Findings[1]);
        editor.NewNoteText = "they stack B after a lost pistol";
        editor.AddNoteCommand.Execute(null);
        editor.Summary = "Slow defaults.";

        using (Assert.Multiple())
        {
            await Assert.That(nuke.IsStarred).IsTrue();
            await Assert.That(nuke.IsEdited).IsTrue();
            await Assert.That(nuke.IsEditing).IsFalse();
            await Assert.That(editor.Findings.Select(f => f.Key)).DoesNotContain(MirageKey);
            await Assert.That(editor.HasHidden).IsTrue();
            await Assert.That(editor.Findings.Last().IsNote).IsTrue();
            await Assert.That(editor.Findings.Last().Section).IsEqualTo(ScoutingReportEditorViewModel.NotesSection);
            await Assert.That(editor.CountsLine).IsEqualTo("3 findings · 1 starred · 1 dismissed");
        }

        // A section rebuild in another order keeps the star, the rewrite and the left-out line by key.
        editor.Load(team, "Falcons", "3 demos across 2 maps", [.. Sources().Reverse()]);
        ScoutingReportFindingViewModel again = editor.Findings.Single(f => f.Key == NukeKey);
        using (Assert.Multiple())
        {
            await Assert.That(again.IsStarred).IsTrue();
            await Assert.That(again.Text).IsEqualTo("de_nuke: their best map");
            await Assert.That(editor.Findings.Any(f => f.Key == MirageKey)).IsFalse();
            await Assert.That(editor.Summary).IsEqualTo("Slow defaults.");
        }

        // The one-pager is the starred line alone; "starred only" previews it.
        ScoutingReportDocument doc = editor.Document();
        string md = ScoutingReportExporter.Markdown(doc);
        editor.ShowStarredOnly = true;
        using (Assert.Multiple())
        {
            await Assert.That(md).Contains("## Nuke\n\n#### Map record\n\n- Nuke: their best map\n");
            await Assert.That(md).Contains("Slow defaults.");
            await Assert.That(md).DoesNotContain("BombsiteA");
            await Assert.That(editor.Findings.Count).IsEqualTo(1);
            await Assert.That(editor.Findings.Select(f => f.Key))
                .IsEquivalentTo([NukeKey], CollectionOrdering.Matching);
        }

        // Reset puts the generated line back; "Show settled" lists the dismissed line with Restore.
        editor.ShowStarredOnly = false;
        editor.ResetEditCommand.Execute(again);
        await Assert.That(editor.SettledLabel).IsEqualTo("Show settled (1)");
        editor.ShowSettled = true;
        ScoutingReportFindingViewModel dismissed = editor.Findings.Single(f => f.Key == MirageKey);
        await Assert.That(dismissed.RemoveLabel).IsEqualTo("Restore");
        await Assert.That(ScoutingReportExporter.Markdown(editor.Document())).DoesNotContain("de_mirage")
            .Because("a dismissed line shown under the toggle is still out of both forms");
        editor.RemoveCommand.Execute(dismissed);
        editor.ShowSettled = false;
        using (Assert.Multiple())
        {
            await Assert.That(again.Text).IsEqualTo("de_nuke: 2 played, 1-1");
            await Assert.That(store.For(team).Edits).IsEmpty();
            await Assert.That(editor.Findings.Any(f => f.Key == MirageKey)).IsTrue();
        }

        // Removing a note removes it outright.
        editor.RemoveCommand.Execute(editor.Findings.Single(f => f.IsNote));
        await Assert.That(store.For(team).Notes).IsEmpty();
    }

    [Test]
    public async Task Export_WritesTheOnePagerAsHtml_ByDefault_AndTheChosenFormOtherwise()
    {
        List<(string Text, string Stem, string Extension)> written = [];
        ScoutingReportEditorViewModel editor = new(new ScoutingReportNotesStore(null), export: (text, stem, ext) =>
        {
            written.Add((text, stem, ext));
            return Path.Combine(Path.GetTempPath(), stem + ext);
        });

        await Assert.That(editor.ExportCommand.CanExecute(null)).IsFalse().Because("no team selected");
        editor.Load(Guid.NewGuid(), "Falcons", "3 demos", Sources());
        bool offWithNothingStarred = !editor.ExportCommand.CanExecute(null) && !editor.CanExport;
        editor.ToggleStarCommand.Execute(editor.Findings[0]);
        using (Assert.Multiple())
        {
            await Assert.That(offWithNothingStarred).IsTrue().Because("an empty one-pager is not exported");
            await Assert.That(editor.CanExport).IsTrue();
            await Assert.That(editor.ExportLabel).IsEqualTo("Export one-pager · 1 starred");
            await Assert.That(editor.Starred.Single().Where).IsEqualTo("Nuke");
        }

        editor.ExportCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(written.Count).IsEqualTo(1);
            await Assert.That(written[0].Extension).IsEqualTo(".html");
            await Assert.That(written[0].Stem).IsEqualTo("Falcons-one-pager");
            await Assert.That(written[0].Text).Contains("<title>Falcons one-pager</title>");
            await Assert.That(written[0].Text).DoesNotContain("de_mirage");
            await Assert.That(editor.ExportLine).IsEqualTo("one-pager written: Falcons-one-pager.html");
        }

        editor.SelectedFormOption = ScoutingReportEditorViewModel.FormOptions.Single(o => o.Value == ScoutingReportForm.LongForm);
        editor.SelectedFormatOption =
            ScoutingReportEditorViewModel.FormatOptions.Single(o => o.Value == ScoutingReportFormat.Markdown);
        editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(editor.ExportLabel).IsEqualTo("Export full report");
            await Assert.That(written[1].Extension).IsEqualTo(".md");
            await Assert.That(written[1].Stem).IsEqualTo("Falcons-scouting-report");
            await Assert.That(written[1].Text).StartsWith("# Falcons scouting report");
            await Assert.That(written[1].Text).Contains("Mirage");
        }

        ScoutingReportEditorViewModel browser = new(new ScoutingReportNotesStore(null), isBrowser: true);
        browser.Load(Guid.NewGuid(), "Falcons", "", Sources());
        using (Assert.Multiple())
        {
            await Assert.That(browser.ExportCommand.CanExecute(null)).IsFalse();
            await Assert.That(browser.HasExportUnavailableNote).IsTrue();
            await Assert.That(browser.MarkdownText()).StartsWith("# Falcons one-pager");
        }
    }

    [Test]
    public async Task AnUnstarredOnePager_ExportsOnlyInHtmlWithAMapInView_AndNeverAsAnEmptyFile()
    {
        List<string> written = [];
        ScoutingReportEditorViewModel editor = new(new ScoutingReportNotesStore(null), export: (text, stem, ext) =>
        {
            written.Add(text);
            return stem + ext;
        });
        editor.Load(Guid.NewGuid(), "Falcons", "3 demos", Sources());
        editor.BuyStages = _ => Task.FromResult<IReadOnlyList<ScoutingReportBuyStage>>([]);
        bool offWithNoMap = !editor.CanExport && !editor.ExportCommand.CanExecute(null);
        editor.HasMapInView = true;
        bool onWithAMap = editor.CanExport && editor.ExportCommand.CanExecute(null);
        editor.ExportFormat = ScoutingReportFormat.Markdown;
        bool offInMarkdown = !editor.CanExport && !editor.ExportCommand.CanExecute(null);
        editor.ExportFormat = ScoutingReportFormat.Html;
        editor.ExportCommand.Execute(null);

        using (Assert.Multiple())
        {
            await Assert.That(offWithNoMap).IsTrue().Because("no star and no map in view: nothing to draw");
            await Assert.That(onWithAMap).IsTrue().Because("the map in view has buy pictures");
            await Assert.That(offInMarkdown).IsTrue().Because("Markdown prints no buy picture");
            await Assert.That(written).IsEmpty().Because("no picture came back and nothing is starred");
            await Assert.That(editor.ExportLine).StartsWith("nothing to export");
        }
    }

    [Test]
    public async Task TheSectionBuilders_StateEverySample_AndSkipZeroCounts()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ScoutingLayerBase.CountText(4, 10)).IsEqualTo("4 of 10 rounds");
            await Assert.That(ScoutingLayerBase.CountText(1, 0)).IsEqualTo("1 round");
            await Assert.That(ScoutingReportEditorViewModel.FromPeriodDiff(null, null, "x")).IsEmpty();
        }
    }

    [Test]
    public async Task TheTab_CollectsItsSections_IntoTheEditor_AndExportsTheStarredOnes()
    {
        DemoCacheStore cache = new(null);
        TeamIdentityService teams = new(null, cache.Library(), run: _inline);
        await teams.StartAsync();
        using (cache.BeginBatch())
        {
            cache.Upsert(Record("/d/s1.dem", 10, Ids(1, 2, 3, 4, 5), Ids(11, 12, 13, 14, 15), "de_nuke", 9, 16));
            cache.Upsert(Record("/d/s2.dem", 11, Ids(11, 12, 13, 14, 15), Ids(1, 2, 3, 4, 5), "de_mirage", 9, 13));
        }

        await teams.Idle;
        Guid teamA = teams.Teams
            .First(t => t.Rosters.Any(r => r.CoreLineup != null && r.CoreLineup.Contains(Ids(1)[0]))).Id;
        List<string> written = [];
        VetoHistoryStore vetoes = new(null);
        using ScoutingReportTabViewModel vm = new(teams, cache.Library(), vetoes, isBrowser: false,
            notes: new ScoutingReportNotesStore(null),
            export: (text, _, _) =>
            {
                written.Add(text);
                return "x.html";
            });

        await Assert.That(vm.Editor.HasTeam).IsFalse();
        vm.SelectedTeam = vm.Teams.Single(t => t.Id == teamA);

        ScoutingReportFindingViewModel[] maps =
            [.. vm.Editor.Findings.Where(f => f.Section == ScoutingReportEditorViewModel.MapPoolSection)];
        using (Assert.Multiple())
        {
            await Assert.That(vm.Editor.HasTeam).IsTrue();
            await Assert.That(maps.Length).IsEqualTo(2);
            await Assert.That(maps.Select(f => f.Key)).Contains(NukeKey);
            await Assert.That(vm.Editor.Findings.Any(f => f.Section == ScoutingReportEditorViewModel.PeriodDiffSection))
                .IsTrue();
        }

        // A veto step the user adds lands as a line of its own.
        vm.NewVetoMap = "de_ancient";
        vm.AddVetoCommand.Execute(null);
        await Assert.That(vm.Editor.Findings.Any(f =>
            f.Section == ScoutingReportEditorViewModel.VetoSection && f.Text.Contains("de_ancient"))).IsTrue();

        vm.Editor.ToggleStarCommand.Execute(vm.Editor.Findings.Single(f => f.Key == NukeKey));
        vm.Editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(written.Single()).Contains("one-pager");
            await Assert.That(written.Single()).Contains("Nuke: 1 played");
            await Assert.That(written.Single()).DoesNotContain("de_mirage: ")
                .Because("the unstarred map line is off; the roster scope may still name the map in view");
        }

        vm.SelectedTeam = null;
        await Assert.That(vm.Editor.HasTeam).IsFalse();
        await Assert.That(vm.Editor.HasFindings).IsFalse();
    }

    [Test]
    public async Task TheStarKey_CarriesItsScope_AndEveryOldKeyThatHasAMappingIsRemapped()
    {
        using (Assert.Multiple())
        {
            await Assert.That(ScoutingReportStarKeys.Key(new ScoutingScope("de_mirage", "CT", "awp", "Full", "0:30"),
                "holds A | ramp")).IsEqualTo("de_mirage|CT|awp|Full|0:30|holds A / ramp");
            await Assert.That(ScoutingReportStarKeys.Parse("de_mirage|CT|setups|Full||read")?.Scope)
                .IsEqualTo(new ScoutingScope("de_mirage", "CT", "setups", "Full"));
            await Assert.That(ScoutingReportStarKeys.Parse("note:abc")).IsNull();
            await Assert.That(ScoutingReportStarKeys.Where(new ScoutingScope("de_nuke", "T", "plants", "All")))
                .IsEqualTo("de_nuke T");

            await Assert.That(ScoutingReportStarKeys.Remap("map|de_nuke")).IsEqualTo("de_nuke||pool|||record");
            await Assert.That(ScoutingReportStarKeys.Remap("map|deciders")).IsEqualTo("||pool|||deciders");
            await Assert.That(ScoutingReportStarKeys.Remap("heatmap|de_nuke|full buy"))
                .IsEqualTo("de_nuke|CT|setups|Full||read");
            await Assert.That(ScoutingReportStarKeys.Remap("heatmap|de_nuke|pistol")).IsNull()
                .Because("the buy filter has no pistol");
            await Assert.That(ScoutingReportStarKeys.Remap("openings|de_nuke T · first utility 5-10 s"))
                .IsEqualTo("de_nuke|T|util|All||first utility 5-10 s");
            await Assert.That(ScoutingReportStarKeys.Remap("openings|de_nuke T · entry p0 at A"))
                .IsEqualTo("de_nuke|T|opening|All||entry p0 at A");
            await Assert.That(ScoutingReportStarKeys.Remap("openings|de_nuke T · site A"))
                .IsEqualTo("de_nuke|T|plants|All||site A");
            await Assert.That(ScoutingReportStarKeys.Remap("openings|de_nuke T · lurker p3"))
                .IsEqualTo("de_nuke|T|lurks|All||lurker p3");
            await Assert.That(ScoutingReportStarKeys.Remap("postplant|de_nuke T · plant A spot 1 (1050, 1000)"))
                .IsEqualTo("de_nuke|T|plants|All||plant A spot 1 (1050, 1000)");
            await Assert.That(ScoutingReportStarKeys.Remap("postplant|de_nuke CT · retake together"))
                .IsEqualTo("de_nuke|CT|retakes|All||retake together");
            await Assert.That(ScoutingReportStarKeys.Remap("situational|de_nuke T · led 2-up"))
                .IsEqualTo("de_nuke|T|econ|All||led 2-up");
            await Assert.That(ScoutingReportStarKeys.Remap("situational|de_nuke T won pistol, won the bonus"))
                .IsEqualTo("de_nuke|T|econ|All||won pistol, won the bonus");
            await Assert.That(ScoutingReportStarKeys.Remap("period|last")).IsEqualTo("||period|||last");
            await Assert.That(ScoutingReportStarKeys.Remap("veto|0123abcd")).IsEqualTo("||veto|||0123abcd");
            await Assert.That(ScoutingReportStarKeys.Remap("note:0123")).IsNull();
            await Assert.That(ScoutingReportStarKeys.Remap("mystery|thing")).IsNull();
        }
    }

    [Test]
    public async Task ASchemaOneFile_IsRemappedOnLoad_KeepsWhatHasNoMapping_AndIsRewrittenAtSchemaTwo()
    {
        string root = Path.Combine(Path.GetTempPath(), "dv-scouting-report-remap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Guid team = Guid.NewGuid();
            string path = Path.Combine(root, ScoutingReportNotesStore.FileName);
            await File.WriteAllTextAsync(path, $$"""
                {
                  "schemaVersion": 1,
                  "teams": [{
                    "teamId": "{{team}}",
                    "summary": "Aggressive CT.",
                    "starred": ["map|de_nuke", "heatmap|de_nuke|full buy", "heatmap|de_nuke|pistol", "note:abc"],
                    "edits": { "openings|de_nuke T · site A": "they love A" },
                    "hidden": ["postplant|de_nuke CT · retake together", "mystery|thing"],
                    "notes": []
                  }]
                }
                """);

            ScoutingReportNotesStore store = new(StoredFile.At(path));
            ScoutingReportTeamNotes notes = store.For(team);
            string written = await File.ReadAllTextAsync(path);
            using (Assert.Multiple())
            {
                await Assert.That(store.FileProblem).IsNull();
                await Assert.That(notes.Starred).IsEquivalentTo(
                    ["de_nuke||pool|||record", "de_nuke|CT|setups|Full||read", "heatmap|de_nuke|pistol", "note:abc"],
                    CollectionOrdering.Matching);
                await Assert.That(notes.Edits["de_nuke|T|plants|All||site A"]).IsEqualTo("they love A");
                await Assert.That(notes.Hidden).IsEquivalentTo(
                    ["de_nuke|CT|retakes|All||retake together", "mystery|thing"], CollectionOrdering.Matching)
                    .Because("a key with no mapping is kept, never dropped");
                await Assert.That(notes.Summary).IsEqualTo("Aggressive CT.");
                await Assert.That(written).Contains("\"schemaVersion\": 2");
                await Assert.That(written).Contains("de_nuke||pool|||record");
            }

            // Read again at schema 2: nothing moves twice.
            ScoutingReportTeamNotes again = new ScoutingReportNotesStore(StoredFile.At(path)).For(team);
            await Assert.That(again.Starred).IsEquivalentTo(notes.Starred, CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ASuggestedLine_StartsStarredOnce_AndAnUnstarSticks()
    {
        ScoutingReportNotesStore store = new(null);
        ScoutingReportEditorViewModel editor = new(store);
        Guid team = Guid.NewGuid();
        editor.Load(team, "Falcons", "", Sources());

        editor.Seed([SetupKey]);
        bool seeded = editor.IsStarred(SetupKey);
        editor.SetStarred(SetupKey, false);
        editor.Seed([SetupKey]);
        using (Assert.Multiple())
        {
            await Assert.That(seeded).IsTrue();
            await Assert.That(editor.IsStarred(SetupKey)).IsFalse().Because("a suggestion is starred once ever");
            await Assert.That(store.For(team).Seeded).IsEquivalentTo([SetupKey], CollectionOrdering.Matching);
            await Assert.That(editor.StarredCount).IsEqualTo(0);
        }
    }

    private static string[] Ids(params int[] numbers) => [.. numbers.Select(n => $"7656{n:D4}")];

    private static DemoCacheRecord Record(string path, int day, string[] t, string[] ct, string map, int ctScore,
        int tScore)
    {
        DemoCacheRecord record = new()
        {
            Path = path,
            Size = 1000,
            ModifiedTicks = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(day).Ticks,
            Map = map,
            CtScore = ctScore,
            TScore = tScore,
            CtSideWins = ctScore,
            TSideWins = tScore
        };
        int slot = 0;
        foreach (string id in t)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 2 });
        }

        foreach (string id in ct)
        {
            record.Players.Add(new CachedPlayerInfo { Slot = slot++, Name = $"p{id[^4..]}", SteamId64 = id, Team = 3 });
        }

        DemoCacheStore.StampParse(record);
        DemoCacheStore.StampAnalysis(record);
        return record;
    }
}
