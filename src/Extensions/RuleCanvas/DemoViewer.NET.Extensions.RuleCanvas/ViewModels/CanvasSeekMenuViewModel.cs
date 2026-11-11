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
    /// <summary>The key that seeks to the next fire of the selection, as the header hints it.</summary>
    public const string NextKey = "]";

    /// <summary>The key that seeks to the previous fire of the selection, as the header hints it.</summary>
    public const string PreviousKey = "[";

    [ObservableProperty]
    private string _nextText = Model.Live.LiveValuesText.WithKeyHint("Next fire", NextKey);

    [ObservableProperty]
    private string _previousText = Model.Live.LiveValuesText.WithKeyHint("Previous fire", PreviousKey);

    [ObservableProperty]
    private string _firstText = "First fire";

    [ObservableProperty]
    private bool _canNext;

    [ObservableProperty]
    private bool _canPrevious;

    [ObservableProperty]
    private bool _canFirst;

    /// <summary>
    ///     Sets all six at once, so the menu reads consistently. The next and previous headers get their key
    ///     hints here; the first fire has no key.
    /// </summary>
    public void Set(string next, bool canNext, string previous, bool canPrevious, string first, bool canFirst)
    {
        NextText = Model.Live.LiveValuesText.WithKeyHint(next, NextKey);
        CanNext = canNext;
        PreviousText = Model.Live.LiveValuesText.WithKeyHint(previous, PreviousKey);
        CanPrevious = canPrevious;
        FirstText = first;
        CanFirst = canFirst;
    }
}
