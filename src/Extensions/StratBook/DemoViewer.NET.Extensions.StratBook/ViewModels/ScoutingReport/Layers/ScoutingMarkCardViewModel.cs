#region

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>The card a clicked map mark pins over the map: what the mark is, every row behind it, and a close.</summary>
public sealed partial class ScoutingMarkCardViewModel : ObservableObject
{
    private Action? _close;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _layer = "";

    [ObservableProperty]
    private string _title = "";

    /// <summary>The mark's rows, drawn like the inspector's.</summary>
    public ObservableCollection<ScoutingInspectorRowViewModel> Rows { get; } = [];

    /// <summary>The close button's accessible name.</summary>
    public string CloseLabel => $"Close {Title}";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(CloseLabel));

    /// <summary>Shows a mark.</summary>
    /// <param name="layer">The layer's name.</param>
    /// <param name="title">The mark's title.</param>
    /// <param name="rows">Its rows.</param>
    /// <param name="close">What closing does.</param>
    internal void Show(string layer, string title, IReadOnlyList<ScoutingInspectorRowViewModel> rows, Action close)
    {
        Layer = ScoutingNames.Humanise(layer);
        Title = ScoutingNames.Humanise(title);
        Rows.Clear();
        foreach (ScoutingInspectorRowViewModel row in rows)
        {
            Rows.Add(row);
        }

        _close = close;
        IsOpen = true;
    }

    /// <summary>Hides the card.</summary>
    internal void Hide()
    {
        IsOpen = false;
        Rows.Clear();
        _close = null;
    }

    [RelayCommand]
    private void Close() => _close?.Invoke();
}
