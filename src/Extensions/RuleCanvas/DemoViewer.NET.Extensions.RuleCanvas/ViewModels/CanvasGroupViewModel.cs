#region

using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     A frame on the canvas around some nodes: an editor item beside the nodes, drawn under them, with a
///     title the user can edit in place. Layout, not document: it lives in the sidecar, so it is rebuilt from
///     <see cref="CanvasGroup" /> on every redraw and never written to the YAML. The container writes
///     <see cref="Location" /> back on drag and the grouping node writes <see cref="Size" /> on resize.
/// </summary>
public sealed partial class CanvasGroupViewModel : ObservableObject
{
    private readonly Action<CanvasGroupViewModel>? _changed;

    /// <param name="group">The frame as the layout stores it.</param>
    /// <param name="changed">Called when the title is committed, so the tab can write the sidecar.</param>
    public CanvasGroupViewModel(CanvasGroup group, Action<CanvasGroupViewModel>? changed = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        _changed = changed;
        Members = group.Members;
        _title = group.Title;
        _titleText = group.Title;
        _location = group.Rect.Position;
        _size = group.Rect.Size;
    }

    /// <summary>The nodes inside, by layout key, as stored. Dragging a node out is applied when the layout is captured.</summary>
    public IReadOnlyList<RulesetDocumentNodeKey> Members { get; }

    /// <summary>A frame drawn on a focused slice: its title cannot be edited, and nothing about it is written.</summary>
    public bool IsReadOnly { get; init; }

    /// <summary>What the header says.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string _title;

    /// <summary>The title editor's text; committed on Enter and on losing focus.</summary>
    [ObservableProperty]
    private string _titleText;

    /// <summary>True while the header shows the editor instead of the title.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTitleText))]
    private bool _isEditingTitle;

    /// <summary>Graph position of the frame's top-left; the container writes it back on drag.</summary>
    [ObservableProperty]
    private Point _location;

    /// <summary>The frame's size; the grouping node writes it back on resize.</summary>
    [ObservableProperty]
    private Size _size;

    /// <summary>The frame in graph coordinates, as the layout stores it.</summary>
    public Rect Rect => new(Location, Size);

    /// <summary>The header: the title, or a placeholder for an unnamed frame.</summary>
    public string HeaderText => Title.Length > 0 ? Title : "Group";

    public bool ShowTitleText => !IsEditingTitle;

    /// <summary>
    ///     A frame is never dimmed by a search; only nodes are. An instance property, since the container theme
    ///     binds it on frames and nodes alike.
    /// </summary>
    [ObservableProperty]
    private double _dimOpacity = 1;

    /// <summary>The frame as the layout stores it, with the members it still holds.</summary>
    public CanvasGroup ToGroup(IEnumerable<RulesetDocumentNodeKey> members) => new(Title, [.. members], Rect);

    [RelayCommand]
    private void BeginEditTitle()
    {
        if (IsReadOnly)
        {
            return;
        }

        TitleText = Title;
        IsEditingTitle = true;
    }

    /// <summary>Takes the editor's text as the title. Bound to Enter and to losing focus.</summary>
    [RelayCommand]
    public void CommitTitle()
    {
        IsEditingTitle = false;
        string text = TitleText.Trim();
        if (!string.Equals(text, Title, StringComparison.Ordinal))
        {
            Title = text;
            _changed?.Invoke(this);
        }
    }
}
