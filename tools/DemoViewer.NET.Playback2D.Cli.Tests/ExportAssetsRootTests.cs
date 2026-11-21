namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>The explicit asset root refusal on <c>export</c>, which needs a demo to reach it.</summary>
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
}
