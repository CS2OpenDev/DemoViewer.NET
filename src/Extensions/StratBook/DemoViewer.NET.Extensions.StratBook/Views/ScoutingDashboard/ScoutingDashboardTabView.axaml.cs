#region

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingDashboard;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingDashboard;

/// <summary>
///     The Scouting Report Dashboard view. Bindings only, apart from a rank box committing when it loses focus and
///     "Copy markdown", whose clipboard needs the visual tree (<c>TopLevel.Clipboard</c>).
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
