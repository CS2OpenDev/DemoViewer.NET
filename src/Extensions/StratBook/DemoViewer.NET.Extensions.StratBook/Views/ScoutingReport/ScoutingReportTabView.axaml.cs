#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;

/// <summary>
///     The Scouting Report tab view. Bindings only, apart from the arrow keys on the map tabs and the side switch,
///     which move the focus with the selection, and "Copy markdown": the clipboard needs the visual tree
///     (<c>TopLevel.Clipboard</c>), so the handler lives here and the text comes from the VM.
/// </summary>
public partial class ScoutingReportTabView : UserControl
{
    // The finding being edited when its editor had focus. A row that scrolls out is recycled, which takes
    // the focus with it; the editor that shows the row again takes it back.
    private ScoutingReportFindingViewModel? _editing;

    /// <summary>Builds the view.</summary>
    public ScoutingReportTabView() => InitializeComponent();

    // A rank box that loses focus commits what was typed, the same as Enter.
    private static void OnRankLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: RankFieldViewModel rank })
        {
            rank.Commit();
        }
    }

    private void OnFindingEditGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ScoutingReportFindingViewModel row })
        {
            _editing = row;
        }
    }

    // Only a focus the user moved elsewhere ends the claim; a recycled editor has a new row or none.
    private void OnFindingEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || _editing is not { } row)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(box.DataContext, row) && box.IsEffectivelyVisible
                && TopLevel.GetTopLevel(box) is not null
                || !row.IsEditing)
            {
                _editing = null;
            }
        }, DispatcherPriority.Background);
    }

    private void OnFindingEditRebound(object? sender, EventArgs e) => Reclaim(sender as TextBox);

    private void OnFindingEditAttached(object? sender, VisualTreeAttachmentEventArgs e) => Reclaim(sender as TextBox);

    private void Reclaim(TextBox? box)
    {
        if (box is { DataContext: ScoutingReportFindingViewModel { IsEditing: true } row } && ReferenceEquals(row, _editing)
            && !box.IsFocused)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(box.DataContext, row) && row.IsEditing)
                {
                    box.Focus();
                }
            }, DispatcherPriority.Loaded);
        }
    }

    // The map tabs are one tab stop (the open map's tab); Left and Right open the previous or next map and keep
    // the focus on its tab, Home and End the first and last.
    private void OnMapTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScoutingReportTabViewModel { SelectedMap: { } current } vm || vm.Maps.Count == 0
            || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        int at = vm.Maps.IndexOf(current);
        int next = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, at - 1),
            Key.Right or Key.Down => Math.Min(vm.Maps.Count - 1, at + 1),
            Key.Home => 0,
            Key.End => vm.Maps.Count - 1,
            _ => -1
        };
        if (next < 0)
        {
            return;
        }

        e.Handled = true;
        if (next == at)
        {
            return;
        }

        MapPoolRowViewModel map = vm.Maps[next];
        vm.SelectMapCommand.Execute(map);
        if (sender is ItemsControl tabs)
        {
            Dispatcher.UIThread.Post(() => (tabs.ContainerFromItem(map) as ContentPresenter)?.Child?.Focus(
                NavigationMethod.Directional), DispatcherPriority.Loaded);
        }
    }

    // The side switch is one tab stop; the arrow keys flip it and move the focus with it.
    private void OnSideKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScoutingReportTabViewModel vm || e.KeyModifiers != KeyModifiers.None
            || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return;
        }

        e.Handled = true;
        bool toCt = e.Key is Key.Left or Key.Up;
        if (toCt == vm.IsCt)
        {
            return;
        }

        vm.Side = toCt ? ScoutingSide.Ct : ScoutingSide.T;
        if (sender is Panel side)
        {
            side.Children[toCt ? 0 : 1].Focus(NavigationMethod.Directional);
        }
    }

    private async void OnCopyMarkdownClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ScoutingReportTabViewModel { Editor: { HasTeam: true } editor })
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
