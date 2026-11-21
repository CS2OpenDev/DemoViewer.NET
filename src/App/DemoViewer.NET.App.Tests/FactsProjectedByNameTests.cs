#region

using CS2DemoKit.Analysis.Output;
using DemoViewer.NET.Services.Facts;

#endregion

namespace DemoViewer.NET.AppTests;

public class FactsProjectedByNameTests
{
    private static MetricTable Table(string name, string dimension) => new(name, [dimension], [], []);

    // A user ruleset that kept a shipped ruleset's table name puts two outputs under it; the lookup must not throw.
    [Test]
    public async Task TwoTablesUnderOneName_TheFirstStands_AndTheLookupDoesNotThrow()
    {
        MetricTable shipped = Table("round_facts", "round");
        MetricTable copy = Table("round_facts", "other");

        Dictionary<string, MetricTable> byName =
            FactsEvaluator.ProjectedByName([shipped, copy, Table("player_match", "player")]);

        using (Assert.Multiple())
        {
            await Assert.That(byName.Count).IsEqualTo(2);
            await Assert.That(byName["round_facts"]).IsSameReferenceAs(shipped);
        }
    }
}
