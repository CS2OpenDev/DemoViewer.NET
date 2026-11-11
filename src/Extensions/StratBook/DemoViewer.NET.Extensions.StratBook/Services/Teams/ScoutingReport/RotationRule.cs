namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>
///     One CT's rotation in one planted round: the player's own samples from first contact up to and including the
///     first one on the hit site, exactly as the round positions file has them.
/// </summary>
/// <param name="Key">The player's key (<see cref="SetupRoundCapture.KeyOf" />).</param>
/// <param name="Name">The name the demo's roster had.</param>
/// <param name="Samples">The samples from contact to the arrival, both included; at least two.</param>
/// <param name="BeforePlant">The arrival came before the plant.</param>
public sealed record RotationTrail(
    string Key,
    string Name,
    IReadOnlyList<SetupTimedSample> Samples,
    bool BeforePlant)
{
    /// <summary>Where the player stood at contact.</summary>
    public SetupTimedSample Start => Samples[0];

    /// <summary>The first sample on the hit site.</summary>
    public SetupTimedSample Arrival => Samples[^1];

    /// <summary>Seconds after contact of the arrival.</summary>
    public double ArrivalSeconds => Arrival.Seconds;
}

/// <summary>
///     What counts as a rotation. A CT player of a planted round (the plant site is the hit site; a round without a
///     plant has no known hit site and is not read) is a candidate when the file has them at contact (their first
///     sample after contact is within <see cref="MaxStepSeconds" /> of it), their setup window was off the hit site,
///     their last setup sample was off it, and so was their sample at contact. A candidate rotated when, walking
///     their samples forward from contact:
///     <list type="bullet">
///         <item>no two consecutive samples are more than <see cref="MaxStepSeconds" /> apart (a death, a
///         disconnect or a missing step ends the track) and none moves faster than
///         <see cref="MaxUnitsPerSecond" /> across the floor (a teleport or a spectator slot);</item>
///         <item>they left their contact spot (a sample more than <see cref="LeaveUnits" /> from it) by
///         <see cref="PlantSlackSeconds" /> after the plant at the latest, so a CT who held until the plant and
///         walked over afterwards is a retake, not a rotation;</item>
///         <item>a sample lands on the hit site (by the map's areas) inside the followed window, and that first one
///         is at least <see cref="MinUnits" /> from the contact spot, so a CT already at the edge of the site does
///         not count.</item>
///     </list>
///     The trail stops at that first sample on the site, so leaving and coming back later never shows; an arrival
///     after the plant still counts when the move began before it, and says so.
/// </summary>
public static class RotationRule
{
    /// <summary>The most seconds between two consecutive samples of one track: one cadence step plus slack.</summary>
    public const double MaxStepSeconds = 1.5;

    /// <summary>The fastest a player can cover the floor; CS2 runs at 250 units a second at most.</summary>
    public const double MaxUnitsPerSecond = 500;

    /// <summary>How far from the contact spot a sample has to be for the player to have left it.</summary>
    public const double LeaveUnits = 150;

    /// <summary>How far the arrival has to be from the contact spot.</summary>
    public const double MinUnits = 400;

    /// <summary>How long after the plant the player may still be seen leaving: one sample step.</summary>
    public const double PlantSlackSeconds = 1;

    /// <summary>
    ///     The player was off the hit site and tracked at contact, so a rotation of theirs would be seen: the
    ///     rounds' denominator.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="hit">The hit site's area.</param>
    /// <param name="areas">The map's areas.</param>
    public static bool IsCandidate(SetupRoundPlayer player, SetupArea hit, SetupAreaClassifier areas)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(areas);
        if (player.AfterContact is not [var start, ..] || start.Seconds > MaxStepSeconds
                                                        || areas.Of(start.Sample) == hit)
        {
            return false;
        }

        return player.Setup.Count == 0
               || (areas.Of(player.Setup) != hit && areas.Of(player.Setup[^1]) != hit);
    }

    /// <summary>The player's rotation onto the hit site, or null when the rule above does not hold.</summary>
    /// <param name="player">The player.</param>
    /// <param name="hit">The hit site's area.</param>
    /// <param name="plantSeconds">Seconds from contact to the plant.</param>
    /// <param name="areas">The map's areas.</param>
    public static RotationTrail? Trail(SetupRoundPlayer player, SetupArea hit, double plantSeconds,
        SetupAreaClassifier areas)
    {
        if (!IsCandidate(player, hit, areas))
        {
            return null;
        }

        IReadOnlyList<SetupTimedSample> after = player.AfterContact;
        SetupSample origin = after[0].Sample;
        bool left = false;
        for (int i = 1; i < after.Count; i++)
        {
            SetupTimedSample previous = after[i - 1];
            SetupTimedSample sample = after[i];
            double step = sample.Seconds - previous.Seconds;
            if (step <= 0 || step > MaxStepSeconds
                          || Floor(previous.Sample, sample.Sample) > MaxUnitsPerSecond * step)
            {
                return null;
            }

            if (!left && Distance(origin, sample.Sample) > LeaveUnits)
            {
                if (sample.Seconds > plantSeconds + PlantSlackSeconds)
                {
                    return null;
                }

                left = true;
            }

            if (areas.Of(sample.Sample) != hit)
            {
                continue;
            }

            return left && Distance(origin, sample.Sample) >= MinUnits
                ? new RotationTrail(player.Key, player.Name, [.. after.Take(i + 1)], sample.Seconds < plantSeconds)
                : null;
        }

        return null;
    }

    private static double Floor(SetupSample p, SetupSample q)
    {
        double dx = p.X - q.X;
        double dy = p.Y - q.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double Distance(SetupSample p, SetupSample q)
    {
        double dx = p.X - q.X;
        double dy = p.Y - q.Y;
        double dz = p.Z - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
