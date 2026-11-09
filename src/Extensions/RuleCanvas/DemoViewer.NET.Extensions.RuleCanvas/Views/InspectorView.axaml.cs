#region

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The Inspector view. Bindings, plus what a binding cannot do: commit a text field when it loses focus,
///     and read Enter and Escape in the wire editor.
/// </summary>
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

    // Enter writes once any list inside is closed; Escape puts the file's condition back.
    private void OnWireKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not InspectorViewModel { Wire: { } editor } || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                editor.Cancel();
                e.Handled = true;
                break;
            case Key.Enter when e.Source is not AutoCompleteBox { IsDropDownOpen: true } and not ComboBox { IsDropDownOpen: true }:
                editor.Commit();
                e.Handled = true;
                break;
        }
    }
}
