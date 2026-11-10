#region

using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Scouting Report export from scoped stars: the one-pager's identity block, roster line, map, side and
///     layer order and scope suffixes; the long form's completeness; HTML escaping with nothing external; and one
///     stage picture per map and side that has a starred map-layer line, and none otherwise.
/// </summary>
public class ScoutingReportExportTests
{
    private static readonly IReadOnlyList<string> _order = [.. ScoutingLayerCatalog.Create().Select(l => l.Id)];
    private static readonly byte[] _stagePng = [0x89, 0x50, 0x4E, 0x47, 9, 9, 9];

    private static string K(string map, string side, string layer, string buy = "All", string window = "",
        string text = "read") =>
        ScoutingReportStarKeys.Key(new ScoutingScope(map, side, layer, buy, window), text);

    private static ScoutingReportFinding F(string key, string section, string text, bool starred = true) =>
        new() { Key = key, Section = section, Text = text, IsStarred = starred };

    private static ScoutingReportDocument Doc(params ScoutingReportFinding[] findings) => new()
    {
        TeamName = "Falcons",
        SampleLine = "6 demos across 2 maps",
        DateRangeLine = "Aug 1, 2026 to Nov 8, 2026",
        RecordLine = "67% of 6 maps (4-2)",
        Ranks = ["VRS 7 (set 2026-11-01)", "HLTV 9 (set 2026-10-28)"],
        OpponentMixLine = "4 vs top-30 · 2 other",
        HeadToHeadLine = "vs Us 1–2",
        Roster = [new ScoutingReportRosterEntry("m0NESY", "AWP", "1.21")],
        RosterScope = "every map",
        LayerOrder = _order,
        Findings = findings
    };

    // Starred lines in an order unlike print order: a T line first, AWP before Setups, the map-wide record
    // between maps, a team-wide line and a note mixed in, and one unstarred line.
    private static ScoutingReportFinding[] Mixed() =>
    [
        F(K("de_nuke", "T", "plants"), "de_nuke T · Plants", "A site in 6 of 10 rounds"),
        F(K("de_nuke", "CT", "awp", window: "0:30"), "de_nuke CT · AWP", "the AWP holds outside"),
        F(K("de_nuke", "CT", "setups", "Full"), "de_nuke CT · Setups", "2-1-2 on full buys"),
        F(K("de_nuke", "", "pool", "", "", "record"), "Map pool", "de_nuke: 4 played, 3-1"),
        F(K("", "", "pool", "", "", "deciders"), "Map pool", "Deciders: 1-0"),
        F(K("de_mirage", "CT", "setups", "Eco"), "de_mirage CT · Setups", "stack A on ecos"),
        F(K("de_mirage", "CT", "opening"), "de_mirage CT · Opening deaths", "first death mid", starred: false),
        F("note:abc", ScoutingReportEditorViewModel.NotesSection, "watch the B apps smoke"),
        F(K("de_nuke", "CT", "awp", window: "contact", text: "routes"), "de_nuke CT · AWP", "ramp then heaven")
    ];

    private static void InOrder(string text, params string[] parts)
    {
        int at = -1;
        foreach (string part in parts)
        {
            int next = text.IndexOf(part, at + 1, StringComparison.Ordinal);
            if (next < 0)
            {
                throw new InvalidOperationException($"\"{part}\" missing or out of order after index {at} in:\n{text}");
            }

            at = next;
        }
    }

    [Test]
    public async Task TheOnePager_PrintsIdentityAndRoster_ThenMapCtTMapWide_InLayerOrder_WithScopeSuffixes()
    {
        string md = ScoutingReportExporter.Markdown(Doc(Mixed()));

        InOrder(md,
            "# Falcons one-pager",
            "**Record:** 67% of 6 maps (4-2)",
            "**Ranks:** VRS 7 (set 2026-11-01) · HLTV 9 (set 2026-10-28)",
            "**Sample:** 6 demos across 2 maps · Aug 1, 2026 to Nov 8, 2026",
            "**Opponents:** 4 vs top-30 · 2 other",
            "**Head to head:** vs Us 1–2",
            "**Roster (role, rating over every map):** m0NESY (AWP, 1.21)",
            "## de_nuke",
            "### CT",
            "- **Setups:** 2-1-2 on full buys (Full buy)\n",
            "- **AWP:** the AWP holds outside (to 0:30)\n",
            "- **AWP:** ramp then heaven (to first contact)\n",
            "### T",
            "- **Plants:** A site in 6 of 10 rounds\n",
            "### Map-wide",
            "- **Map pool:** de_nuke: 4 played, 3-1\n",
            "## de_mirage",
            "### CT",
            "- **Setups:** stack A on ecos (Eco buy)\n",
            "## Map pool",
            "- Deciders: 1-0\n",
            "## Notes",
            "- watch the B apps smoke\n");

        string html = ScoutingReportExporter.Html(Doc(Mixed()));
        InOrder(html, "<h2>de_nuke</h2>", "<h3>CT</h3>", "<span class=\"layer\">Setups:</span> 2-1-2 on full buys",
            "<span class=\"scope\">(Full buy)</span>", "<h3>T</h3>", "<h3>Map-wide</h3>", "<h2>de_mirage</h2>",
            "<h2>Map pool</h2>", "<h2>Notes</h2>");

        using (Assert.Multiple())
        {
            await Assert.That(md).DoesNotContain("first death mid").Because("unstarred lines stay off the one-pager");
            await Assert.That(md).DoesNotContain("Opening deaths");
            await Assert.That(md).DoesNotContain("(All buy)").Because("the All buy is implied and gets no suffix");
            await Assert.That(html).Contains("<dt>Ranks</dt><dd>VRS 7 (set 2026-11-01) · HLTV 9 (set 2026-10-28)</dd>");
        }
    }

    [Test]
    public async Task TheOnePager_WithNothingStarred_StillPrintsTheIdentity()
    {
        string md = ScoutingReportExporter.Markdown(Doc(F(K("de_nuke", "CT", "setups"), "de_nuke CT · Setups", "x",
            starred: false)));
        using (Assert.Multiple())
        {
            await Assert.That(md).Contains("**Record:** 67% of 6 maps (4-2)");
            await Assert.That(md).Contains(ScoutingReportExporter.NothingStarredLine);
            await Assert.That(md).DoesNotContain("## de_nuke");
        }
    }

    [Test]
    public async Task TheLongForm_PrintsEveryLineNotDismissed_OverEveryMapAndSide_AndTheNotes()
    {
        ScoutingReportNotesStore store = new(null);
        ScoutingReportEditorViewModel editor = new(store, export: (_, _, _) => "x");
        ScoutingReportFindingSource[] sources =
        [
            new(K("de_nuke", "", "pool", "", "", "record"), ScoutingReportEditorViewModel.MapPoolSection, "de_nuke: 4 played", Map: "de_nuke"),
            new(K("de_nuke", "CT", "setups"), "de_nuke CT · Setups", "setups read", Map: "de_nuke"),
            new(K("de_nuke", "CT", "setups", text: "A anchor"), "de_nuke CT · Setups", "A anchor: 4 of 10 rounds", Map: "de_nuke"),
            new(K("de_nuke", "CT", "econ"), "de_nuke CT · Economy", "econ read", Map: "de_nuke"),
            new(K("de_nuke", "T", "plants"), "de_nuke T · Plants", "plants read", Map: "de_nuke"),
            new(K("de_mirage", "T", "lurks"), "de_mirage T · Lurks", "lurks read", Map: "de_mirage"),
            new(K("de_mirage", "T", "util"), "de_mirage T · Utility", "dismissed read", Map: "de_mirage"),
            new(K("", "", "period", text: "note"), ScoutingReportEditorViewModel.PeriodDiffSection, "period note")
        ];
        editor.Load(Guid.NewGuid(), "Falcons", "6 demos", sources);
        editor.RemoveCommand.Execute(editor.Findings.Single(f => f.Text == "dismissed read"));
        editor.NewNoteText = "watch the B apps smoke";
        editor.AddNoteCommand.Execute(null);
        editor.ToggleStarCommand.Execute(editor.Findings.Single(f => f.Text == "plants read"));
        editor.ExportForm = ScoutingReportForm.LongForm;

        string md = ScoutingReportExporter.Markdown(editor.Document(false), ScoutingReportForm.LongForm);

        InOrder(md,
            "# Falcons scouting report",
            "## de_nuke", "### CT", "#### Setups", "- setups read\n", "- A anchor: 4 of 10 rounds\n",
            "#### Economy", "- econ read\n",
            "### T", "#### Plants", "- ★ plants read\n",
            "### Map-wide", "#### Map pool", "- de_nuke: 4 played\n",
            "## de_mirage", "### T", "#### Lurks", "- lurks read\n",
            "## Period Diff", "- period note\n",
            "## Notes", "- watch the B apps smoke\n");
        await Assert.That(md).DoesNotContain("dismissed read").Because("a dismissed line is in neither form");
    }

    [Test]
    public async Task TheHtml_EscapesEveryUserString_AndReferencesNothingOutside()
    {
        ScoutingReportDocument doc = new()
        {
            TeamName = "<script>alert(1)</script>",
            SampleLine = "6 demos & 2 maps",
            HeadToHeadLine = "vs \"Us\" 1–2",
            Roster = [new ScoutingReportRosterEntry("<b>bold</b>", "AWP", "1.00")],
            Summary = "They <i>always</i> go B",
            LayerOrder = _order,
            Findings =
            [
                F(K("de_<nuke>", "CT", "setups"), "de_<nuke> CT · Set<ups>", "a < b && c > d"),
                F("note:1", "Notes", "<img src=x onerror=alert(1)>")
            ],
            Stages = [new ScoutingReportStageImage("de_<nuke>", "CT", "de_<nuke> CT · Set<ups>", _stagePng)]
        };

        string html = ScoutingReportExporter.Html(doc);

        using (Assert.Multiple())
        {
            await Assert.That(html).DoesNotContain("<script").Because("no script, the team name included");
            await Assert.That(html).DoesNotContain("<b>bold");
            await Assert.That(html).DoesNotContain("<i>always");
            await Assert.That(html).DoesNotContain("<img src=x");
            await Assert.That(html).Contains("&lt;script&gt;alert(1)&lt;/script&gt; one-pager");
            await Assert.That(html).Contains("6 demos &amp; 2 maps");
            await Assert.That(html).Contains("vs &quot;Us&quot; 1–2");
            await Assert.That(html).Contains("a &lt; b &amp;&amp; c &gt; d");
            await Assert.That(html).Contains("<h2>de_&lt;nuke&gt;</h2>");
            await Assert.That(html).Contains("alt=\"de_&lt;nuke&gt; CT · Set&lt;ups&gt;\"");
            await Assert.That(html).DoesNotContain("http").Because("no external reference: the emailed file renders alone");
            await Assert.That(html).DoesNotContain("<link");
            await Assert.That(html).DoesNotContain("src=\"x");
            await Assert.That(html).Contains("src=\"data:image/png;base64," + Convert.ToBase64String(_stagePng) + "\"");
        }
    }

    [Test]
    public async Task TheOnePager_DrawsOneStagePerMapAndSideWithAStarredMapLayerLine_AndNoneOtherwise()
    {
        ScoutingMapMark setupsMark = new(1, 2, 0, ScoutingMarkShape.Dot, ScoutingColor.Player1);
        ScoutingMapMark awpMark = new(3, 4, 0, ScoutingMarkShape.Badge, ScoutingColor.Ct, 9f, "1");
        ScoutingMapMark plantsMark = new(5, 6, 0, ScoutingMarkShape.Dot, ScoutingColor.T);
        ScoutingReportStageLayer setups = new("setups", "Setups", [setupsMark]);
        ScoutingReportStageLayer awp = new("awp", "AWP", [awpMark]);
        ScoutingReportStageLayer plants = new("plants", "Plants", [plantsMark]);
        ScoutingReportFindingSource[] sources =
        [
            new(K("de_nuke", "", "pool", "", "", "record"), ScoutingReportEditorViewModel.MapPoolSection, "de_nuke: 4 played", Map: "de_nuke"),
            new(K("de_nuke", "CT", "awp", window: "0:30"), "de_nuke CT · AWP", "awp read", Map: "de_nuke", Stage: awp),
            new(K("de_nuke", "CT", "setups"), "de_nuke CT · Setups", "setups read", Map: "de_nuke", Stage: setups),
            new(K("de_nuke", "CT", "setups", text: "A anchor"), "de_nuke CT · Setups", "A anchor: 4", Map: "de_nuke", Stage: setups),
            new(K("de_nuke", "T", "plants"), "de_nuke T · Plants", "plants read", Map: "de_nuke", Stage: plants),
            new(K("de_nuke", "T", "econ"), "de_nuke T · Economy", "econ read", Map: "de_nuke")
        ];
        List<(string Map, IReadOnlyList<ScoutingMapMark> Marks)> rendered = [];
        List<string> written = [];
        ScoutingReportEditorViewModel editor = new(new ScoutingReportNotesStore(null),
            export: (text, _, _) =>
            {
                written.Add(text);
                return "x.html";
            },
            renderStage: (map, marks) =>
            {
                rendered.Add((map, marks));
                return _stagePng;
            });
        editor.Load(Guid.NewGuid(), "Falcons", "6 demos", sources);
        void Star(string text) => editor.ToggleStarCommand.Execute(editor.Findings.Single(f => f.Text == text));

        // Only text lines starred: no picture at all.
        Star("de_nuke: 4 played");
        Star("econ read");
        editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(rendered).IsEmpty();
            await Assert.That(written[^1]).DoesNotContain("data:image");
            await Assert.That(written[^1]).DoesNotContain("<figure");
        }

        // Two CT setups lines and the AWP line: one CT picture, setups' marks once, in registry order.
        Star("awp read");
        Star("setups read");
        Star("A anchor: 4");
        editor.ExportCommand.Execute(null);
        string html = written[^1];
        using (Assert.Multiple())
        {
            await Assert.That(rendered.Count).IsEqualTo(1);
            await Assert.That(rendered[0].Map).IsEqualTo("de_nuke");
            await Assert.That(rendered[0].Marks.SequenceEqual([setupsMark, awpMark])).IsTrue();
            await Assert.That(html.Split("<figure").Length - 1).IsEqualTo(1);
            await Assert.That(html).Contains("<figcaption>de_nuke CT · Setups, AWP</figcaption>");
        }

        InOrder(html, "<h3>CT</h3>", "<figure class=\"stage\">", "Setups:", "<h3>T</h3>", "Economy:");

        // Markdown carries no picture, renders none, and says where the picture is.
        rendered.Clear();
        editor.ExportFormat = ScoutingReportFormat.Markdown;
        editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(rendered).IsEmpty();
            await Assert.That(written[^1]).DoesNotContain("data:image");
            await Assert.That(written[^1])
                .Contains(ScoutingReportExporter.StageOmittedLine("de_nuke CT · Setups, AWP"));
        }

        // The long form carries the lines' own pictures, never the stage.
        editor.ExportFormat = ScoutingReportFormat.Html;
        editor.ExportForm = ScoutingReportForm.LongForm;
        editor.ExportCommand.Execute(null);
        using (Assert.Multiple())
        {
            await Assert.That(rendered).IsEmpty();
            await Assert.That(written[^1]).DoesNotContain("<figure");
        }
    }

    [Test]
    public async Task OnPaper_TurnsTheRenderersBackgroundWhite_AndKeepsEverythingElse()
    {
        using SKBitmap bitmap = new(4, 4);
        bitmap.Erase(ScenePalette.Dark.Background);
        bitmap.SetPixel(1, 1, SKColors.Red);
        using SKData data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        using SKBitmap paper = SKBitmap.Decode(ScoutingStageRenderer.OnPaper(data.ToArray()));

        using (Assert.Multiple())
        {
            await Assert.That(paper.GetPixel(0, 0)).IsEqualTo(SKColors.White);
            await Assert.That(paper.GetPixel(3, 3)).IsEqualTo(SKColors.White);
            await Assert.That(paper.GetPixel(1, 1)).IsEqualTo(SKColors.Red);
        }
    }
}
