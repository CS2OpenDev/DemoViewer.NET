#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     Attached behaviours for the editors on a node face. <c>CommitOnLostFocus</c> runs a command when the
///     control, or an editor inside it, loses focus, so a typed value is written the same way Enter writes
///     it without a handler per control type in the view.
/// </summary>
public static class FaceBehaviors
{
    public static readonly AttachedProperty<ICommand?> CommitOnLostFocusProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("CommitOnLostFocus", typeof(FaceBehaviors));

    static FaceBehaviors()
    {
        CommitOnLostFocusProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.LostFocus -= OnLostFocus;
            if (args.NewValue is ICommand)
            {
                control.LostFocus += OnLostFocus;
            }
        });
    }

    public static ICommand? GetCommitOnLostFocus(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(CommitOnLostFocusProperty);
    }

    public static void SetCommitOnLostFocus(Control control, ICommand? value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(CommitOnLostFocusProperty, value);
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && GetCommitOnLostFocus(control) is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }
}
