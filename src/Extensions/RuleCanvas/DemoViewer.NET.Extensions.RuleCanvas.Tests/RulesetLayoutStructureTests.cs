#region

using System.Text.Json;
using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The sidecar's second generation: folds and frames beside the positions. Both directions of the
///     compatibility promise are tested: a positions-only sidecar loads with no fold and no frame, and a
///     sidecar this build writes still places every node under a reader of the old shape, which is written
///     out here as the handful of lines it was.
/// </summary>
public class RulesetLayoutStructureTests
{
    private static readonly RulesetDocumentNodeKey _kills = new("player_round", "round_kills");
    private static readonly RulesetDocumentNodeKey _deaths = new("player_round", "round_deaths");
    private static readonly RulesetDocumentNodeKey _kast = new("player_round", "round_kast");
    private static readonly RulesetDocumentNodeKey _frags = new("player_round", "frags");

    private static CanvasGroup Frame(params RulesetDocumentNodeKey[] members) => new("Kills", members, new Rect(10, 20, 300, 200));

    // The old shape's reader, as it was: nodes with x and y, nothing else asked for.
    private static Dictionary<string, Point> OldShapeReader(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        Dictionary<string, Point> positions = [];
        foreach (JsonProperty entry in document.RootElement.GetProperty("nodes").EnumerateObject())
        {
            if (entry.Value.TryGetProperty("x", out JsonElement x) && entry.Value.TryGetProperty("y", out JsonElement y))
            {
                positions[entry.Name] = new Point(x.GetDouble(), y.GetDouble());
            }
        }

        return positions;
    }

    [Test]
    public async Task AnOldSidecar_LoadsWithNoFoldAndNoFrame()
    {
        RulesetNodeLayout layout = RulesetLayoutStore.Parse(
            """
            {"version":1,"nodes":{"player_round.round_kills":{"x":10,"y":20},"player_round.round_deaths":{"x":30,"y":40}}}
            """);
        using (Assert.Multiple())
        {
            await Assert.That(layout.Count).IsEqualTo(2);
            await Assert.That(layout.Collapsed).IsEmpty();
            await Assert.That(layout.Groups).IsEmpty();
            await Assert.That(layout.IsEmpty).IsFalse();
        }
    }

    [Test]
    public async Task FoldsAndFrames_RoundTripThroughTheFile()
    {
        using RulesTree tree = RulesTree.New();
        string ruleset = tree.User("player_round", "ruleset: player_round\n");
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_kills, new Point(1, 2))
            .With(_deaths, new Point(3, 4))
            .WithCollapsed(_kills, true)
            .WithCollapsed(_kast, true)
            .WithGroup(Frame(_kills, _deaths));

        await Assert.That(RulesetLayoutStore.Save(ruleset, arranged)).IsTrue();
        RulesetNodeLayout reloaded = RulesetLayoutStore.Load(ruleset);
        using (Assert.Multiple())
        {
            await Assert.That(reloaded.PositionFor(_kills)).IsEqualTo(new Point(1, 2));
            await Assert.That(reloaded.IsCollapsed(_kills)).IsTrue();
            await Assert.That(reloaded.IsCollapsed(_kast)).IsTrue().Because("a fold without a position is still stored");
            await Assert.That(reloaded.IsCollapsed(_deaths)).IsFalse();
            await Assert.That(reloaded.PositionFor(_kast)).IsNull().Because("a fold alone places nothing");
            await Assert.That(reloaded.Groups.Count).IsEqualTo(1);
            await Assert.That(reloaded.Groups[0].Title).IsEqualTo("Kills");
            await Assert.That(reloaded.Groups[0].Rect).IsEqualTo(new Rect(10, 20, 300, 200));
            await Assert.That(reloaded.Groups[0].Members).IsEquivalentTo([_kills, _deaths]);
            await Assert.That(reloaded.GroupOf(_deaths)).IsNotNull();
            await Assert.That(reloaded.GroupOf(_kast)).IsNull();
        }
    }

    [Test]
    public async Task ASidecarWithFoldsAndFrames_StillPlacesEveryNode_UnderTheOldReader()
    {
        RulesetNodeLayout arranged = RulesetNodeLayout.Empty
            .With(_kills, new Point(1, 2))
            .With(_deaths, new Point(3, 4))
            .WithCollapsed(_kills, true)
            .WithCollapsed(_kast, true)
            .WithGroup(Frame(_kills, _deaths));

        Dictionary<string, Point> old = OldShapeReader(RulesetLayoutStore.Serialize(arranged));
        using (Assert.Multiple())
        {
            await Assert.That(old["player_round.round_kills"]).IsEqualTo(new Point(1, 2));
            await Assert.That(old["player_round.round_deaths"]).IsEqualTo(new Point(3, 4));
            await Assert.That(old.ContainsKey("player_round.round_kast")).IsFalse().Because("a fold-only entry has no x and y, so the old reader skips it");
        }
    }

    [Test]
    public async Task ALayoutWithNoFoldAndNoFrame_IsWrittenInTheOldShape()
    {
        string json = RulesetLayoutStore.Serialize(RulesetNodeLayout.Empty.With(_kills, new Point(1, 2)));
        using (Assert.Multiple())
        {
            await Assert.That(json).DoesNotContain("groups");
            await Assert.That(json).DoesNotContain("collapsed");
            await Assert.That(json.Replace(" ", "").Replace("\n", "").Replace("\r", ""))
                .IsEqualTo("""{"version":1,"nodes":{"player_round.round_kills":{"x":1,"y":2}}}""");
        }
    }

    [Test]
    public async Task AFrame_NeedsItsRectangleAndOneReadableMember_OrIsSkipped()
    {
        RulesetNodeLayout layout = RulesetLayoutStore.Parse(
            """
            {"version":1,"nodes":{},"groups":[
              {"title":"ok","members":["player_round.round_kills"],"x":1,"y":2,"width":30,"height":40},
              {"title":"no rect","members":["player_round.round_kills"]},
              {"title":"no members","members":[],"x":1,"y":2,"width":30,"height":40},
              {"title":"bad member","members":["unqualified"],"x":1,"y":2,"width":30,"height":40},
              {"title":"negative","members":["player_round.round_kills"],"x":1,"y":2,"width":-30,"height":40},
              "not an object"]}
            """);
        using (Assert.Multiple())
        {
            await Assert.That(layout.Groups.Count).IsEqualTo(1);
            await Assert.That(layout.Groups[0].Title).IsEqualTo("ok");
            await Assert.That(layout.IsEmpty).IsFalse().Because("a frame alone is an arrangement worth keeping");
        }
    }

    [Test]
    public async Task AFrameOnlyLayout_IsSaved_AndClearedLikeAnyOther()
    {
        using RulesTree tree = RulesTree.New();
        string ruleset = tree.User("player_round", "ruleset: player_round\n");
        string sidecar = RulesetLayoutStore.SidecarPathFor(ruleset);

        await Assert.That(RulesetLayoutStore.Save(ruleset, RulesetNodeLayout.Empty.WithGroup(Frame(_kills)))).IsTrue();
        await Assert.That(File.Exists(sidecar)).IsTrue();
        await Assert.That(RulesetLayoutStore.Load(ruleset).Groups.Count).IsEqualTo(1);

        await Assert.That(RulesetLayoutStore.Save(ruleset, RulesetNodeLayout.Empty.WithGroup(Frame(_kills)).WithoutGroup(Frame(_kills)))).IsTrue();
        await Assert.That(File.Exists(sidecar)).IsFalse().Because("no position, no fold, no frame is the absence of a file");
    }

    [Test]
    public async Task ANode_IsInOneFrameAtMost_AndAFrameLeftEmptyGoes()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty
            .WithGroup(Frame(_kills, _deaths))
            .WithGroup(new CanvasGroup("Kast", [_deaths, _kast], new Rect(0, 0, 1, 1)));
        using (Assert.Multiple())
        {
            await Assert.That(layout.Groups.Count).IsEqualTo(2);
            await Assert.That(layout.GroupOf(_deaths)!.Title).IsEqualTo("Kast").Because("the later frame takes the member");
            await Assert.That(layout.GroupOf(_kills)!.Members).IsEquivalentTo([_kills]);
        }

        RulesetNodeLayout taken = layout.WithGroup(new CanvasGroup("All", [_kills, _deaths, _kast], new Rect(0, 0, 1, 1)));
        await Assert.That(taken.Groups.Count).IsEqualTo(1).Because("both earlier frames lost their every member");
        await Assert.That(RulesetNodeLayout.Empty.WithGroup(new CanvasGroup("none", [], new Rect(0, 0, 1, 1))).IsEmpty).IsTrue();
    }

    [Test]
    public async Task WithoutGroup_MatchesByMembers_NotByTitleOrPlace()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.WithGroup(Frame(_kills, _deaths));
        RulesetNodeLayout removed = layout.WithoutGroup(new CanvasGroup("another title", [_deaths, _kills], new Rect(9, 9, 9, 9)));
        using (Assert.Multiple())
        {
            await Assert.That(removed.Groups).IsEmpty();
            await Assert.That(removed.IsEmpty).IsTrue();
            await Assert.That(ReferenceEquals(layout.WithoutGroup(Frame(_kast)), layout)).IsTrue().Because("no such frame, nothing changes");
        }
    }

    [Test]
    public async Task Renamed_CarriesThePositionTheFoldAndTheMembership()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty
            .With(_kills, new Point(1, 2))
            .With(_deaths, new Point(3, 4))
            .WithCollapsed(_kills, true)
            .WithGroup(Frame(_kills, _deaths));

        RulesetNodeLayout renamed = layout.Renamed(_kills, _frags);
        using (Assert.Multiple())
        {
            await Assert.That(renamed.PositionFor(_frags)).IsEqualTo(new Point(1, 2));
            await Assert.That(renamed.PositionFor(_kills)).IsNull();
            await Assert.That(renamed.IsCollapsed(_frags)).IsTrue();
            await Assert.That(renamed.IsCollapsed(_kills)).IsFalse();
            await Assert.That(renamed.GroupOf(_frags)!.Members).IsEquivalentTo([_frags, _deaths]);
            await Assert.That(renamed.GroupOf(_kills)).IsNull();
            await Assert.That(renamed.PositionFor(_deaths)).IsEqualTo(new Point(3, 4)).Because("the other node is untouched");
            await Assert.That(ReferenceEquals(layout.Renamed(_kills, _kills), layout)).IsTrue();
        }
    }

    [Test]
    public async Task Overlaid_TakesPositionsPerNode_AndFoldsAndFramesWholesale()
    {
        RulesetNodeLayout saved = RulesetNodeLayout.Empty
            .With(_kills, new Point(1, 2))
            .With(_kast, new Point(9, 9))
            .WithCollapsed(_deaths, true)
            .WithGroup(Frame(_kills, _deaths));
        RulesetNodeLayout onScreen = RulesetNodeLayout.Empty
            .With(_kills, new Point(5, 6))
            .With(_deaths, new Point(7, 8));

        RulesetNodeLayout merged = saved.Overlaid(onScreen);
        using (Assert.Multiple())
        {
            await Assert.That(merged.PositionFor(_kills)).IsEqualTo(new Point(5, 6)).Because("what is on screen wins");
            await Assert.That(merged.PositionFor(_kast)).IsEqualTo(new Point(9, 9)).Because("a stale position is kept");
            await Assert.That(merged.IsCollapsed(_deaths)).IsFalse().Because("the screen holds no fold, so there is none");
            await Assert.That(merged.Groups).IsEmpty();
            await Assert.That(ReferenceEquals(saved.Overlaid(RulesetNodeLayout.Empty), saved)).IsTrue().Because("nothing on screen, the sidecar decides");
        }
    }

    [Test]
    public async Task Without_ForgetsThePositionOnly()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.With(_kills, new Point(1, 2)).WithCollapsed(_kills, true);
        RulesetNodeLayout trimmed = layout.Without(_kills);
        using (Assert.Multiple())
        {
            await Assert.That(trimmed.PositionFor(_kills)).IsNull();
            await Assert.That(trimmed.IsCollapsed(_kills)).IsTrue();
            await Assert.That(trimmed.IsEmpty).IsFalse();
            await Assert.That(trimmed.Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Serialize_OrdersGroupMembers_SoOneArrangementIsOneFile()
    {
        RulesetNodeLayout forwards = RulesetNodeLayout.Empty.WithGroup(Frame(_kills, _deaths));
        RulesetNodeLayout backwards = RulesetNodeLayout.Empty.WithGroup(Frame(_deaths, _kills));
        await Assert.That(RulesetLayoutStore.Serialize(forwards)).IsEqualTo(RulesetLayoutStore.Serialize(backwards));
    }
}
