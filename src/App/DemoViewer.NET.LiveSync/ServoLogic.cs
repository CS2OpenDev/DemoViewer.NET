namespace DemoViewer.NET.LiveSync;

/// <summary>
///     The drift servo's pure decision function — extracted from the
///     UI-coupled pump so the thresholds are unit-testable. While both sides play, CS2's ticks
///     are a drift REFERENCE: small error does nothing, moderate error bends DV's playback speed
///     (never a discrete seek — those pause DV and trigger the heavy entity re-seek), and only a
///     large divergence forces a hard resync.
/// </summary>
internal static class ServoLogic
{
    public enum Correction
    {
        /// <summary>In the locked band, speed already natural — do nothing.</summary>
        None,

        /// <summary>Back in the locked band after servo bending — restore the base speed.</summary>
        RestoreSpeed,

        /// <summary>Bend playback speed toward CS2's clock.</summary>
        AdjustSpeed,

        /// <summary>Divergence too large — discrete seek + play.</summary>
        HardResync
    }

    /// <summary>|err| at or below this: locked — no correction (restore 1× if the servo was bending).</summary>
    public const int LockedBand = 8;

    /// <summary>|err| at or below this: speed-servo band; above: hard resync.</summary>
    public const int ServoBand = 128;

    /// <summary>
    ///     Decides the correction for a drift error (CS2's mapped DV tick minus DV's current
    ///     tick; positive = DV is behind). <paramref name="servoEngaged" /> is whether a previous
    ///     decision bent the speed (so re-entering the locked band restores 1×).
    ///     <paramref name="baseSpeed" /> is the speed CS2 is actually running at: 1× normally, the
    ///     user's mirrored speed under "timescale-set". Every returned speed is relative to it, so
    ///     the servo never pulls DV off a timescale CS2 keeps running at.
    /// </summary>
    public static (Correction Kind, double Speed) Decide(long error, bool servoEngaged, double baseSpeed = 1.0)
    {
        long magnitude = Math.Abs(error);
        if (magnitude <= LockedBand)
        {
            return servoEngaged ? (Correction.RestoreSpeed, baseSpeed) : (Correction.None, baseSpeed);
        }

        if (magnitude <= ServoBand)
        {
            return (Correction.AdjustSpeed, baseSpeed * Math.Clamp(1.0 + error / 256.0, 0.75, 1.5));
        }

        return (Correction.HardResync, baseSpeed);
    }
}
