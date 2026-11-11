namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>A T fake then rotate, as Suggested Tags' fake detector proposed it.</summary>
/// <param name="Round">The round.</param>
/// <param name="Fake">The site the T side showed.</param>
/// <param name="Real">The site it executed on.</param>
public sealed record FakeRound(int Round, BombSite Fake, BombSite Real)
{
    /// <summary>The T-side fake proposals among a demo's stored proposals; a label the detector does not write is skipped.</summary>
    /// <param name="proposals">Round, side and labels of each fake proposal.</param>
    public static IReadOnlyList<FakeRound> From(IEnumerable<(int Round, int Side, IReadOnlyDictionary<string, string> Labels)> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        List<FakeRound> rounds = [];
        foreach ((int round, int side, IReadOnlyDictionary<string, string> labels) in proposals)
        {
            if (side == 2
                && labels.TryGetValue("fake", out string? fake) && SiteOf(fake) is { } f
                && labels.TryGetValue("real", out string? real) && SiteOf(real) is { } r
                && f != r)
            {
                rounds.Add(new FakeRound(round, f, r));
            }
        }

        return rounds;
    }

    private static BombSite? SiteOf(string place) => place switch
    {
        "BombsiteA" => BombSite.A,
        "BombsiteB" => BombSite.B,
        _ => null
    };
}

/// <summary>One fake round the team defended: whether a CT set up on the real site left it before the plant.</summary>
/// <param name="Round">The round.</param>
/// <param name="Fake">The proposal.</param>
/// <param name="RotatedOff">A CT set up on the real site was off it after contact and before the plant.</param>
/// <param name="Rotator">The first of them to leave, or null.</param>
public sealed record FakeRotation(SetupHeatmapRound Round, FakeRound Fake, bool RotatedOff, string? Rotator);
