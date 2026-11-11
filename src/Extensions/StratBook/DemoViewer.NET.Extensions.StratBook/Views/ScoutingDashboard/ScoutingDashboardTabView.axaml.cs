#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingDashboard;

/// <summary>
///     The Scouting Report Dashboard view. Bindings only, apart from a rank box committing when it loses focus,
///     "Copy markdown", whose clipboard needs the visual tree (<c>TopLevel.Clipboard</c>), and a card's map picture
///     opening larger over the tab.
/// </summary>
public partial class ScoutingDashboardTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public ScoutingDashboardTabView() => InitializeComponent();

    private static void OnRankLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: RankFieldViewModel rank })
        {
            rank.Commit();
        }
    }

    private void OnExpandMapClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ScoutingDashboardCardViewModel { HasMapImage: true } card })
        {
            return;
        }

        // The overlay binds to the card, not a copy of its picture: a rebuild while it is open shows the new one.
        MapOverlayCard.DataContext = card;
        MapOverlay.IsVisible = true;
        MapOverlayClose.Focus();
        e.Handled = true;
    }

    private void OnMapOverlayCloseClick(object? sender, RoutedEventArgs e) => CloseMapOverlay();

    // A press on the dimmed backdrop closes; a press on the picture or its title does not.
    private void OnMapOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, MapOverlay))
        {
            CloseMapOverlay();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && MapOverlay.IsVisible)
        {
            CloseMapOverlay();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void CloseMapOverlay()
    {
        MapOverlay.IsVisible = false;
        MapOverlayCard.DataContext = null;
    }

    private async void OnCopyMarkdownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScoutingDashboardTabViewModel { Report.Editor: { HasTeam: true } editor })
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            editor.ReportCopyFailed();
            return;
        }

        try
        {
            await clipboard.SetTextAsync(editor.MarkdownText());
            editor.ReportCopied();
        }
        catch (Exception)
        {
            // Clipboard writes are permission or gesture gated on some hosts (the browser among them).
            editor.ReportCopyFailed();
        }
    }
}
