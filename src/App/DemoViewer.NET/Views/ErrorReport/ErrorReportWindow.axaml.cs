#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.ErrorReport;

#endregion

namespace DemoViewer.NET.Views.ErrorReport;

/// <summary>The "Report Analysis Errors" window: a frame around <see cref="ErrorReportView" />.</summary>
public partial class ErrorReportWindow : Window
{
    /// <summary>Initializes a new <see cref="ErrorReportWindow" /> instance.</summary>
    public ErrorReportWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     The user closing the window with unsaved notes is held until they answer the discard prompt; the
    ///     view-model then closes it through <c>CloseRequested</c>. The app or the OS shutting down is never held.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
            && DataContext is ErrorReportViewModel vm && !vm.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
