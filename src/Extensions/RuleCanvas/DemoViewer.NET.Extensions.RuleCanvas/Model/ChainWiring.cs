#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>One wire to draw, between two keys the canvas has nodes for.</summary>
/// <param name="Source">The firing, gating or read node.</param>
/// <param name="Target">The node written.</param>
/// <param name="Text">What the wire says: the event for a write, the test for a logic input, <c>reads</c> for a read.</param>
/// <param name="Condition">The lowered condition, for the hover, or null.</param>
/// <param name="IsRead">A value read rather than a write; drawn dashed.</param>
public sealed record WireSpec(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target, string Text, string? Condition, bool IsRead)
{
    /// <summary>The same wire as a placement edge.</summary>
    public RulesetDocumentEdge AsEdge() => new(Source, Target, Text);
}

/// <summary>
///     The wires of a composition: the chain's when it built, else the declared reads. One wire per
///     distinct reference between two drawn nodes; what a wire means is written on the wire. Pure.
/// </summary>
public static class ChainWiring
{
    /// <summary>The wires among <paramref name="drawn" />, in the chain's order.</summary>
    public static IReadOnlyList<WireSpec> Wires(RulesetCorpus.Composed composed, IReadOnlySet<RulesetDocumentNodeKey> drawn)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(drawn);
        List<WireSpec> wires = [];
        if (composed.Chain.Edges.Count > 0)
        {
            foreach (ChainEdge edge in composed.Chain.Edges)
            {
                if (drawn.Contains(edge.Source) && drawn.Contains(edge.Target))
                {
                    wires.Add(new WireSpec(edge.Source, edge.Target, WireText(edge),
                        string.IsNullOrWhiteSpace(edge.Condition) ? null : edge.Condition, edge.IsRead));
                }
            }

            return wires;
        }

        HashSet<(RulesetDocumentNodeKey, RulesetDocumentNodeKey)> seen = [];
        foreach (RulesetDocumentEdge edge in composed.Graph.Edges)
        {
            if (drawn.Contains(edge.Source) && drawn.Contains(edge.Target) && seen.Add((edge.Source, edge.Target)))
            {
                wires.Add(new WireSpec(edge.Source, edge.Target, edge.Reference, null, true));
            }
        }

        return wires;
    }

    /// <summary>
    ///     What the wire says: the event for a write, with what it does when that is not a value; the test
    ///     for a logic input; <c>reads</c> for a read.
    /// </summary>
    public static string WireText(ChainEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (edge.IsRead)
        {
            return "reads";
        }

        return edge.Effect switch
        {
            EdgeEffect.Activate => edge.Label + " on",
            EdgeEffect.Deactivate => edge.Label + " off",
            EdgeEffect.Conjunction => "and " + (edge.Condition ?? ""),
            EdgeEffect.Disjunction => "or " + (edge.Condition ?? ""),
            _ => edge.Label
        };
    }
}
