#region

using Avalonia;
using Avalonia.Media;
using Nodify.Avalonia.Connections;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     A connection whose label box is painted in <see cref="BaseConnection.TextBackground" />. The base
///     paints it in the stroke colour, which hides the text on the wire. The label's box is kept from the
///     last draw, so the condition editor can be anchored where the label is.
/// </summary>
public sealed class LabelledConnection : Connection
{
    protected override Type StyleKeyOverride => typeof(Connection);

    /// <summary>The label's box with its padding, in this connection's coordinates, from the last draw; null before one.</summary>
    public Rect? LabelBounds { get; private set; }

    /// <summary>Where an editor on this wire is anchored: the label's centre, or the wire's midpoint before a draw or without a label.</summary>
    public Point LabelAnchor => LabelBounds is { } bounds
        ? bounds.Center
        : new Point((Source.X + Target.X) / 2, (Source.Y + Target.Y) / 2);

    protected override void DrawTextBackground(DrawingContext drawingContext, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        Thickness padding = TextPadding;
        double radius = TextCornerRadius;
        Rect rect = new(bounds.TopLeft - new Vector(padding.Left, padding.Top),
            new Size(bounds.Width + padding.Left + padding.Right, bounds.Height + padding.Top + padding.Bottom));
        LabelBounds = rect;
        if (TextBackground is null)
        {
            return;
        }

        drawingContext.DrawRectangle(TextBackground, new Pen(Stroke, 1), rect, radius, radius);
    }
}
