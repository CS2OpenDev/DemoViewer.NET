#region

using Avalonia;
using Avalonia.Media;
using Nodify.Avalonia.Connections;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Views;

/// <summary>
///     A connection whose label box is painted in <see cref="BaseConnection.TextBackground" />. The base
///     paints it in the stroke colour, which hides the text on the wire.
/// </summary>
public sealed class LabelledConnection : Connection
{
    protected override Type StyleKeyOverride => typeof(Connection);

    protected override void DrawTextBackground(DrawingContext drawingContext, Rect bounds)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (TextBackground is null)
        {
            return;
        }

        Thickness padding = TextPadding;
        double radius = TextCornerRadius;
        Rect rect = new(bounds.TopLeft - new Vector(padding.Left, padding.Top),
            new Size(bounds.Width + padding.Left + padding.Right, bounds.Height + padding.Top + padding.Bottom));
        drawingContext.DrawRectangle(TextBackground, new Pen(Stroke, 1), rect, radius, radius);
    }
}
