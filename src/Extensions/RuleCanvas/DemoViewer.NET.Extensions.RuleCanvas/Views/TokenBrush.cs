#region

using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     Paints a control from a token named at run time. A DynamicResource key is fixed in markup, so a node
///     whose colour depends on its kind binds the key through these attached properties instead; the brush is
///     resolved from the control's own resource chain, which includes the canvas theme, and follows the theme.
/// </summary>
public static class TokenBrush
{
    /// <summary>The token key painted as the control's background (a Border or a TemplatedControl).</summary>
    public static readonly AttachedProperty<string?> BackgroundProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Background", typeof(TokenBrush));

    /// <summary>The token key painted as the control's foreground (a TextBlock or a TemplatedControl).</summary>
    public static readonly AttachedProperty<string?> ForegroundProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Foreground", typeof(TokenBrush));

    /// <summary>The token key painted as the control's border (a Border or a TemplatedControl).</summary>
    public static readonly AttachedProperty<string?> BorderBrushProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("BorderBrush", typeof(TokenBrush));

    /// <summary>The token key painted as a shape's fill.</summary>
    public static readonly AttachedProperty<string?> FillProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Fill", typeof(TokenBrush));

    static TokenBrush()
    {
        BackgroundProperty.Changed.AddClassHandler<Control>((control, args) =>
            Apply(control, args.NewValue as string, Border.BackgroundProperty, Panel.BackgroundProperty, TemplatedControl.BackgroundProperty));
        ForegroundProperty.Changed.AddClassHandler<Control>((control, args) =>
            Apply(control, args.NewValue as string, TextBlock.ForegroundProperty, TemplatedControl.ForegroundProperty));
        BorderBrushProperty.Changed.AddClassHandler<Control>((control, args) =>
            Apply(control, args.NewValue as string, Border.BorderBrushProperty, TemplatedControl.BorderBrushProperty));
        FillProperty.Changed.AddClassHandler<Control>((control, args) =>
            Apply(control, args.NewValue as string, Shape.FillProperty));
    }

    public static string? GetBackground(Control control) => control.GetValue(BackgroundProperty);

    public static void SetBackground(Control control, string? value) => control.SetValue(BackgroundProperty, value);

    public static string? GetForeground(Control control) => control.GetValue(ForegroundProperty);

    public static void SetForeground(Control control, string? value) => control.SetValue(ForegroundProperty, value);

    public static string? GetBorderBrush(Control control) => control.GetValue(BorderBrushProperty);

    public static void SetBorderBrush(Control control, string? value) => control.SetValue(BorderBrushProperty, value);

    public static string? GetFill(Control control) => control.GetValue(FillProperty);

    public static void SetFill(Control control, string? value) => control.SetValue(FillProperty, value);

    // The first of the candidate properties the control's type declares is the one bound.
    private static void Apply(Control control, string? key, params AvaloniaProperty[] candidates)
    {
        foreach (AvaloniaProperty property in candidates)
        {
            if (!property.OwnerType.IsInstanceOfType(control))
            {
                continue;
            }

            if (string.IsNullOrEmpty(key))
            {
                control.ClearValue(property);
            }
            else
            {
                control.Bind(property, control.GetResourceObservable(key));
            }

            return;
        }
    }
}

/// <summary>
///     The token a node kind is painted in: an engine kind, a counted stat, a derived one, or a highlight.
///     Converts the kind string a node carries into the key the canvas theme defines.
/// </summary>
public sealed class RuleKindToken : IValueConverter
{
    public static RuleKindToken Instance { get; } = new();

    /// <summary>The theme key for <paramref name="kind" />, as the face names it.</summary>
    public static string KeyFor(string? kind) => kind switch
    {
        "event" => "RuleKindEvent",
        "enrich" or "entity" or "external" or "engine" => "RuleKindEnrich",
        "context" => "RuleKindContext",
        "flag" or "compute" => "RuleKindDerived",
        "highlight" => "RuleKindHighlight",
        _ => "RuleKindStat"
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => KeyFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
///     The token a diagnostics badge is painted in. Composition emits only errors today; a text that calls
///     itself a warning gets the caution colour so the day it does, the badge already reads right.
/// </summary>
public sealed class DiagnosticSeverityToken : IValueConverter
{
    public static DiagnosticSeverityToken Instance { get; } = new();

    public static string KeyFor(string? text) =>
        text is not null && text.Contains("warning", StringComparison.OrdinalIgnoreCase) ? "AccentCaution" : "AccentError";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => KeyFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
