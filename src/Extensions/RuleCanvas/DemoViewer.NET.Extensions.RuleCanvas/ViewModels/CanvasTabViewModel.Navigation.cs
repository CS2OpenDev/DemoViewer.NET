#region

using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.RuleCanvas.Model;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     Following a reference: a cross-ruleset read to the entry it names in the other ruleset, a define to
///     the stats firing on it and back, a diagnostic to its node. Each jump selects and asks the view to
///     frame the selection; a jump that switches the picker is remembered, two deep, so Alt+Left returns.
/// </summary>
public sealed partial class CanvasTabViewModel
{
    private const int HistoryDepth = 2;

    // Where a cross-ruleset jump left from: the ruleset and the node selected there, newest last.
    private readonly List<(string Ruleset, RulesetDocumentNodeKey? Selected)> _history = [];

    /// <summary>Raised on the UI thread after a navigation selected something, so the view frames the selection.</summary>
    public event Action? FrameRequested;

    /// <summary>Whether Alt+Left has somewhere to go back to.</summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>
    ///     Opens the ruleset a dependency output or a drawn foreign entry belongs to and selects the entry
    ///     there: <paramref name="entry" /> for a dependency node's output, the node's own id otherwise.
    ///     False when the node is the open document's own.
    /// </summary>
    public bool GoToDefinition(CanvasNodeViewModel node, string? entry = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.ForeignRuleset is not { } ruleset)
        {
            return false;
        }

        string id = node.IsDependency ? entry ?? "" : node.Id;
        return NavigateTo(ruleset, id.Length == 0 ? null : new RulesetDocumentNodeKey(ruleset, id));
    }

    /// <summary>Follows a cross-ruleset read wire to the entry it reads; false for a wire inside the open document.</summary>
    public bool GoToDefinition(CanvasConnectionViewModel wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return wire.Source.Owner is { } owner && GoToDefinition(owner, wire.Source.Title);
    }

    /// <summary>
    ///     Switches the picker to <paramref name="ruleset" />, selects <paramref name="select" /> when it names a
    ///     drawn node, and frames it. The place left is pushed on the history. False when the corpus has no such ruleset.
    /// </summary>
    public bool NavigateTo(string ruleset, RulesetDocumentNodeKey? select)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleset);
        if (SelectedRuleset is not { } from || Rulesets.All(r => r.Id != ruleset))
        {
            return false;
        }

        Push((from.Id, SelectedNodes.OfType<CanvasNodeViewModel>().FirstOrDefault()?.Key));
        OpenRuleset(ruleset);
        SelectAndFrame(select);
        return true;
    }

    /// <summary>Alt+Left: back to the ruleset and selection the last cross-ruleset jump left.</summary>
    [RelayCommand]
    private void NavigateBack()
    {
        if (_history.Count == 0)
        {
            return;
        }

        (string ruleset, RulesetDocumentNodeKey? selected) = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        OnPropertyChanged(nameof(CanGoBack));
        if (OpenRuleset(ruleset))
        {
            SelectAndFrame(selected);
        }
    }

    /// <summary>Selects and frames every stat that fires on the define <paramref name="define" />; nothing when none does.</summary>
    public void ShowStatsFiringOn(CanvasNodeViewModel define)
    {
        ArgumentNullException.ThrowIfNull(define);
        List<CanvasNodeViewModel> firing = [.. Connections
            .Where(w => ReferenceEquals(w.Source.Owner, define) && !w.IsRead && !w.IsGate && w.Target.Owner is not null)
            .Select(w => w.Target.Owner!)
            .Distinct()];
        if (firing.Count == 0)
        {
            return;
        }

        SelectedNodes.Clear();
        foreach (CanvasNodeViewModel node in firing)
        {
            SelectedNodes.Add(node);
        }

        FrameRequested?.Invoke();
    }

    /// <summary>Selects and frames the define <paramref name="stat" /> fires on; nothing for a stat on an event.</summary>
    public void GoToDefine(CanvasNodeViewModel stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        CanvasNodeViewModel? define = Connections
            .Where(w => ReferenceEquals(w.Target.Owner, stat) && !w.IsRead && !w.IsGate)
            .Select(w => w.Source.Owner)
            .FirstOrDefault(n => n is { IsDefine: true });
        if (define is not null)
        {
            SelectAndFrame(define.Key);
        }
    }

    private void SelectAndFrame(RulesetDocumentNodeKey? key)
    {
        if (key is { } wanted)
        {
            SelectByKey(wanted);
        }

        if (SelectedNodes.Count > 0)
        {
            FrameRequested?.Invoke();
        }
    }

    private void Push((string Ruleset, RulesetDocumentNodeKey? Selected) place)
    {
        _history.Add(place);
        while (_history.Count > HistoryDepth)
        {
            _history.RemoveAt(0);
        }

        OnPropertyChanged(nameof(CanGoBack));
    }
}
