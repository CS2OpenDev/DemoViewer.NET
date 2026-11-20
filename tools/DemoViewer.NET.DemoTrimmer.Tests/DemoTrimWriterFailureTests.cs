#region

using CS2DemoKit.Parser;

#endregion

namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     A trim that fails part-way must not cost the file it was about to replace. Re-running the
///     trimmer into a directory that already holds a good candidate is the normal way to use it.
/// </summary>
public sealed class DemoTrimWriterFailureTests
{
    private static readonly byte[] _previous = "a good candidate from an earlier run"u8.ToArray();

    [Test]
    public async Task AWriteThatThrowsPartWay_LeavesThePreviousOutputAlone()
    {
        (byte[] raw, ParsedDemo demo) = SyntheticDemo.Build(corruptFrame: true);
        TrimWindow window = new(0, 0, SyntheticDemo.PacketTick, 1000, 1, [560, 1010], false);

        string dir = Path.Combine(Path.GetTempPath(), "dv-trimmer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string outPath = Path.Combine(dir, "trim.dem");
            await File.WriteAllBytesAsync(outPath, _previous);

            Exception? thrown = null;
            try
            {
                DemoTrimWriter.Write(demo, raw, window, TrimVariant.V0, outPath);
            }
            catch (ArgumentException e)
            {
                thrown = e;
            }

            await Assert.That(thrown).IsNotNull();
            await Assert.That(await File.ReadAllBytesAsync(outPath)).IsEquivalentTo(_previous);
            await Assert.That(Directory.GetFiles(dir).Length).IsEqualTo(1)
                .Because("the partial file is not left beside it either");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
