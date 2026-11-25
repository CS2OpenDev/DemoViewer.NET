namespace DemoViewer.NET.Services.DemoCache;

/// <summary>
///     The one freshness compare for a demo's recorded size and write time against the file's. The library keys
///     on local write-time ticks, so a change of the machine's time zone (or a value one side took in UTC) moves
///     every recorded stamp by the zone offset while the files stay as they were. Every offset in use is a whole
///     number of quarter hours and no two zones are more than 26 hours apart, so a write time that differs from
///     the recorded one by exactly such a step, at the same size, is read as the same file. A caller that matches
///     this way rewrites what it stored to the stamp it has now, so the next compare is exact again.
/// </summary>
internal static class DemoFileStamp
{
    private static readonly long Step = TimeSpan.FromMinutes(15).Ticks;
    private static readonly long Widest = TimeSpan.FromHours(26).Ticks;

    /// <summary>Same size, and the same write time or one a time zone shift away from it.</summary>
    /// <param name="recordedSize">The size recorded for the file.</param>
    /// <param name="recordedTicks">The write time recorded for the file, local or UTC ticks.</param>
    /// <param name="size">The file's size now.</param>
    /// <param name="ticks">The file's write time now, local or UTC ticks.</param>
    internal static bool Matches(long recordedSize, long recordedTicks, long size, long ticks) =>
        recordedSize == size && SameWriteTime(recordedTicks, ticks);

    /// <summary>The same write time, or one a time zone shift away from it.</summary>
    /// <param name="recorded">The write time recorded, local or UTC ticks.</param>
    /// <param name="now">The write time now, local or UTC ticks.</param>
    internal static bool SameWriteTime(long recorded, long now)
    {
        if (recorded == now)
        {
            return true;
        }

        // Ticks of a real write time are never negative, so the difference cannot overflow.
        long apart = Math.Abs(recorded - now);
        return recorded > 0 && now > 0 && apart <= Widest && apart % Step == 0;
    }
}
