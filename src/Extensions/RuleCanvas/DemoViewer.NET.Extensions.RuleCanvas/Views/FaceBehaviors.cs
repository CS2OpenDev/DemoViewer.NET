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
///     it without a handler per control type in the view. <c>FocusWhenShown</c> moves focus into an editor
///     the moment it becomes visible, with its text selected, so the gesture that revealed it is the only
///     one needed before typing: Enter and commit-on-blur both need focus inside the editor.
/// </summary>
public static class FaceBehaviors
{
    public static readonly AttachedProperty<ICommand?> CommitOnLostFocusProperty =
        AvaloniaProperty.RegisterAttached<Control, ICommand?>("CommitOnLostFocus", typeof(FaceBehaviors));

    public static readonly AttachedProperty<bool> FocusWhenShownProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("FocusWhenShown", typeof(FaceBehaviors));

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
        FocusWhenShownProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.PropertyChanged -= OnShown;
            if (args.NewValue is true)
            {
                control.PropertyChanged += OnShown;
            }
        });
    }

    public static bool GetFocusWhenShown(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(FocusWhenShownProperty);
    }

    public static void SetFocusWhenShown(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(FocusWhenShownProperty, value);
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

    // IsEffectivelyVisible is already updated when this runs: the visual's own change handling precedes
    // the instance event.
    private static void OnShown(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.GetNewValue<bool>() && sender is Control control && control.Focus()
            && control is TextBox box)
        {
            box.SelectAll();
        }
    }
}
