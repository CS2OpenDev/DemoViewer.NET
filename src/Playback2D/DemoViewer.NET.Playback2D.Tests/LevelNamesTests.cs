#region

using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Levels;

#endregion

namespace DemoViewer.NET.Playback2DTests;

/// <summary>
///     Floor names are what a player calls the floor, never the Z band the split was made at.
/// </summary>
public class LevelNamesTests
{
    [Test]
    [Arguments("de_nuke")]
    [Arguments("de_vertigo")]
    [Arguments("DE_NUKE")]
    [Arguments("workshop/123456/de_nuke")]
    public async Task NamedMap_UsesItsOwnNames(string map)
    {
        await Assert.That(LevelNames.For(map, 0, 2)).IsEqualTo("Lower");
        await Assert.That(LevelNames.For(map, 1, 2)).IsEqualTo("Upper");
    }

    [Test]
    [Arguments("de_train")]
    [Arguments("")]
    [Arguments(null)]
    public async Task TwoLevels_FallBackToUpperAndLowerFloor(string? map)
    {
        await Assert.That(LevelNames.For(map, 0, 2)).IsEqualTo("Lower floor");
        await Assert.That(LevelNames.For(map, 1, 2)).IsEqualTo("Upper floor");
    }

    [Test]
    public async Task ThreeLevels_CountUpFromTheBottom_EvenOnANamedMap()
    {
        string[] names = [.. Enumerable.Range(0, 3).Select(i => LevelNames.For("de_nuke", i, 3))];

        await Assert.That(names).IsEquivalentTo(["Level 1", "Level 2", "Level 3"]);
    }

    [Test]
    public async Task NoName_EverCarriesAZBand()
    {
        List<string> names = [];
        foreach (string? map in new[] { "de_nuke", "de_train", null })
        {
            for (int count = 1; count <= 4; count++)
            {
                for (int i = 0; i < count; i++)
                {
                    names.Add(LevelNames.For(map, i, count));
                }
            }
        }

        await Assert.That(names.Where(n => n.Contains("z[", StringComparison.Ordinal) ||
            n.Contains("floor 0", StringComparison.Ordinal))).IsEmpty();
    }

    [Test]
    public async Task MapSpace_NamesItsLevels_ForItsMap()
    {
        MapSpace space = new() { MapName = "de_nuke" };
        space.Rebuild([new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)]);

        await Assert.That(space.Levels.Select(l => l.Name).ToList()).IsEquivalentTo(["Lower", "Upper"]);
    }

    [Test]
    public async Task MapSpace_RenamesOnTheNextRebuild_WhenTheMapArrivesLate_KeepingIds()
    {
        MapSpace space = new();
        FloorSlice[] bands = [new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)];
        space.Rebuild(bands);
        MapLevelId upper = space.Levels[1].Id;
        await Assert.That(space.Levels[1].Name).IsEqualTo("Upper floor");

        space.MapName = "de_vertigo";
        LevelSetChange change = space.Rebuild(bands);

        await Assert.That(change.Changed).IsTrue();
        await Assert.That(space.Levels[1].Id).IsEqualTo(upper);
        await Assert.That(space.Levels[1].Name).IsEqualTo("Upper");

        LevelSetChange again = space.Rebuild(bands);
        await Assert.That(again.IsEmpty).IsTrue();
    }

    [Test]
    public async Task Factory_TakesTheMapNameFromTheFrame()
    {
        MapSpaceFactory factory = new();
        factory.SetAuthoritativeFloors([new FloorSlice(-100000, -528), new FloorSlice(-528, 100000)]);

        factory.Update(new Scene2DFrame { Map = new SceneMapInfo { MapName = "de_nuke" } });

        await Assert.That(factory.Space.Levels.Select(l => l.Name).ToList()).IsEquivalentTo(["Lower", "Upper"]);
    }
}
