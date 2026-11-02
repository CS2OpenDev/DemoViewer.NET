#region

using Avalonia;
using Avalonia.Media;

#endregion

namespace DemoViewer.NET.Theming;

/// <summary>One stop of a <see cref="ThemeGradient" />.</summary>
public readonly record struct ThemeGradientStop(double Offset, Color Color);

/// <summary>
///     A linear gradient token (today only <c>LibraryCardScrim</c>). Held as data so a theme file can set it the
///     way it sets a colour, and compared by value: two gradients are equal when their points and stops are.
/// </summary>
public sealed class ThemeGradient : IEquatable<ThemeGradient>
{
    public ThemeGradient(RelativePoint start, RelativePoint end, IReadOnlyList<ThemeGradientStop> stops)
    {
        Start = start;
        End = end;
        Stops = [.. stops];
    }

    public RelativePoint Start { get; }

    public RelativePoint End { get; }

    public IReadOnlyList<ThemeGradientStop> Stops { get; }

    /// <summary>The same gradient with stop <paramref name="index" /> recoloured.</summary>
    public ThemeGradient WithStopColor(int index, Color color)
    {
        ThemeGradientStop[] stops = [.. Stops];
        stops[index] = stops[index] with { Color = color };
        return new ThemeGradient(Start, End, stops);
    }

    public LinearGradientBrush ToBrush()
    {
        LinearGradientBrush brush = new() { StartPoint = Start, EndPoint = End };
        foreach (ThemeGradientStop stop in Stops)
        {
            brush.GradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        }

        return brush;
    }

    public static ThemeGradient FromBrush(ILinearGradientBrush brush) =>
        new(brush.StartPoint, brush.EndPoint, [.. brush.GradientStops.Select(s => new ThemeGradientStop(s.Offset, s.Color))]);

    public bool Equals(ThemeGradient? other) =>
        other is not null && Start == other.Start && End == other.End && Stops.SequenceEqual(other.Stops);

    public override bool Equals(object? obj) => Equals(obj as ThemeGradient);

    public override int GetHashCode() => HashCode.Combine(Start, End, Stops.Count);
}
