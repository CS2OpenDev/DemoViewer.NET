using DemoViewer.NET.Extensions.StratBook.Modules.Situations;

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The shipped seam's follow: asked for after the open, the seek and the tab switch have all landed, and never
///     when the caller names nobody or the seek did not land.
/// </summary>
public class SituationPlaybackFollowTests
{
    private const string Demo = "/demos/a.dem";
    private const ulong Grim = 76561190000000003;

    [Test]
    public async Task AFollowId_IsFollowedLast_AfterTheOpenSeekAndTabSwitch()
    {
        Rig rig = new(loaded: null);

        bool landed = await rig.Seam.SeekAsync(Demo, 640, Grim);

        using (Assert.Multiple())
        {
            await Assert.That(landed).IsTrue();
            await Assert.That(rig.Calls).IsEquivalentTo(
                ["open " + Demo, "seek 640", "tab " + SituationPlaybackSeek.PlaybackTabId, $"follow {Grim}"],
                TUnit.Assertions.Enums.CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task NoFollowId_FollowsNobody()
    {
        Rig rig = new(loaded: Demo);

        await Assert.That(await rig.Seam.SeekAsync(Demo, 10)).IsTrue();
        await Assert.That(await rig.Seam.SeekAsync(Demo, 20, 0)).IsTrue();
        await Assert.That(rig.Calls.Any(c => c.StartsWith("follow", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ASeekThatDidNotLand_FollowsNobody()
    {
        Rig failedOpen = new(loaded: null) { OpenLands = false };
        Rig tabOff = new(loaded: Demo) { TabOnStrip = false };

        using (Assert.Multiple())
        {
            await Assert.That(await failedOpen.Seam.SeekAsync(Demo, 10, Grim)).IsFalse();
            await Assert.That(await tabOff.Seam.SeekAsync(Demo, 10, Grim)).IsFalse();
            await Assert.That(failedOpen.Calls.Concat(tabOff.Calls).Any(c => c.StartsWith("follow", StringComparison.Ordinal)))
                .IsFalse();
        }
    }

    [Test]
    public async Task ADeclinedFollow_DoesNotUnlandTheSeek()
    {
        Rig rig = new(loaded: Demo) { FollowAnswer = false };

        await Assert.That(await rig.Seam.SeekAsync(Demo, 10, Grim)).IsTrue();
        await Assert.That(rig.Calls[^1]).IsEqualTo($"follow {Grim}");
    }

    [Test]
    public async Task ASeamWithNoFollow_StillSeeks()
    {
        List<int> seeks = [];
        SituationPlaybackSeek seam = new(() => Demo, _ => Task.FromResult(true), seeks.Add, _ => true);

        await Assert.That(await seam.SeekAsync(Demo, 10, Grim)).IsTrue();
        await Assert.That(seeks).IsEquivalentTo([10]);
    }

    private sealed class Rig
    {
        private string? _loaded;

        public Rig(string? loaded)
        {
            _loaded = loaded;
            Seam = new SituationPlaybackSeek(() => _loaded, path =>
                {
                    Calls.Add("open " + path);
                    if (OpenLands)
                    {
                        _loaded = path;
                    }

                    return Task.FromResult(OpenLands);
                }, tick => Calls.Add("seek " + tick), id =>
                {
                    Calls.Add("tab " + id);
                    return TabOnStrip;
                }, steamId =>
                {
                    Calls.Add($"follow {steamId}");
                    return FollowAnswer;
                });
        }

        public List<string> Calls { get; } = [];

        public bool OpenLands { get; init; } = true;

        public bool TabOnStrip { get; init; } = true;

        public bool FollowAnswer { get; init; } = true;

        public SituationPlaybackSeek Seam { get; }
    }
}
