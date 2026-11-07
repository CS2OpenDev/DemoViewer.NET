#region

using System.Globalization;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Builds the node view model for a face: the trigger, gate and scope as chips; the <c>match:</c> form as
///     facet rows written the way the author wrote them; the <c>compute:</c>, flag, <c>where:</c> or
///     <c>when:</c> text as the expression line and, for an authorable node, as editors. The writers given
///     here are what every row and picker writes through, with the node as the first argument.
/// </summary>
/// <param name="writeFacet">Writes a facet of the node: a value to set, or null to remove it. Null writes nowhere.</param>
/// <param name="writeField">Writes a field of the node: a value to set, or null to remove it. Null writes nowhere.</param>
public sealed class CanvasNodeFactory(
    Action<CanvasNodeViewModel, string, string?>? writeFacet = null,
    Action<CanvasNodeViewModel, string, string?>? writeField = null)
{
    /// <summary>A factory whose nodes write nowhere, for a canvas that is only drawn.</summary>
    public static CanvasNodeFactory ReadOnly { get; } = new();

    public CanvasNodeViewModel Create(NodeFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        List<string> chips = [];
        List<CanvasFacetViewModel> facets = [];
        List<string> available = [];
        Dictionary<string, RulesTypeKind> availableKinds = new(StringComparer.Ordinal);
        List<string> expression = [];
        List<CanvasExpressionViewModel> lines = [];
        CanvasNodeViewModel? built = null;
        // Rows write through the node they belong to; the node writes through the factory's writers.
        void Facet(string facet, string? value) => built?.WriteFacet(facet, value);
        void Field(string key, string? value) => built?.WriteField(key, value);

        switch (face)
        {
            case ScaffoldFace scaffold:
                if (scaffold.IsPerPlayer)
                {
                    chips.Add("each player");
                }

                break;
            case HighlightFace highlight:
                AddScope(chips, highlight.Per);
                expression.Add("when " + highlight.When);
                lines.Add(new CanvasExpressionViewModel("when", "when", highlight.When ?? "", Field));
                lines.Add(new CanvasExpressionViewModel("title", "title", highlight.Title ?? "", Field));
                lines.Add(new CanvasExpressionViewModel("score", "score", highlight.Score?.ToString(CultureInfo.InvariantCulture) ?? "", Field));
                AddReads(chips, highlight.CrossRulesetReads);
                break;
            case UndeclaredFace undeclared:
                if (!undeclared.IsHighlight)
                {
                    AddTrigger(chips, undeclared.View, undeclared.Events);
                }

                AddReads(chips, undeclared.CrossRulesetReads);
                break;
            case StatFace stat:
                AddTrigger(chips, stat.View, stat.Events);
                if (stat.Gate.Length > 0)
                {
                    chips.Add("while " + stat.Gate);
                }

                AddScope(chips, stat.Per);
                foreach (MatchFacetRow row in stat.Form.Rows)
                {
                    if (row.IsSet)
                    {
                        facets.Add(new CanvasFacetViewModel(row.Name, row.Type.Kind, row.Value!.ToMatchValue(), Facet));
                    }
                    else
                    {
                        available.Add(row.Name);
                        availableKinds[row.Name] = row.Type.Kind;
                    }
                }

                foreach (MatchBinding unknown in stat.Form.Unknown)
                {
                    facets.Add(new CanvasFacetViewModel(unknown.Key, RulesTypeKind.None,
                        MatchFacetValue.From(unknown.Test)?.ToMatchValue() ?? "", Facet));
                }

                if (stat is ComputeFace)
                {
                    if (stat.KindArg is { } formula)
                    {
                        expression.Add("= " + formula);
                    }

                    lines.Add(new CanvasExpressionViewModel("compute", "=", stat.KindArg ?? "", Field));
                }
                else if (stat is FlagFace)
                {
                    if (stat.KindArg is { } condition)
                    {
                        expression.Add("when " + condition);
                    }

                    lines.Add(new CanvasExpressionViewModel("when", "when", stat.KindArg ?? "", Field));
                }

                if (!string.IsNullOrWhiteSpace(stat.Trigger?.Where))
                {
                    expression.Add("where " + stat.Trigger.Where);
                }

                if (stat is not ComputeFace)
                {
                    lines.Add(new CanvasExpressionViewModel("where", "where", stat.Trigger?.Where ?? "", Field));
                }

                AddReads(chips, stat.CrossRulesetReads);
                break;
        }

        if (!face.IsAuthorable && !face.IsScaffold)
        {
            chips.Add("from " + face.Ruleset);
        }

        built = new CanvasNodeViewModel
        {
            Face = face,
            Id = face.Id,
            Ruleset = face.Ruleset,
            Kind = face.Kind,
            Label = face.Label,
            View = (face as StatFace)?.View ?? (face as UndeclaredFace)?.View,
            Chips = chips,
            Facets = facets,
            AvailableFacets = available,
            AvailableFacetKind = name => availableKinds.GetValueOrDefault(name, RulesTypeKind.None),
            Expression = expression.Count == 0 ? null : string.Join('\n', expression),
            Lines = lines,
            Per = face.Per,
            Gate = face.Gate,
            SourceLine = face.SourceLine,
            IsAuthorable = face.IsAuthorable,
            Diagnostics = face.Diagnostics
        };
        CanvasNodeViewModel node = built;
        node.AttachWriters((facet, value) => writeFacet?.Invoke(node, facet, value), (key, value) => writeField?.Invoke(node, key, value));
        return node;
    }

    // The view as the author named it; only a trigger the resolver gave no view shows its wire events.
    private static void AddTrigger(List<string> chips, string? view, IReadOnlyList<string> events)
    {
        if (!string.IsNullOrEmpty(view))
        {
            chips.Add("on " + view);
            return;
        }

        foreach (string eventName in events)
        {
            chips.Add("on " + eventName);
        }
    }

    private static void AddScope(List<string> chips, string per)
    {
        if (per.Length > 0)
        {
            chips.Add("per " + per);
        }
    }

    private static void AddReads(List<string> chips, IReadOnlyList<string> reads)
    {
        foreach (string read in reads)
        {
            chips.Add("reads " + read);
        }
    }
}
