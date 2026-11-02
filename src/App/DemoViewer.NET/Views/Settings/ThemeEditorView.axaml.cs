#region

using Avalonia.Controls;
using Avalonia.Input;
using DemoViewer.NET.ViewModels.Settings;

#endregion

namespace DemoViewer.NET.Views.Settings;

/// <summary>The Theme Customization editor, hosted by <see cref="ThemeEditorWindow" />.</summary>
public partial class ThemeEditorView : UserControl
{
    public ThemeEditorView()
    {
        InitializeComponent();
    }

    // A tap anywhere on a row (not just its swatch) puts it in the picker. The hex box keeps its own input.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ThemeTokenRowViewModel row } && DataContext is ThemeEditorViewModel vm)
        {
            vm.SelectedRow = row;
        }
    }
}
