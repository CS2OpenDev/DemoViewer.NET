#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using DemoViewer.NET.Extensions.StratBook.ViewModels.UtilityBook;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.UtilityBook;

/// <summary>
///     The Utility Book view. Bindings only, except the clipboard: it needs the visual tree
///     (<c>TopLevel.GetTopLevel</c>), so the view hands the view model a writer while it is attached.
/// </summary>
public partial class UtilityBookTabView : UserControl
{
    /// <summary>Builds the view.</summary>
    public UtilityBookTabView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wire();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree())
        {
            Wire();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unwire();
    }

    private UtilityBookTabViewModel? _wired;

    // Only while attached: the host keeps the view model when it drops the view, and a handler or a clipboard
    // writer left on it would keep this view alive.
    private void Wire()
    {
        Unwire();
        _wired = DataContext as UtilityBookTabViewModel;
        if (_wired is not null)
        {
            _wired.PropertyChanged += OnVmPropertyChanged;
            PlaceCard();
            _wired.Clipboard = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                }
            };
        }
    }

    private void Unwire()
    {
        if (_wired is not null)
        {
            _wired.PropertyChanged -= OnVmPropertyChanged;
            _wired.Clipboard = null;
            _wired = null;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UtilityBookTabViewModel.CardOnLeft))
        {
            PlaceCard();
        }
    }

    private void PlaceCard()
    {
        if (this.FindControl<Border>("Card") is { } card && _wired is not null)
        {
            card.HorizontalAlignment = _wired.CardOnLeft
                ? Avalonia.Layout.HorizontalAlignment.Left
                : Avalonia.Layout.HorizontalAlignment.Right;
        }
    }
}
