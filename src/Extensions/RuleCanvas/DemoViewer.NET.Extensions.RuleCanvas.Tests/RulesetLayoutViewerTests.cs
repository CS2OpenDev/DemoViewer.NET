#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The pinned viewer flag in the layout sidecar: stored per key beside the fold, written as <c>viewer</c>,
///     absent from a layout without one so the file stays the older shape byte for byte, read as nothing by a
///     sidecar that predates it, and carried through a rename and an overlay like the fold is.
/// </summary>
public class RulesetLayoutViewerTests
{
    private static readonly RulesetDocumentNodeKey _kills = new("player_round", "round_kills");
    private static readonly RulesetDocumentNodeKey _deaths = new("player_round", "round_deaths");
    private static readonly RulesetDocumentNodeKey _kast = new("player_round", "round_kast");
    private static readonly RulesetDocumentNodeKey _frags = new("player_round", "round_frags");

    [Test]
    public async Task APinnedViewer_RoundTripsThroughTheSidecar_WithOrWithoutAPosition()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty
            .With(_kills, new Point(10, 20))
            .WithPinned(_kills, true)
            .WithPinned(_kast, true)
            .WithCollapsed(_kast, true);

        string json = RulesetLayoutStore.Serialize(layout);
        RulesetNodeLayout reloaded = RulesetLayoutStore.Parse(json);
        using (Assert.Multiple())
        {
            await Assert.That(json).Contains("\"viewer\": true");
            await Assert.That(reloaded.IsPinned(_kills)).IsTrue();
            await Assert.That(reloaded.IsPinned(_kast)).IsTrue().Because("a viewer without a position is still stored");
            await Assert.That(reloaded.IsCollapsed(_kast)).IsTrue().Because("the fold and the viewer share the entry");
            await Assert.That(reloaded.IsPinned(_deaths)).IsFalse();
            await Assert.That(reloaded.PositionFor(_kills)).IsEqualTo(new Point(10, 20));
            await Assert.That(RulesetLayoutStore.Serialize(reloaded)).IsEqualTo(json).Because("one arrangement, one file");
        }
    }

    [Test]
    public async Task ALayoutWithoutAViewer_WritesNoViewerProperty()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.With(_kills, new Point(1, 2)).WithCollapsed(_deaths, true);
        string json = RulesetLayoutStore.Serialize(layout);
        using (Assert.Multiple())
        {
            await Assert.That(json).DoesNotContain("viewer");
            await Assert.That(RulesetLayoutStore.Parse(json).Pinned).IsEmpty();
        }
    }

    [Test]
    public async Task AnOlderSidecar_ReadsAsNoViewers_AndAnUnknownViewerValueIsNotAPin()
    {
        const string old = """
            {
              "version": 1,
              "nodes": {
                "player_round.round_kills": { "x": 10, "y": 20, "collapsed": true },
                "player_round.round_deaths": { "x": 30, "y": 40, "viewer": "yes" },
                "player_round.round_kast": { "viewer": false }
              }
            }
            """;
        RulesetNodeLayout layout = RulesetLayoutStore.Parse(old);
        using (Assert.Multiple())
        {
            await Assert.That(layout.Pinned).IsEmpty();
            await Assert.That(layout.IsCollapsed(_kills)).IsTrue();
            await Assert.That(layout.PositionFor(_deaths)).IsEqualTo(new Point(30, 40)).Because("a bad viewer value costs nothing else");
            await Assert.That(layout.IsEmpty).IsFalse();
        }
    }

    [Test]
    public async Task AViewerAlone_IsNotAnEmptyLayout_AndUnpinningTheLastOneIs()
    {
        RulesetNodeLayout pinned = RulesetNodeLayout.Empty.WithPinned(_kills, true);
        using (Assert.Multiple())
        {
            await Assert.That(pinned.IsEmpty).IsFalse();
            await Assert.That(pinned.WithPinned(_kills, true)).IsSameReferenceAs(pinned).Because("no change, no copy");
            await Assert.That(pinned.WithPinned(_kills, false)).IsSameReferenceAs(RulesetNodeLayout.Empty);
        }
    }

    [Test]
    public async Task ARename_CarriesTheViewer_AndAnOverlayTakesTheScreensViewers()
    {
        RulesetNodeLayout layout = RulesetNodeLayout.Empty.With(_kills, new Point(1, 2)).WithPinned(_kills, true).WithPinned(_deaths, true);
        RulesetNodeLayout renamed = layout.Renamed(_kills, _frags);
        RulesetNodeLayout screen = RulesetNodeLayout.Empty.With(_kast, new Point(5, 6)).WithPinned(_kast, true);
        RulesetNodeLayout merged = layout.Overlaid(screen);
        using (Assert.Multiple())
        {
            await Assert.That(renamed.IsPinned(_frags)).IsTrue();
            await Assert.That(renamed.IsPinned(_kills)).IsFalse();
            await Assert.That(renamed.IsPinned(_deaths)).IsTrue();
            await Assert.That(merged.IsPinned(_kast)).IsTrue();
            await Assert.That(merged.IsPinned(_kills)).IsFalse().Because("the screen holds no viewer there, so there is none");
            await Assert.That(merged.PositionFor(_kills)).IsEqualTo(new Point(1, 2)).Because("a position off screen may still be worth keeping");
        }
    }

    [Test]
    public async Task SaveThenLoad_KeepsTheViewer_AndRemovingEveryEntryRemovesTheFile()
    {
        using RulesTree tree = RulesTree.New();
        string ruleset = tree.User("player_round", "ruleset: player_round\n");
        await Assert.That(RulesetLayoutStore.Save(ruleset, RulesetNodeLayout.Empty.WithPinned(_kills, true))).IsTrue();
        await Assert.That(RulesetLayoutStore.Load(ruleset).IsPinned(_kills)).IsTrue();
        await Assert.That(RulesetLayoutStore.Save(ruleset, RulesetNodeLayout.Empty)).IsTrue();
        await Assert.That(File.Exists(RulesetLayoutStore.SidecarPathFor(ruleset))).IsFalse();
    }
}
