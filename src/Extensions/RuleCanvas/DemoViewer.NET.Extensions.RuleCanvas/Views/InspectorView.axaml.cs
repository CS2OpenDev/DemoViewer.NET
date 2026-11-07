#region

using Avalonia.Controls;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>The Inspector view. Bindings, plus the one thing a binding cannot do: commit a text field when it loses focus.</summary>
public partial class InspectorView : UserControl
{
    public InspectorView() => InitializeComponent();

    // Enter is a key binding on the editor itself; focus loss is the other way a typed value lands.
    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: InspectorFieldViewModel field })
        {
            field.Commit();
        }
    }
}
