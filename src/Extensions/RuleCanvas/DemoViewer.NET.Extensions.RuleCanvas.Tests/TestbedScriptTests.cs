#region

using System.Diagnostics;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     scripts/rule-canvas-testbed.sh run under bash with a fake <c>dotnet</c> first on the path, so what it
///     copies, keeps, wipes and refuses is checked without starting the app.
/// </summary>
public class TestbedScriptTests
{
    private static string Script => Path.Combine(Path.GetDirectoryName(RulesTree.RepoRulesDirectory)!, "scripts", "rule-canvas-testbed.sh");

    private sealed class Sandbox : IDisposable
    {
        public Sandbox()
        {
            Directory.CreateDirectory(Bin);
            string shim = Path.Combine(Bin, "dotnet");
            File.WriteAllText(shim, "#!/bin/sh\necho \"SHIM $* USERDIR=$CS2DEMOKIT_USER_RULES_DIR\"\n");
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rulecanvas-tbscript-" + Guid.NewGuid().ToString("N"));

        private string Bin => Path.Combine(Root, "bin");

        public string Home => Path.Combine(Root, "home");

        public (int Code, string Output) Run(params string[] args)
        {
            ProcessStartInfo start = new("/bin/bash") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Script);
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            start.Environment["PATH"] = Bin + ":/usr/bin:/bin";
            start.Environment["HOME"] = Home;
            start.Environment.Remove("DV_TESTBED_DIR");
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp tree that outlives the test is tolerable.
            }
        }
    }

    private static string[] Fixtures => Names(FixtureRulesetsTests.TestbedDirectory);

    private static string[] Names(string dir) =>
        [.. Directory.EnumerateFiles(dir, "*.rules.yaml").Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal)];

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new SkipTestException("the launcher is a bash script");
        }
    }

    [Test]
    public async Task APlainRun_CopiesEveryFixture_KeepsEdits_AndPointsTheAppAtTheCopy()
    {
        SkipOnWindows();
        using Sandbox box = new();
        string dir = Path.Combine(box.Home, ".cache", "demoviewer-rule-canvas-testbed", "rules");
        (int code, string output) = box.Run();
        using (Assert.Multiple())
        {
            await Assert.That(code).IsEqualTo(0);
            await Assert.That(Names(dir)).IsEquivalentTo(Fixtures);
            await Assert.That(output).Contains($"testbed rules: {dir}");
            await Assert.That(output).Contains("DemoViewer.NET.Desktop -c Release USERDIR=" + dir);
        }

        string show = Path.Combine(dir, "tb_show.rules.yaml");
        File.AppendAllText(show, "# edited\n");
        File.Delete(Path.Combine(dir, "tb_kinds.rules.yaml"));
        (code, output) = box.Run("--no-build");
        using (Assert.Multiple())
        {
            await Assert.That(code).IsEqualTo(0);
            await Assert.That(File.ReadAllText(show)).EndsWith("# edited\n").Because("a plain run copies only what is missing");
            await Assert.That(File.Exists(Path.Combine(dir, "tb_kinds.rules.yaml"))).IsTrue();
            await Assert.That(output).Contains("tb_show (edited; --reset restores it)");
            await Assert.That(output).Contains("-c Release --no-build USERDIR=");
        }

        (code, _) = box.Run("--reset");
        using (Assert.Multiple())
        {
            await Assert.That(code).IsEqualTo(0);
            await Assert.That(File.ReadAllText(show)).IsEqualTo(File.ReadAllText(Path.Combine(FixtureRulesetsTests.TestbedDirectory, "tb_show.rules.yaml")));
        }

        File.Delete(Path.Combine(dir, "tb_kinds.rules.yaml"));
        (code, output) = box.Run("--prepare");
        using (Assert.Multiple())
        {
            await Assert.That(code).IsEqualTo(0);
            await Assert.That(File.Exists(Path.Combine(dir, "tb_kinds.rules.yaml"))).IsTrue().Because("--prepare copies like a plain run");
            await Assert.That(output).Contains($"CS2DEMOKIT_USER_RULES_DIR=\"{dir}\" artifacts/bin/DemoViewer.NET.Desktop/debug/DemoViewer.NET.Desktop");
            await Assert.That(output).DoesNotContain("USERDIR=").Because("--prepare never runs dotnet");
        }
    }

    [Test]
    public async Task TheScript_RefusesTheFixtureDirectory_AndAFolderItDidNotMake()
    {
        SkipOnWindows();
        using Sandbox box = new();
        string[] before = [.. Directory.EnumerateFiles(FixtureRulesetsTests.TestbedDirectory).Order()];
        (int intoFixtures, string said) = box.Run("--reset", "--dir", FixtureRulesetsTests.TestbedDirectory);

        string foreign = Path.Combine(box.Root, "my-rules");
        RulesTree.Touch(Path.Combine(foreign, "mine.rules.yaml"), "ruleset: mine\n");
        (int intoForeign, _) = box.Run("--reset", "--dir", foreign);
        using (Assert.Multiple())
        {
            await Assert.That(intoFixtures).IsEqualTo(2);
            await Assert.That(said).Contains("refusing");
            await Assert.That(Directory.EnumerateFiles(FixtureRulesetsTests.TestbedDirectory).Order()).IsEquivalentTo(before);
            await Assert.That(intoForeign).IsEqualTo(2);
            await Assert.That(File.Exists(Path.Combine(foreign, "mine.rules.yaml"))).IsTrue();
            await Assert.That(Directory.EnumerateFiles(foreign).Count()).IsEqualTo(1);
        }
    }
}
