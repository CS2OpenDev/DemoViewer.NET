#region

using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Extensions.Testing;
using DemoViewer.NET.Modules.Playback2D;
using DemoViewer.NET.Playback2D.Zones;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The one-pager with real stage pictures: synthetic stars over two maps, drawn on the shipped radar art by
///     the app's renderer. The page lands in <see cref="HeadlessSession.ArtifactDir" /> as
///     <c>sample-one-pager.html</c> for a person to look at; the test checks each picture is a light 640 px PNG.
/// </summary>
[NotInParallel(TestLanes.HeadlessUi)]
[Category("Integration")]
public class ScoutingReportExportRenderTests
{
    private static string K(string map, string side, string layer, string buy = "All", string window = "",
        string text = "read") =>
        ScoutingReportStarKeys.Key(new ScoutingScope(map, side, layer, buy, window), text);

    // Marks at the centres of the map's largest places, so they land on the floor.
    private static List<ScoutingMapMark> Marks(string map, int skip, int count, ScoutingMarkShape shape,
        ScoutingColor color, float size, bool numbered = false)
    {
        using IMapAssetLease? lease = MapAssets.Acquire(map);
        List<ZoneArea> spots =
        [
            .. (lease?.Zones?.Zones.Areas ?? [])
                .GroupBy(a => a.PlaceId).Select(g => g.MaxBy(a => a.Area)!)
                .OrderByDescending(a => a.Area)
        ];
        return
        [
            .. spots.Skip(skip).Take(count).Select((a, i) => new ScoutingMapMark((float)a.CentroidX,
                (float)a.CentroidY, (float)a.Z, shape, color, size, numbered ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null))
        ];
    }

    [Test]
    public async Task TheOnePager_DrawsEachStarredStage_OnPaper_AndWritesTheSample()
    {
        HostedMapAssets.Install();
        ScoutingReportStageLayer nukeSetups = new("setups", "Setups",
            Marks("de_nuke", 0, 6, ScoutingMarkShape.Dot, ScoutingColor.Player1, 5f));
        ScoutingReportStageLayer nukeAwp = new("awp", "AWP",
            Marks("de_nuke", 6, 3, ScoutingMarkShape.Badge, ScoutingColor.Ct, 9f, numbered: true));
        ScoutingReportStageLayer mirageT = new("plants", "Plants",
            Marks("de_mirage", 0, 5, ScoutingMarkShape.Dot, ScoutingColor.T, 7f));
        ScoutingReportFindingSource[] sources =
        [
            new(K("de_nuke", "", "pool", "", "", "record"), ScoutingReportEditorViewModel.MapPoolSection,
                "de_nuke: 4 played, 3-1 (CT 31-17, T 22-24)", Map: "de_nuke"),
            new(K("de_nuke", "CT", "setups", "Full"), "de_nuke CT · Setups",
                "2-1-2 on full buys: one outside, one ramp, two B, one heaven (9 of 14 rounds)", Map: "de_nuke",
                Stage: nukeSetups),
            new(K("de_nuke", "CT", "awp", window: "0:30"), "de_nuke CT · AWP",
                "the AWP opens outside and falls back to heaven by 0:30 (7 of 12 rounds)", Map: "de_nuke", Stage: nukeAwp),
            new(K("de_nuke", "CT", "retakes"), "de_nuke CT · Retakes", "retake B from ramp: 3 of 5 won", Map: "de_nuke"),
            new(K("de_nuke", "T", "econ"), "de_nuke T · Economy", "force after a lost pistol in 4 of 4 halves",
                Map: "de_nuke"),
            new(K("de_mirage", "T", "plants"), "de_mirage T · Plants", "A plant default for 6 of 9 plants",
                Map: "de_mirage", Stage: mirageT),
            new(K("de_mirage", "T", "lurks", text: "who"), "de_mirage T · Lurks", "who lurks: <kyxsan> 5 of 8 rounds",
                Map: "de_mirage"),
            new(K("", "", "pool", "", "", "deciders"), ScoutingReportEditorViewModel.MapPoolSection,
                "Deciders: 2-1 (3 inferable)")
        ];
        string? html = null;
        List<(string Map, int Bytes)> drawn = [];
        ScoutingReportEditorViewModel editor = new(new ScoutingReportNotesStore(null),
            export: (text, _, _) =>
            {
                html = text;
                return "sample-one-pager.html";
            },
            header: () => new ScoutingReportHeader
            {
                RecordLine = "64% of 11 maps (7-4)",
                Ranks = ["VRS 7 (set 2026-11-01)", "HLTV 9 (set 2026-10-28)"],
                DateRangeLine = "Aug 3, 2026 to Nov 8, 2026",
                OpponentMixLine = "6 vs top-30 · 4 other · 1 unknown",
                HeadToHeadLine = "vs Us 1–2",
                Roster =
                [
                    new ScoutingReportRosterEntry("m0NESY", "AWP", "1.21"), new("kyxsan", "IGL", "0.94"),
                    new("NiKo", "Star", "1.17"), new("TeSSiMo", "Entry", "1.02"), new("Magisk", "Anchor", "1.05")
                ],
                RosterScope = "every map"
            },
            renderStage: (map, marks) =>
            {
                byte[]? png = ScoutingStageRenderer.TryRender(map, marks);
                drawn.Add((map, png?.Length ?? 0));
                return png;
            });
        editor.Load(Guid.NewGuid(), "Falcons", "11 demos across 2 maps", sources);
        editor.Summary = "Slow defaults on T; the AWP decides their CT rounds.";
        foreach (ScoutingReportFindingViewModel row in editor.Findings.ToList())
        {
            if (row.Text != "retake B from ramp: 3 of 5 won")
            {
                editor.ToggleStarCommand.Execute(row);
            }
        }

        editor.ExportCommand.Execute(null);
        string path = Path.Combine(HeadlessSession.ArtifactDir, "sample-one-pager.html");
        await File.WriteAllTextAsync(path, html);
        Console.WriteLine($"[one-pager] {path}");

        using (Assert.Multiple())
        {
            await Assert.That(string.Join(",", drawn.Select(d => d.Map))).IsEqualTo("de_nuke,de_mirage");
            await Assert.That(drawn.All(d => d.Bytes > 10_000)).IsTrue().Because("each stage is the real radar");
            await Assert.That(html!.Split("<figure").Length - 1).IsEqualTo(2);
            await Assert.That(html).DoesNotContain("retake B from ramp");
        }

        // A light picture: the corner (outside the radar) is white paper, and the size is the stage's.
        byte[] first = ScoutingStageRenderer.TryRender("de_mirage", mirageT.Marks)!;
        using SKBitmap bitmap = SKBitmap.Decode(first);
        using (Assert.Multiple())
        {
            await Assert.That(bitmap.Width).IsEqualTo(ScoutingStageRenderer.Size.Width);
            await Assert.That(bitmap.GetPixel(1, 1)).IsEqualTo(SKColors.White);
        }
    }
}
