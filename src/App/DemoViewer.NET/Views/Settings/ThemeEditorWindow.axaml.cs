#region

using Avalonia.Controls;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.Views.Settings;

/// <summary>
///     Desktop host for the Theme Customization editor. Closing with unsaved edits is held until the user
///     answers the editor's prompt; the view-model then asks again through <c>CloseRequested</c>.
/// </summary>
public partial class ThemeEditorWindow : Window
{
    public ThemeEditorWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ThemeEditorViewModel vm && !vm.TryClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
