#region

using CommunityToolkit.Mvvm.ComponentModel;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

/// <summary>
///     The seek items of a node's or a wire's menu: next, previous and first fire relative to the playhead.
///     Filled by the tab when the menu opens, since the answer depends on the run, the instance and the
///     playhead at that moment; an item that cannot run says why in its header.
/// </summary>
public sealed partial class CanvasSeekMenuViewModel : ObservableObject
{
    [ObservableProperty]
    private string _nextText = "Next fire";

    [ObservableProperty]
    private string _previousText = "Previous fire";

    [ObservableProperty]
    private string _firstText = "First fire";

    [ObservableProperty]
    private bool _canNext;

    [ObservableProperty]
    private bool _canPrevious;

    [ObservableProperty]
    private bool _canFirst;

    /// <summary>Sets all six at once, so the menu reads consistently.</summary>
    public void Set(string next, bool canNext, string previous, bool canPrevious, string first, bool canFirst)
    {
        NextText = next;
        CanNext = canNext;
        PreviousText = previous;
        CanPrevious = canPrevious;
        FirstText = first;
        CanFirst = canFirst;
    }
}
