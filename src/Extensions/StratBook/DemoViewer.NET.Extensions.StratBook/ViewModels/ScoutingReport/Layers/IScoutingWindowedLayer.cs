#region

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>One time window a layer offers: the key its stars file under and the pill's label.</summary>
/// <param name="Key">"0:15", "0:30", "contact"; the star key's window part.</param>
/// <param name="Label">"0:15", "first contact".</param>
public sealed record ScoutingWindow(string Key, string Label);

/// <summary>
///     A layer whose marks and rows run to a time window the user picks with pills in the inspector (the AWP trail).
///     The window is the layer's own state: the registry builds the layer's context with it, so the stars of each
///     window are kept apart, and rebuilds only this layer when a pill is picked.
/// </summary>
public interface IScoutingWindowedLayer : IScoutingLayer
{
    /// <summary>The windows offered, in pill order.</summary>
    IReadOnlyList<ScoutingWindow> Windows { get; }

    /// <summary>The window in force: one of <see cref="Windows" />' keys.</summary>
    string Window { get; set; }
}

/// <summary>One window pill in the inspector.</summary>
public sealed partial class ScoutingWindowPillViewModel : ObservableObject
{
    private readonly Action<string> _pick;

    /// <param name="window">The window.</param>
    /// <param name="isOn">The one in force.</param>
    /// <param name="pick">Picks it.</param>
    public ScoutingWindowPillViewModel(ScoutingWindow window, bool isOn, Action<string> pick)
    {
        ArgumentNullException.ThrowIfNull(window);
        Window = window;
        IsOn = isOn;
        _pick = pick;
    }

    public ScoutingWindow Window { get; }

    public string Label => Window.Label;

    public bool IsOn { get; }

    /// <summary>The pill's accessible name.</summary>
    public string AccessibleName => $"Trail to {Window.Label}";

    [RelayCommand]
    private void Pick() => _pick(Window.Key);
}
