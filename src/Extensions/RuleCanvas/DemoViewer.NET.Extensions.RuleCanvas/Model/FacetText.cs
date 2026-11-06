#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     A <c>match:</c> facet's test the way the corpus writes it: a bare literal for equality, the operator
///     and literal otherwise, a range as <c>[low..high]</c>, a list membership as <c>in ...</c>. Read off
///     the engine's own parse, never re-derived from the YAML text.
/// </summary>
public static class FacetText
{
    public static string Of(UnaryTest? test) =>
        test switch
        {
            LiteralTest literal => literal.RawText,
            ComparisonTest comparison => comparison.Operator == ComparisonOperator.Equal
                ? comparison.LiteralRawText
                : Symbol(comparison.Operator) + " " + comparison.LiteralRawText,
            RangeTest range => $"[{range.Low}..{range.High}]",
            InListRefTest listRef => "in " + listRef.ListRef,
            InListLiteralTest listLiteral => "in [" + string.Join(", ", listLiteral.Items) + "]",
            null => "",
            _ => test.ToString() ?? ""
        };

    private static string Symbol(ComparisonOperator op) =>
        op switch
        {
            ComparisonOperator.Equal => "==",
            ComparisonOperator.NotEqual => "!=",
            ComparisonOperator.Greater => ">",
            ComparisonOperator.GreaterOrEqual => ">=",
            ComparisonOperator.Less => "<",
            ComparisonOperator.LessOrEqual => "<=",
            _ => ""
        };
}
