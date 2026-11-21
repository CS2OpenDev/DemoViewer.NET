#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     The writer's own bookkeeping, over <see cref="SyntheticDemo" />: what it records in
///     <c>DEM_FileInfo</c> and what it leaves on disk. None of it needs a real demo, and the round-trip
///     suite (which does) cannot reach either case: it trims round 1 into an empty directory.
/// </summary>
public sealed class DemoTrimWriterTests
{
    private static readonly int[] _sourceRoundStarts = [100, 500, 900, 1300];

    /// <summary>
    ///     A checkpoint entry drops the stream before it, so the rounds that started there are not in
    ///     the file and neither are their scrubber markers. The first kept round's own start stays even
    ///     when the checkpoint landed a little after it: that is the round the file opens on.
    /// </summary>
    [Test]
    public async Task CheckpointEntry_DropsRoundStartsFromBeforeTheWindow()
    {
        (byte[] raw, ParsedDemo demo) = SyntheticDemo.Build(_sourceRoundStarts);
        TrimWindow window = new(0, 0, 520, 1000, 1, [560, 1010], true);

        IReadOnlyList<int> kept = WriteAndReadRoundStarts(demo, raw, window, TrimVariant.V1);

        await Assert.That(string.Join(",", kept)).IsEqualTo("500,900");
    }

    /// <summary>A contiguous trim keeps every frame from 0, so every round start up to the cut is real.</summary>
    [Test]
    public async Task ContiguousTrim_KeepsEveryRoundStartUpToTheCut()
    {
        (byte[] raw, ParsedDemo demo) = SyntheticDemo.Build(_sourceRoundStarts);
        TrimWindow window = new(0, 0, SyntheticDemo.PacketTick, 1000, 1, [560, 1010], false);

        IReadOnlyList<int> kept = WriteAndReadRoundStarts(demo, raw, window, TrimVariant.V0);

        await Assert.That(string.Join(",", kept)).IsEqualTo("100,500,900");
    }

    /// <summary>
    ///     A source with no DEM_FileInfo (an unfinalised recording) must not come out with a header
    ///     offset that names the end of the file. The format says 0 when the frame is absent.
    /// </summary>
    [Test]
    public async Task SourceWithoutFileInfo_GetsAZeroFileInfoOffset()
    {
        (byte[] raw, ParsedDemo demo) = SyntheticDemo.Build(withFileInfo: false);
        TrimWindow window = new(0, 0, SyntheticDemo.PacketTick, 1000, 1, [], false);
        string dir = CreateTempDir();
        try
        {
            string outPath = Path.Combine(dir, "trim.dem");
            DemoTrimWriter.Write(demo, raw, window, TrimVariant.V0, outPath);

            byte[] written = File.ReadAllBytes(outPath);
            await Assert.That(BitConverter.ToInt32(written, 8)).IsEqualTo(0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static IReadOnlyList<int> WriteAndReadRoundStarts(
        ParsedDemo demo, byte[] raw, TrimWindow window, TrimVariant variant)
    {
        string dir = CreateTempDir();
        try
        {
            string outPath = Path.Combine(dir, "trim.dem");
            DemoTrimWriter.Write(demo, raw, window, variant, outPath);
            return SyntheticDemo.RoundStartTicks(outPath, demo);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dv-trimmer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
