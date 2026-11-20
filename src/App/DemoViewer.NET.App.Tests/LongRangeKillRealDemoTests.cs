#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using DemoViewer.NET.Modules.Highlights;
using DemoViewer.NET.TestSupport;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     <c>long_range_kill</c> gates on <c>player_death.distance</c>, which the game sends in meters: the longest
///     AWP lines on a pro dust2 or mirage demo measure 50 to 60. A threshold written in world units (2500) never
///     fired on any demo. Point <c>DEMO_PATH</c> at a full competitive demo, never the incomplete tour sample.
/// </summary>
[NotInParallel]
[Category("RealDemo")]
public class LongRangeKillRealDemoTests
{
    [Test]
    public async Task LongRangeKill_FiresOnAFullMatch()
    {
        string path = DemoTestHelper.RequireDemo();
        ParsedDemo parsed = DemoTestHelper.GetOrParse(path);
        MergedRulesBuild rules =
            new(() => YamlConfigLoader.LoadWithOverlay(RuleSetLocator.ResolveShippedRulesDirectory(), null));

        IReadOnlyList<HighlightFired> fired = rules.BareRun(parsed).Highlights;

        await Assert.That(fired.Count(h => h.HighlightId == "long_range_kill")).IsGreaterThan(0)
            .Because("a full match has kills past the long-range threshold, which is in meters");
    }
}
