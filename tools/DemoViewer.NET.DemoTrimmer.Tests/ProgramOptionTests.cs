namespace DemoViewer.NET.DemoTrimmer.Tests;

/// <summary>
///     An option the command does not understand fails the run. Both cases used to be ignored, so a
///     wrong <c>--skip-boundaries</c> trimmed the warmup rounds and still reported a verified trim.
/// </summary>
public sealed class ProgramOptionTests
{
    private static readonly string[] _known = ["--out", "--skip-boundaries"];

    [Test]
    [Arguments("--skip-boundary")]
    [Arguments("--skip-boundaries=3")]
    public async Task AnUnknownOption_IsRefused(string option)
    {
        string[] args = ["trim", "demo.dem", "--out", "out", option, "3"];

        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => Program.RejectUnknownOptions(args, _known));

        await Assert.That(thrown.Message).Contains("unknown option");
    }

    [Test]
    public async Task KnownOptionsAndTheirValues_AreAccepted()
    {
        Program.RejectUnknownOptions(["trim", "demo.dem", "--out", "out", "--skip-boundaries", "3"], _known);

        await Assert.That(Program.IntOption(["trim", "d", "--skip-boundaries", "3"], "--skip-boundaries", 0))
            .IsEqualTo(3);
        await Assert.That(Program.IntOption(["trim", "d"], "--skip-boundaries", 7)).IsEqualTo(7);
    }

    [Test]
    [Arguments("three")]
    [Arguments("")]
    public async Task ANonIntegerOrMissingValue_IsRefused(string value)
    {
        string[] args = value.Length == 0
            ? ["trim", "demo.dem", "--skip-boundaries"]
            : ["trim", "demo.dem", "--skip-boundaries", value];

        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => Program.IntOption(args, "--skip-boundaries", 0));

        await Assert.That(thrown.Message).Contains("expects an integer");
    }
}
