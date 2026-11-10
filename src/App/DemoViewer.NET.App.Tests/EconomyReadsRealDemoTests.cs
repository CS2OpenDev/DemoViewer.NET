#region

using System.Diagnostics;
using System.Globalization;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.Services.DemoProcessing;
using DemoViewer.NET.Services.DemoProcessing.Collectors;
using DemoViewer.NET.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The engine's economy and timeout reads on a real pro demo, through the one forward pass the visit makes:
///     helmets, round spend and the tactical timeout counts at freeze end. Needs <c>DEMO_PATH</c> naming a pro MR12 demo (or a folder plus
///     <c>DEMO_PATH_PICK</c>), read in place.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class EconomyReadsRealDemoTests
{
    private const int RegulationRounds = 24;

    private static string RequirePinnedDemo() =>
        DemoTestHelper.ResolveDemoPathOverride(Environment.GetEnvironmentVariable(DemoTestHelper.DemoPathEnvVar),
            Environment.GetEnvironmentVariable("DEMO_PATH_PICK"), out string? problem)
        ?? throw new SkipTestException(problem ?? $"{DemoTestHelper.DemoPathEnvVar} names no demo");

    [Test]
    public async Task ForwardPass_ReadsHelmetsSpendAndTimeouts_AtFreezeEnd()
    {
        string path = RequirePinnedDemo();
        RulesetDoc doc = EconomyReadsTests.Load(EconomyReadsTests.ProbeYaml);

        using DemoReader reader = DemoReader.OpenFile(path, ForwardDemoPass.ReaderOptions(CancellationToken.None));
        ForwardDemoResult pass = ForwardDemoPass.Run(reader, ForwardNeeds.Rules, [doc], null,
            new HashSet<string> { "econ_probe" });
        MetricTable table = pass.Run!.ProjectConfiguredOutputs(pass.Demo).Single(t => t.Name == "econ_probe");
        List<Row> rows =
        [
            .. table.Rows.Select(r => new Row(Int(r, "round"), Int(r, "slot"), Int(r, "helmet") != 0,
                Int(r, "spent"), Int(r, "t_left"), Int(r, "ct_left")))
        ];

        // One value per round for the match reads.
        List<(int Round, int T, int Ct)> timeouts =
        [
            .. rows.Where(r => r.Round is >= 1 and <= RegulationRounds).GroupBy(r => r.Round).OrderBy(g => g.Key)
                .Select(g => (g.Key, g.First().TLeft, g.First().CtLeft))
        ];
        Console.WriteLine($"demo read: {path}");
        Console.WriteLine($"{Path.GetFileName(path)}: {rows.Count} rows, {timeouts.Count} regulation rounds");
        Console.WriteLine("T left : " + string.Join(" ", timeouts.Select(t => t.T)));
        Console.WriteLine("CT left: " + string.Join(" ", timeouts.Select(t => t.Ct)));
        foreach (int round in new[] { 1, 2, 13, 14 })
        {
            List<Row> of = [.. rows.Where(r => r.Round == round)];
            Console.WriteLine($"round {round}: helmets {of.Count(r => r.Helmet)}/{of.Count}, spent "
                + string.Join(",", of.Select(r => r.Spent)));
        }

        List<Row> pistols = [.. rows.Where(r => r.Round is 1 or 13)];
        List<Row> later = [.. rows.Where(r => r.Round is > 1 and <= RegulationRounds and not 13)];
        using (Assert.Multiple())
        {
            await Assert.That(timeouts.Count).IsGreaterThanOrEqualTo(16).Because("a pro map plays at least 16 rounds");
            await Assert.That(pistols.Count).IsGreaterThanOrEqualTo(10);

            // 800 to start, and a helmet costs 1000 with the vest: nobody has one at a pistol round's freeze end.
            await Assert.That(pistols.Count(r => r.Helmet)).IsEqualTo(0);
            await Assert.That(pistols.All(r => r.Spent is >= 0 and <= 800)).IsTrue();
            await Assert.That(pistols.Any(r => r.Spent > 0)).IsTrue().Because("pistol rounds still buy");

            // The buy rounds after: most players wear one, and spend past a pistol's money.
            await Assert.That(later.Count(r => r.Helmet) * 2).IsGreaterThan(later.Count);
            await Assert.That(later.Max(r => r.Spent)).IsGreaterThan(800);

            // Counts start full, only fall within a half, and never go below zero.
            await Assert.That(timeouts[0].T).IsGreaterThanOrEqualTo(1);
            await Assert.That(timeouts[0].Ct).IsEqualTo(timeouts[0].T);
            await Assert.That(timeouts.All(t => t.T is >= 0 and <= 4 && t.Ct is >= 0 and <= 4)).IsTrue();
            foreach ((int first, int last) in new[] { (1, 12), (13, RegulationRounds) })
            {
                List<(int Round, int T, int Ct)> half = [.. timeouts.Where(t => t.Round >= first && t.Round <= last)];
                for (int i = 1; i < half.Count; i++)
                {
                    await Assert.That(half[i].T).IsLessThanOrEqualTo(half[i - 1].T).Because($"T, round {half[i].Round}");
                    await Assert.That(half[i].Ct).IsLessThanOrEqualTo(half[i - 1].Ct)
                        .Because($"CT, round {half[i].Round}");
                }
            }

            await Assert.That(timeouts.Any(t => t.T < timeouts[0].T || t.Ct < timeouts[0].Ct)).IsTrue()
                .Because("a pro map with no tactical timeout at all proves nothing about the counts");
        }
    }

    /// <summary>
    ///     What the economy reads cost the merged run: the shipped rules alone, and with the probe ruleset added.
    ///     Interleaved and repeated, since a single timing drifts between sessions; the best of each is printed.
    ///     Asserts only that the runs completed.
    /// </summary>
    [Test]
    public async Task MergedRun_Cost_WithAndWithoutTheProbe()
    {
        string path = RequirePinnedDemo();
        IReadOnlyList<RulesetDoc> shipped = new MergedRulesBuild(() =>
            YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null)).Docs;
        IReadOnlyList<RulesetDoc> withProbe = [.. shipped, EconomyReadsTests.Load(EconomyReadsTests.ProbeYaml)];
        ParsedDemo parsed = MemoryMappedDemoSource.ParseFile(path);

        const int Rounds = 3;
        double[] best = [double.MaxValue, double.MaxValue];
        for (int i = 0; i < Rounds; i++)
        {
            best[0] = Math.Min(best[0], Time(() => ForwardDemoPass.Build(parsed, shipped, null, CollectorSet.Empty)));
            best[1] = Math.Min(best[1], Time(() => ForwardDemoPass.Build(parsed, withProbe, null, CollectorSet.Empty)));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(path)}, {parsed.Frames.Count} frames, best of {Rounds} (build + evaluate): "
            + $"shipped {best[0]:F0} ms, + probe {best[1]:F0} ms ({best[1] - best[0]:+0;-0} ms)"));
        await Assert.That(best.All(b => b < double.MaxValue)).IsTrue();

        double Time(Func<BuildResult> build)
        {
            Stopwatch watch = Stopwatch.StartNew();
            AnalysisRun run = DemoAnalysis.Evaluate(parsed, build(), new AnalysisOptions { CaptureSnapshots = false });
            watch.Stop();
            GC.KeepAlive(run);
            return watch.Elapsed.TotalMilliseconds;
        }
    }

    private static int Int(MetricRow row, string column) =>
        row.Values.GetValueOrDefault(column) switch
        {
            null => 0,
            bool b => b ? 1 : 0,
            IConvertible c => Convert.ToInt32(c, CultureInfo.InvariantCulture),
            var other => throw new InvalidOperationException($"{column} is {other.GetType().Name}")
        };

    private sealed record Row(int Round, int Slot, bool Helmet, int Spent, int TLeft, int CtLeft);
}
