#region

using System.Globalization;
using Avalonia.Data.Converters;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     The name a compact scaffold node shows: the engine's name without the <c>enrich.</c> prefix, which the
///     kind pill already says. The full name stays on the tooltip and in the layout key.
/// </summary>
public sealed class ScaffoldName : IValueConverter
{
    public static ScaffoldName Instance { get; } = new();

    public static string Of(string? id) =>
        id is not null && id.StartsWith("enrich.", StringComparison.Ordinal) ? id["enrich.".Length..] : id ?? "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Of(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Whether a gallery item of this kind can be placed without a node under it: everything but a gate.</summary>
public sealed class GalleryItemPlaceable : IValueConverter
{
    public static GalleryItemPlaceable Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not Model.GalleryItemKind.Gate;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
///     A wire's label at the viewport's zoom: the text as given down to <see cref="MinZoom" />, and nothing
///     below it, where a 10px label would render under 7px and only clutter the wire. A multi-value converter
///     over the wire's text and the editor's <c>ViewportZoom</c>.
/// </summary>
public sealed class WireLabelAtZoom : IMultiValueConverter
{
    /// <summary>The zoom below which labels hide.</summary>
    public const double MinZoom = 0.7;

    public static WireLabelAtZoom Instance { get; } = new();

    public static string Of(string? text, double zoom) => zoom < MinZoom ? "" : text ?? "";

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? text = values.Count > 0 ? values[0] as string : null;
        double zoom = values.Count > 1 && values[1] is double z ? z : 1;
        return Of(text, zoom);
    }
}
