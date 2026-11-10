#region

using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Views.ScoutingReport;

/// <summary>
///     A <see cref="ScoutingColor" /> as a brush: the theme's CT, T, bad and accent tokens where the theme has
///     them, the categorical player set (the stage's own) where it does not.
/// </summary>
public sealed class ScoutingColorBrush : IValueConverter
{
    public static ScoutingColorBrush Instance { get; } = new();

    private static readonly IBrush[] _players =
    [
        new SolidColorBrush(Color.Parse("#3987E5")), new SolidColorBrush(Color.Parse("#D95926")),
        new SolidColorBrush(Color.Parse("#199E70")), new SolidColorBrush(Color.Parse("#C98500")),
        new SolidColorBrush(Color.Parse("#D55181"))
    ];

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ScoutingColor color ? For(color) : null;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>The brush for a colour.</summary>
    /// <param name="color">The colour.</param>
    public static IBrush? For(ScoutingColor color) => color switch
    {
        ScoutingColor.Ct => Token("Pb2dTeamCt"),
        ScoutingColor.T => Token("Pb2dTeamT"),
        ScoutingColor.Bad => Token("StatNegative"),
        ScoutingColor.Lurk => _players[4],
        >= ScoutingColor.Player1 and <= ScoutingColor.Player5 => _players[color - ScoutingColor.Player1],
        _ => Token("StatBarFill")
    };

    private static IBrush? Token(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out object? found)
            ? found as IBrush
            : null;
}
