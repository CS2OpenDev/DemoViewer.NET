#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Transfer;

#endregion

namespace DemoViewer.NET.Views.Transfer;

/// <summary>
///     Desktop host for the export and import wizards. The user closing it mid-run is held until they answer
///     the wizard's cancel prompt; the view-model then closes it through <c>CloseRequested</c> once the run has
///     stopped. The app or the OS shutting down is never held.
/// </summary>
public partial class DataTransferWizardWindow : Window
{
    /// <summary>Initializes a new <see cref="DataTransferWizardWindow" /> instance.</summary>
    public DataTransferWizardWindow()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
            && DataContext is TransferWizardViewModel vm && !vm.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
