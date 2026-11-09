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
/// <param name="IsGate">A gate holding the target open while it is active; drawn as a long dash.</param>
/// <param name="IsAnd">A logic input the target conjoins; drawn in the and colour.</param>
/// <param name="IsOr">A logic input the target disjoins; drawn in the or colour.</param>
public sealed record WireSpec(RulesetDocumentNodeKey Source, RulesetDocumentNodeKey Target, string Text, string? Condition, bool IsRead,
    bool IsGate = false, bool IsAnd = false, bool IsOr = false)
{
    /// <summary>For a read into a show node, the column the wire stands for; null elsewhere.</summary>
    public int? Column { get; init; }

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
        // A chain of surface nodes alone (a failed build) carries no declared wires; the document's reads stand in.
        if (!composed.Chain.IsLowered)
        {
            HashSet<(RulesetDocumentNodeKey, RulesetDocumentNodeKey)> seen = [];
            foreach (RulesetDocumentEdge edge in composed.Graph.Edges)
            {
                if (drawn.Contains(edge.Source) && drawn.Contains(edge.Target) && seen.Add((edge.Source, edge.Target)))
                {
                    // A define fires the stat; everything else in the document graph is a read.
                    wires.Add(new WireSpec(edge.Source, edge.Target, edge.Reference, null, !edge.Source.IsDefine));
                }
            }
        }

        foreach (ChainEdge edge in composed.Chain.Edges)
        {
            if (drawn.Contains(edge.Source) && drawn.Contains(edge.Target))
            {
                // The connective classes are exclusive with read and gate, as WireText is.
                bool logic = !edge.IsRead && !edge.IsGate;
                wires.Add(new WireSpec(edge.Source, edge.Target, WireText(edge),
                    string.IsNullOrWhiteSpace(edge.Condition) ? null : edge.Condition, edge.IsRead, edge.IsGate,
                    logic && edge.Effect == EdgeEffect.Conjunction, logic && edge.Effect == EdgeEffect.Disjunction) { Column = edge.Column });
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
            // The chain names what is read when it is a companion ("reads .count"); plain reads say "reads".
            return edge.Label.Length == 0 ? "reads" : edge.Label;
        }

        if (edge.IsGate)
        {
            // A guard's gate says "while"; a comparison gate carries its test ("while round.number > 0").
            return edge.Label.Length == 0 ? "while" : edge.Label;
        }

        return edge.Effect switch
        {
            EdgeEffect.Activate => edge.Label + " on",
            EdgeEffect.Deactivate => edge.Label + " off",
            EdgeEffect.Conjunction => "and " + (edge.Authored ?? edge.Condition ?? ""),
            EdgeEffect.Disjunction => "or " + (edge.Authored ?? edge.Condition ?? ""),
            _ => edge.Label
        };
    }

    /// <summary>
    ///     The row of a flat <c>when:</c> a logic wire stands for: the row spelled exactly as the engine's
    ///     lowered condition, else the row on the path the source is read under with the same comparator
    ///     and literal, else the first row on that path. Null for a nested condition or no match.
    /// </summary>
    public static ConditionRow? AuthoredRow(string? whenText, string readPath, string? lowered)
    {
        ArgumentNullException.ThrowIfNull(readPath);
        ConditionExpression when = ConditionExpression.Parse(whenText);
        if (!when.IsFlat || when.Rows.Count == 0)
        {
            return null;
        }

        static string Squash(string? text) => string.Concat((text ?? "").Where(c => !char.IsWhiteSpace(c)));
        string condition = Squash(lowered);
        if (condition.Length > 0)
        {
            ConditionRow? spelled = when.Rows.FirstOrDefault(r => Squash(r.Text) == condition);
            if (spelled is not null)
            {
                return spelled;
            }
        }

        ConditionExpression loweredRows = ConditionExpression.Parse(lowered);
        ConditionRow? operand = loweredRows is { IsFlat: true, Rows.Count: 1 } ? loweredRows.Rows[0] : null;
        ConditionRow? byPath = null;
        foreach (ConditionRow row in when.Rows.Where(r => r.Path == readPath))
        {
            if (operand is not null && row.Comparator == operand.Comparator && Squash(row.Literal) == Squash(operand.Literal))
            {
                return row;
            }

            byPath ??= row;
        }

        return byPath;
    }
}
