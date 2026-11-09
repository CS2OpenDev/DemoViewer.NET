#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     A message that shows itself when it arrives and goes after <see cref="Lifetime" />, or on Escape, or
///     on its close button. Bound to the tab's <see cref="CanvasToast" />, whose sequence number makes a
///     repeated refusal arrive again. The timer lives here so the view model stays free of the dispatcher.
/// </summary>
public sealed class TransientToast : TemplatedControl
{
    public static readonly StyledProperty<CanvasToast?> ToastProperty =
        AvaloniaProperty.Register<TransientToast, CanvasToast?>(nameof(Toast));

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<TransientToast, string>(nameof(Message), "");

    public static readonly StyledProperty<string> ActionLabelProperty =
        AvaloniaProperty.Register<TransientToast, string>(nameof(ActionLabel), "");

    public static readonly StyledProperty<bool> HasActionProperty =
        AvaloniaProperty.Register<TransientToast, bool>(nameof(HasAction));

    public static readonly StyledProperty<TimeSpan> LifetimeProperty =
        AvaloniaProperty.Register<TransientToast, TimeSpan>(nameof(Lifetime), TimeSpan.FromSeconds(5));

    private DispatcherTimer? _timer;

    static TransientToast()
    {
        ToastProperty.Changed.AddClassHandler<TransientToast>((toast, _) => toast.Arrive());
        IsVisibleProperty.OverrideDefaultValue<TransientToast>(false);
    }

    public TransientToast()
    {
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss();
                e.Handled = true;
            }
        };
    }

    /// <summary>The toast to show; a new value with text shows, null hides.</summary>
    public CanvasToast? Toast
    {
        get => GetValue(ToastProperty);
        set => SetValue(ToastProperty, value);
    }

    /// <summary>The text shown, for the template.</summary>
    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>The action button's text, for the template; empty when the toast offers none.</summary>
    public string ActionLabel
    {
        get => GetValue(ActionLabelProperty);
        set => SetValue(ActionLabelProperty, value);
    }

    public bool HasAction
    {
        get => GetValue(HasActionProperty);
        set => SetValue(HasActionProperty, value);
    }

    /// <summary>Runs the toast's action and hides it; nothing when it offers none.</summary>
    public void Act()
    {
        Action? action = Toast?.Action;
        Dismiss();
        action?.Invoke();
    }

    /// <summary>How long a message stays. Zero or less keeps it until dismissed.</summary>
    public TimeSpan Lifetime
    {
        get => GetValue(LifetimeProperty);
        set => SetValue(LifetimeProperty, value);
    }

    /// <summary>Hides the message now.</summary>
    public void Dismiss()
    {
        _timer?.Stop();
        _timer = null;
        IsVisible = false;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_Close") is { } close)
        {
            close.Click += (_, _) => Dismiss();
        }

        if (e.NameScope.Find<Button>("PART_Action") is { } act)
        {
            act.Click += (_, _) => Act();
        }
    }

    private void Arrive()
    {
        _timer?.Stop();
        _timer = null;
        if (Toast is not { Message.Length: > 0 } toast)
        {
            IsVisible = false;
            return;
        }

        Message = toast.Message;
        ActionLabel = toast.ActionLabel ?? "";
        HasAction = toast.HasAction;
        IsVisible = true;
        if (Lifetime > TimeSpan.Zero)
        {
            _timer = new DispatcherTimer(Lifetime, DispatcherPriority.Background, (_, _) => Dismiss());
            _timer.Start();
        }
    }
}
