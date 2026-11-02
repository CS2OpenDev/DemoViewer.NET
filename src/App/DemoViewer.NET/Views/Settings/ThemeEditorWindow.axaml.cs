#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.Views.Settings;

/// <summary>
///     Desktop host for the Theme Customization editor. The user closing it with unsaved edits is held until
///     they answer the editor's prompt; the view-model then asks again through <c>CloseRequested</c>. The main
///     window closing, or the app or the OS shutting down, is never held: the edits are dropped, and the
///     window service's <c>Closed</c> handler ends the preview as usual.
/// </summary>
public partial class ThemeEditorWindow : Window
{
    public ThemeEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined
            && DataContext is ThemeEditorViewModel vm && !vm.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
