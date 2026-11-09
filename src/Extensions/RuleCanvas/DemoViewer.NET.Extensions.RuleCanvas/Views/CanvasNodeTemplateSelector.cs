#region

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     Picks the item template by what the item is: an engine scaffold (an event, an enrichment, a context
///     flag) draws as one compact row, an authored entry as the full face, and a frame as a grouping node
///     under them. The editor takes one item template, so the choice is made here rather than by type.
/// </summary>
public sealed class CanvasNodeTemplateSelector : IDataTemplate
{
    /// <summary>The template for an authored stat or highlight.</summary>
    [Content]
    public IDataTemplate? Authored { get; set; }

    /// <summary>The template for an engine node. Falls back to <see cref="Authored" /> when not set.</summary>
    public IDataTemplate? Scaffold { get; set; }

    /// <summary>The template for a frame around nodes. A frame with no template is not drawn.</summary>
    public IDataTemplate? Group { get; set; }

    public bool Match(object? data) => data is CanvasNodeViewModel or CanvasGroupViewModel;

    public Control? Build(object? param)
    {
        if (param is CanvasGroupViewModel)
        {
            return Group?.Build(param);
        }

        CanvasNodeViewModel? node = param as CanvasNodeViewModel;
        IDataTemplate? template = node is { IsScaffold: true } ? Scaffold ?? Authored : Authored;
        return template?.Build(param);
    }
}
