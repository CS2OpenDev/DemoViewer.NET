#region

using Avalonia;
using DemoViewer.NET.Visualization.Internal;

#endregion

namespace DemoViewer.NET.Visualization.Tests;

/// <summary>
///     Wheel zoom keeps what is under the cursor under the cursor. The graph is drawn at
///     <c>screen = pan + origin + logical * baseScale * zoom</c>, where <c>origin</c> is the letterbox
///     centring of a layout whose aspect differs from the control's, so the pan a zoom solves for has to
///     account for it.
/// </summary>
public class PanZoomHandlerTests
{
    private const double BaseScale = 0.5;

    private static Point Screen(PanZoomHandler handler, Point origin, Point logical) => new(
        handler.PanX + origin.X + logical.X * BaseScale * handler.Zoom,
        handler.PanY + origin.Y + logical.Y * BaseScale * handler.Zoom);

    [Test]
    [Arguments(0.0, 300.0)]
    [Arguments(120.0, 0.0)]
    [Arguments(0.0, 0.0)]
    public async Task ZoomAbout_KeepsTheLogicalPointUnderTheCursor_WhenTheLayoutIsLetterboxed(double originX,
        double originY)
    {
        PanZoomHandler handler = new();
        Point origin = new(originX, originY);
        Point node = new(640, 210);

        // In, in again from a panned and zoomed view, then out: every notch, not just the first.
        foreach (double factor in new[] { 1.15, 1.15, 1.15, 1 / 1.15 })
        {
            Point cursor = Screen(handler, origin, node);

            await Assert.That(handler.ZoomAbout(cursor, origin.X, origin.Y, factor)).IsTrue();

            Point after = Screen(handler, origin, node);
            await Assert.That(after.X).IsEqualTo(cursor.X).Within(1e-9);
            await Assert.That(after.Y).IsEqualTo(cursor.Y).Within(1e-9);
        }
    }

    [Test]
    public async Task ZoomAbout_AtTheZoomLimit_ChangesNothing()
    {
        PanZoomHandler handler = new();
        while (handler.ZoomAbout(new Point(10, 10), 0, 300, 1.15))
        {
        }

        double panX = handler.PanX;
        double zoom = handler.Zoom;

        await Assert.That(handler.ZoomAbout(new Point(400, 400), 0, 300, 1.15)).IsFalse();
        await Assert.That(handler.PanX).IsEqualTo(panX);
        await Assert.That(handler.Zoom).IsEqualTo(zoom);
    }
}
