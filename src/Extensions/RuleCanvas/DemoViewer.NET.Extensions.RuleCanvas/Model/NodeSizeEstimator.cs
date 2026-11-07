#region

using Avalonia;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     A hand model of the node template's size, from what the face shows, used for the first placement and
///     for fitting the viewport before the containers are measured. Each constant names one row of the
///     template; a template change that moves a row is a change here too.
/// </summary>
public static class NodeSizeEstimator
{
    private const double BaseWidth = 250;
    private const double HeaderHeight = 40;
    private const double ChipRowHeight = 24;
    private const double FacetRowHeight = 16;
    private const double ExpressionHeight = 20;
    private const double ConnectorHeight = 22;
    private const double DiagnosticHeight = 18;
    private const double PickerRowHeight = 30;
    private const double LineHeight = 26;
    private const int LineWrapColumns = 44;
    private const double EditableWidth = 360;

    public static Size Estimate(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return new Size(WidthOf(node), HeightOf(node));
    }

    public static double WidthOf(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        int longest = Math.Max(node.Id.Length + (node.Label?.Length ?? 0) + 8,
            node.Expression?.Split('\n').Max(l => l.Length) ?? 0);
        longest = Math.Max(longest, Math.Min(LineWrapColumns, node.Lines.Max(l => (int?)l.Text.Length) ?? 0) + 8);
        longest = Math.Max(longest, (node.Inputs.Max(i => (int?)i.Title.Length) ?? 0) + 14);
        double width = Math.Clamp(BaseWidth + Math.Max(0, longest - 30) * 6, BaseWidth, 520);
        // The picker row and an expression editor are wider than any chip row.
        return node.IsAuthorable && !node.IsScaffold ? Math.Max(width, EditableWidth) : width;
    }

    public static double HeightOf(CanvasNodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        double chips = node.Chips.Count == 0 ? 0 : ChipRowHeight * Math.Ceiling(node.Chips.Count / 3.0);
        double rows = Math.Max(node.Facets.Count * FacetRowHeight,
            Math.Max(node.Inputs.Count, node.Outputs.Count) * ConnectorHeight);
        double expression = node.Expression is null ? 0 : ExpressionHeight * node.Expression.Split('\n').Length;
        double diagnostics = node.Diagnostics.Count * DiagnosticHeight;
        // The editors an authorable stat can show: the picker row, the add-facet picker, and one line per
        // expression, each wrapping at the editor's width. Estimated whether or not the node is editable yet.
        double editors = 0;
        if (node.IsAuthorable && !node.IsScaffold)
        {
            editors = PickerRowHeight + (node.IsHighlight ? 0 : ConnectorHeight)
                      + node.Lines.Sum(l => LineHeight + Math.Max(0, (l.Text.Length - 1) / LineWrapColumns) * 16);
        }

        return HeaderHeight + chips + rows + expression + diagnostics + editors + 16;
    }
}
