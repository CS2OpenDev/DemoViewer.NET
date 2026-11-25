namespace DemoViewer.NET.Playback2D.Cli.Tests;

/// <summary>
///     <c>--out</c> must never land on something the command reads. The write is a plain overwrite, so
///     a repeated path (a shell history edit, a tab completion) replaced the input with a PNG.
/// </summary>
[NotInParallel]
public class OutputGuardTests
{
    private static readonly string _scene =
        Path.Combine(Dv2d.CorpusDirectory, "scenes", "synthetic-empty.scene.json");

    [Test]
    public async Task Render_OutNamingItsOwnFixture_IsRefused_AndTheFixtureSurvives()
    {
        using TempDirectory temp = new();
        string fixture = Path.Combine(temp.Path, "scene.json");
        File.Copy(_scene, fixture);
        byte[] before = await File.ReadAllBytesAsync(fixture);

        // Spelled differently on purpose: the comparison has to be on the resolved path.
        string sameFile = Path.Combine(temp.Path, ".", "scene.json");
        CliRun run = Dv2d.InProcess("render", "--fixture", fixture, "--out", sameFile, "--cpu", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).Contains("--out");
        await Assert.That(await File.ReadAllBytesAsync(fixture)).IsEquivalentTo(before);
    }

    [Test]
    public async Task Render_OutNamingADemo_IsRefused_AndNothingIsWritten()
    {
        using TempDirectory temp = new();
        string outPath = Path.Combine(temp.Path, "match.dem");

        CliRun run = Dv2d.InProcess("render", "--fixture", _scene, "--out", outPath, "--cpu", "--json");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(File.Exists(outPath)).IsFalse();
    }

    /// <summary>The check <c>export</c> shares: an absent input is not a match, a respelled one is.</summary>
    [Test]
    public async Task TheGuard_ComparesResolvedPaths_AndSkipsAbsentInputs()
    {
        RenderCommand.RefuseOutputOverInput("out.webm", null, "", "match.json");

        await Assert.That(Throws.Capture<CliUsageException>(
            () => RenderCommand.RefuseOutputOverInput(Path.Combine("sub", "..", "ink.json"), null, "ink.json")))
            .IsNotNull();
        await Assert.That(Throws.Capture<CliUsageException>(
            () => RenderCommand.RefuseOutputOverInput("clip.DEM", "match.dem"))).IsNotNull();
    }
}
