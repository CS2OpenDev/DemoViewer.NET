namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>The explicit asset root refusal on <c>export</c> and <c>fixture capture</c>, which need a demo to reach it.</summary>
[NotInParallel]
[Category("RealDemo")]
public class ExportAssetsRootTests
{
    [Test]
    public async Task Export_MissingRoot_IsExitTwo_NotARadarlessVideo()
    {
        string outPath = Path.Combine(Path.GetTempPath(), "dv2d-missing-root-" + Guid.NewGuid().ToString("N") + ".gif");
        CliRun run = Dv2d.InProcess("export", "--demo", Dv2d.RequireDemo(), "--from", "0", "--to", "2",
            "--format", "gif", "--fps", "20", "--out", outPath, "--no-encode", "--assets",
            Path.Combine(Path.GetTempPath(), "dv2d-no-such-root"), "--json");

        await Assert.That(run.ExitCode).IsEqualTo(2);
    }

    // A capture under a mistyped root found no map version and wrote the entry without its pin, so the golden
    // stale-assets guard never checked it.
    [Test]
    public async Task FixtureCapture_MissingRoot_IsExitTwo_AndWritesNoEntry()
    {
        string corpus = Path.Combine(Path.GetTempPath(), "dv2d-capture-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        try
        {
            CliRun run = Dv2d.InProcess("fixture", "capture", "--demo", Dv2d.RequireDemo(), "--frame", "0", "--name", "missing-root",
                "--corpus", corpus, "--assets", Path.Combine(Path.GetTempPath(), "dv2d-no-such-root"), "--json");

            using (Assert.Multiple())
            {
                await Assert.That(run.ExitCode).IsEqualTo(2);
                await Assert.That(Directory.EnumerateFileSystemEntries(corpus, "*", SearchOption.AllDirectories))
                    .IsEmpty();
            }
        }
        finally
        {
            Directory.Delete(corpus, true);
        }
    }
}
