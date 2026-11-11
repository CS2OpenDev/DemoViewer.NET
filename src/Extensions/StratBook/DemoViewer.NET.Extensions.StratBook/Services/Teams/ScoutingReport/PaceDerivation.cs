namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>A T round's pace, by its plant time.</summary>
public enum PaceClass
{
    /// <summary>Planted by <see cref="PaceDerivation.FastSeconds" />.</summary>
    Fast,

    /// <summary>Planted after the fast cut and by <see cref="PaceDerivation.LateSeconds" />.</summary>
    Default,

    /// <summary>Planted after the late cut, or not at all.</summary>
    Late
}

/// <summary>One planted round on the pace strip.</summary>
/// <param name="Round">The round.</param>
/// <param name="Site">The site.</param>
/// <param name="Seconds">Seconds after freeze end.</param>
public sealed record PlantDot(TeamRound Round, BombSite Site, double Seconds);

/// <summary>Time to first contact for one buy.</summary>
/// <param name="Buy">The team's buy.</param>
/// <param name="Rounds">The rounds of that buy with a contact; their clips sit around the contact.</param>
/// <param name="Of">Every round of that buy.</param>
/// <param name="MedianSeconds">The median contact, or null with none.</param>
public sealed record ContactByBuy(BuyType Buy, TendencyBucket Rounds, int Of, double? MedianSeconds);

/// <summary>
///     The Pace strip's numbers over a team's T rounds: the pace classes, the plant times per site, the rounds
///     with no plant, and time to first contact by buy. The cut points are fixed at 0:35 and 1:15 (spec Q11);
///     a round planted at exactly a cut point is in the faster class.
/// </summary>
public static class PaceDerivation
{
    /// <summary>A plant by this many seconds after freeze end is a fast execute.</summary>
    public const double FastSeconds = 35;

    /// <summary>A plant after this many seconds, or none, is late.</summary>
    public const double LateSeconds = 75;

    /// <summary>The axis end of the plant strip: a round's length.</summary>
    public const double AxisSeconds = 115;

    /// <summary>The class of a plant time; null (no plant) is late.</summary>
    /// <param name="plantSeconds">Seconds after freeze end, or null.</param>
    public static PaceClass ClassOf(double? plantSeconds) => plantSeconds switch
    {
        null => PaceClass.Late,
        <= FastSeconds => PaceClass.Fast,
        <= LateSeconds => PaceClass.Default,
        _ => PaceClass.Late
    };

    /// <summary>"Fast execute, plant by 0:35" and the like.</summary>
    /// <param name="pace">The class.</param>
    public static string Label(PaceClass pace) => pace switch
    {
        PaceClass.Fast => "Fast execute, plant by 0:35",
        PaceClass.Default => "Default into execute, 0:35–1:15",
        _ => "Late or no plant"
    };

    /// <summary>The three classes in order, each with its rounds; the clip of a planted round sits around the plant.</summary>
    /// <param name="rounds">The T rounds.</param>
    public static IReadOnlyList<TendencyBucket> Classes(IEnumerable<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<TeamRound> list = [.. rounds];
        return
        [
            .. Enum.GetValues<PaceClass>().Select(c => new TendencyBucket(Label(c),
                [.. list.Where(r => ClassOf(r.SecondsTo(r.Facts.PlantTick)) == c).Select(PlantClip)]))
        ];
    }

    /// <summary>Every planted round on A or B, by plant time.</summary>
    /// <param name="rounds">The T rounds.</param>
    public static IReadOnlyList<PlantDot> Plants(IEnumerable<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        return
        [
            .. rounds.Where(r => r.Facts.PlantTick is not null && r.Facts.PlantSite is BombSite.A or BombSite.B)
                .Select(r => new PlantDot(r, r.Facts.PlantSite, r.SecondsTo(r.Facts.PlantTick)!.Value))
                .OrderBy(d => d.Seconds)
        ];
    }

    /// <summary>The rounds with no plant.</summary>
    /// <param name="rounds">The T rounds.</param>
    public static IReadOnlyList<TeamRound> NoPlant(IEnumerable<TeamRound> rounds) =>
        [.. rounds.Where(r => r.Facts.PlantTick is null)];

    /// <summary>Time to first contact for each buy with a round, eco to full, then pistol.</summary>
    /// <param name="rounds">The rounds.</param>
    public static IReadOnlyList<ContactByBuy> ContactByBuy(IEnumerable<TeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<TeamRound> list = [.. rounds];
        List<ContactByBuy> result = [];
        foreach (BuyType buy in (BuyType[])[BuyType.Eco, BuyType.Semi, BuyType.Force, BuyType.Full, BuyType.Pistol])
        {
            List<TeamRound> of = [.. list.Where(r => r.Buy == buy)];
            if (of.Count == 0)
            {
                continue;
            }

            List<TeamRound> contact = [.. of.Where(r => r.Facts.FirstContactTick is not null)];
            result.Add(new ContactByBuy(buy,
                new TendencyBucket(EconomyDerivation.ShortLabel(buy),
                    [.. contact.Select(r => r.Around(r.Facts.FirstContactTick!.Value))]),
                of.Count,
                TeamRounds.Median(contact.Select(r => r.SecondsTo(r.Facts.FirstContactTick)!.Value))));
        }

        return result;
    }

    private static TendencyRound PlantClip(TeamRound round) =>
        round.Facts.PlantTick is { } plant ? round.Around(plant) : round.Clip;
}
