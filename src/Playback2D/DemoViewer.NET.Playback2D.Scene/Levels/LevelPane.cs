#region

using System.Diagnostics.CodeAnalysis;
using DemoViewer.NET.Playback2D.Cameras;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Playback2D.Levels;

/// <summary>
///     One level's on-screen band: which level it shows, where on the host it sits, which camera rig
///     drives it, and the camera itself.
///     <para>
///         Lifetime is owned by <see cref="PaneSet" /> and identity is <see cref="MapLevelId" />, not
///         array position: insert a lower floor and the upper floor's pane keeps its pan, zoom and
///         manual override.
///     </para>
/// </summary>
[PublicAPI]
public sealed class LevelPane
{
    /// <summary>
    ///     The camera that renders this pane. A public <b>field</b> by contract: <c>PanZoomTool</c>
    ///     mutates it in place through a <c>ref</c>, and a property returning a copy of this struct would
    ///     make panning silently do nothing.
    /// </summary>
    [SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
        Justification = "Deliberate: SliceCamera is a mutable struct that pointer tools update in place. " +
            "A property would hand out a copy and break pan/zoom.")]
    public SliceCamera Camera;

    private ViewportTransform _epochTransform;

    /// <summary>Creates a pane for a level with a starting camera and rig.</summary>
    /// <param name="level">The level this pane shows.</param>
    /// <param name="camera">The initial camera.</param>
    /// <param name="rig">The rig driving it.</param>
    public LevelPane(MapLevel level, SliceCamera camera, ICameraRig rig)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(rig);
        Level = level;
        Camera = camera;
        Rig = rig;
        _epochTransform = camera.Current;
    }

    /// <summary>The level being shown. Re-pointed (never mutated) by a <see cref="MapSpace" /> rebuild.</summary>
    public MapLevel Level { get; set; }

    /// <summary>The rig computing this pane's camera target.</summary>
    public ICameraRig Rig { get; set; }

    /// <summary>
    ///     Host-space rectangle this pane occupies. Assigning a different rectangle bumps
    ///     <see cref="CameraEpoch" />, because every cached pane-local picture is invalid at a new size.
    /// </summary>
    public SKRect ViewportRect
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            CameraEpoch++;
        }
    }

    /// <summary>Position from the bottom: 0 is the lowest level. Assigned by <see cref="PaneSet" />.</summary>
    public int LevelIndex { get; internal set; }

    /// <summary>
    ///     Bumped whenever the camera has moved far enough that a pane-local cached picture would be
    ///     visibly wrong, or the pane resized. <c>LayerPictureCache</c> keys <c>PerCamera</c> entries on
    ///     it. A per-pixel comparison would re-record every frame; no comparison at all would freeze the
    ///     radar mid-pan.
    /// </summary>
    public int CameraEpoch { get; internal set; }

    /// <summary>
    ///     The space this pane's level belongs to, for rigs that need floor assignment
    ///     (<c>FitAliveRig</c>, <c>FollowPlayerRig</c>). Assigned by <see cref="PaneSet" />.
    /// </summary>
    public MapSpace? Space { get; internal set; }

    /// <summary>
    ///     How many panes are currently arranged. The pre-v2 rigs only filter by floor when there is more
    ///     than one band (<c>_cameras.Length > 1</c>): a single-band render frames every player regardless
    ///     of Z. Parity behaviour, not an optimisation.
    /// </summary>
    public int PaneCount { get; internal set; } = 1;

    /// <summary>Convenience: this pane's level id.</summary>
    public MapLevelId LevelId => Level.Id;

    /// <summary>
    ///     Re-evaluates <see cref="CameraEpoch" /> against the camera's current transform, bumping it
    ///     when the move is material. Called by <see cref="PaneSet" /> after the camera advance and after
    ///     a pointer gesture. The camera is a public field, so the pane cannot observe writes to it.
    /// </summary>
    /// <returns>True when the epoch was bumped.</returns>
    public bool SyncCameraEpoch()
    {
        if (MaxCornerShift(_epochTransform, Camera.Current) < EpochShiftPixels)
        {
            return false;
        }

        _epochTransform = Camera.Current;
        CameraEpoch++;
        return true;
    }

    // Not SliceCamera.IsSettledAt: that is the animation's stop rule, and its 0.5% scale tolerance
    // lets a cached radar sit pixels off the live markers at the pane edge, at rest, since the camera
    // snaps to its target inside the tolerance and nothing records again.
    private const double EpochShiftPixels = 0.5;

    // How far, in screen pixels, the pane's farthest corner moves between two transforms. The map is
    // affine, so the largest shift over the pane is at a corner. A resize or a NaN reads as infinite.
    private static double MaxCornerShift(ViewportTransform from, ViewportTransform to)
    {
        if (!from.ViewWidth.Equals(to.ViewWidth) || !from.ViewHeight.Equals(to.ViewHeight))
        {
            return double.PositiveInfinity;
        }

        // Called every frame per pane, so the four corners are walked without an array.
        double max = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            double sx = (corner & 1) * to.ViewWidth;
            double sy = (corner >> 1) * to.ViewHeight;
            (double wx, double wy) = to.ScreenToWorld(sx, sy);
            (double px, double py) = from.WorldToScreen(wx, wy);
            double shift = Math.Max(Math.Abs(px - sx), Math.Abs(py - sy));
            if (!(shift <= max))
            {
                max = double.IsFinite(shift) ? shift : double.PositiveInfinity;
            }
        }

        return max;
    }

    /// <summary>An immutable copy of everything the render thread is allowed to see about this pane.</summary>
    public LevelPaneSnapshot Snapshot() =>
        new(Level.Id, LevelIndex, Level, Camera.Current, ViewportRect, CameraEpoch);
}

/// <summary>
///     The render thread's view of one pane: value copies only, captured on the UI thread at submission.
///     The mutable <see cref="LevelPane" /> never crosses the thread boundary.
/// </summary>
/// <param name="LevelId">The pane's level identity.</param>
/// <param name="LevelIndex">Position from the bottom; 0 is lowest.</param>
/// <param name="Level">
///     The level itself. A reference, but every mutable member on it (<c>Radar</c>) is rebound only
///     under the render gate.
/// </param>
/// <param name="Transform">World → pane-local screen for this frame.</param>
/// <param name="ViewportRect">Host-space rectangle of the band.</param>
/// <param name="CameraEpoch">Cache key component for <c>PerCamera</c> pictures.</param>
[PublicAPI]
public readonly record struct LevelPaneSnapshot(
    MapLevelId LevelId,
    int LevelIndex,
    MapLevel Level,
    ViewportTransform Transform,
    SKRect ViewportRect,
    int CameraEpoch);
