#region

using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;
using DemoViewer.NET.Playback2D;
using DemoViewer.NET.Playback2D.Compositing;
using SkiaSharp;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Playback2D.Layers;

/// <summary>
///     One Scouting Report layer on the map stage: its <see cref="ScoutingMapMark" />s, each on the pane of its own
///     floor, drawn at the document's fade (30 % while another layer is inspected). Dots and rings are setups,
///     deaths and plant spots; badges are numbered route ends and lettered strats; clouds are grenade landings.
///     The CT and T colours come from the scene palette, so the stage follows the theme like the 2D viewer.
///     <para>
///         Every mark sits on a dark halo <see cref="HaloWidth" /> wide, at the mark's own opacity: on the light
///         greys of some radars (de_nuke's floor) the bad red measured about 1.2:1 against the art, and the halo
///         is what separates the mark from the floor there. On dark art it only thickens the edge.
///     </para>
/// </summary>
public sealed class ScoutingMarksLayer : ISceneLayer
{
    // The player and lurk colours: the categorical set the mock's legend uses, readable on both radar themes.
    private static readonly SKColor[] _players =
    [
        new(0x39, 0x87, 0xE5), new(0xD9, 0x59, 0x26), new(0x19, 0x9E, 0x70), new(0xC9, 0x85, 0x00),
        new(0xD5, 0x51, 0x81)
    ];

    /// <summary>How far the dark halo reaches past a mark's edge, in screen pixels.</summary>
    public const float HaloWidth = 1.75f;

    // The ring stroke; the halo stroke is this plus a halo each side.
    private const float RingWidth = 2f;

    // The halo's share of the mark's own alpha: dark enough to cut out the mark, light enough not to read as a
    // second, black mark around a faint one.
    private const float HaloShare = 0.85f;

    private static readonly SKColor _halo = new(0x0A, 0x0C, 0x12);
    private static readonly SKColor _bad = new(0xEC, 0x7A, 0x72);
    private static readonly SKColor _lurk = new(0xD5, 0x51, 0x81);
    private static readonly SKColor _neutral = new(0xE4, 0xE7, 0xF2);

    private readonly ScoutingMarksDocument _document;
    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKFont _font = new() { Size = 11f, Embolden = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, StrokeWidth = RingWidth, IsAntialias = true };
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([3f, 3f], 0);

    /// <param name="id">The layer's id.</param>
    /// <param name="document">What to draw; not owned.</param>
    /// <param name="order">Its place in the overlay; a clicked mark's detail draws above the layers.</param>
    public ScoutingMarksLayer(string id, ScoutingMarksDocument document, int order = 116)
    {
        ArgumentNullException.ThrowIfNull(document);
        Id = id;
        _document = document;
        Order = order;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order { get; }

    /// <inheritdoc />
    public LayerCacheHint Cache => LayerCacheHint.PerCamera;

    /// <inheritdoc />
    public bool IsEnabled { get; set; } = true;

    /// <inheritdoc />
    public int ContentVersion => _document.Version;

    /// <inheritdoc />
    public bool Advance(in SceneTime time, Scene2DFrame frame) => false;

    /// <inheritdoc />
    public void Render(SKCanvas canvas, SceneRenderContext ctx)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (!_document.Visible)
        {
            return;
        }

        float fade = _document.Fade;
        // Links first, so the dots at their ends sit on top of them.
        foreach (ScoutingMapMark mark in _document.Marks.Where(m => m.Shape == ScoutingMarkShape.Link))
        {
            if (mark.To is { } to && ctx.BelongsHere(mark.Z))
            {
                (double x0, double y0) = ctx.Transform.WorldToScreen(mark.X, mark.Y);
                (double x1, double y1) = ctx.Transform.WorldToScreen(to.X, to.Y);
                byte a = (byte)Math.Clamp(255 * mark.Opacity * fade, 0, 255);
                Line(canvas, new SKPoint((float)x0, (float)y0), new SKPoint((float)x1, (float)y1),
                    ColorOf(mark.Color, in ctx).WithAlpha(a), _halo.WithAlpha((byte)(a * HaloShare)));
            }
        }

        foreach (ScoutingMapMark mark in _document.Marks)
        {
            if (!ctx.BelongsHere(mark.Z))
            {
                continue;
            }

            (double x, double y) = ctx.Transform.WorldToScreen(mark.X, mark.Y);
            SKPoint at = new((float)x, (float)y);
            SKColor color = ColorOf(mark.Color, in ctx);
            byte alpha = (byte)Math.Clamp(255 * mark.Opacity * fade, 0, 255);
            SKColor halo = _halo.WithAlpha((byte)(alpha * HaloShare));
            switch (mark.Shape)
            {
                case ScoutingMarkShape.Dot:
                case ScoutingMarkShape.Badge:
                    _fill.Color = halo;
                    canvas.DrawCircle(at, mark.Size + HaloWidth, _fill);
                    _fill.Color = color.WithAlpha(alpha);
                    canvas.DrawCircle(at, mark.Size, _fill);
                    if (mark.Shape == ScoutingMarkShape.Badge)
                    {
                        DrawText(canvas, at, mark.Text, new SKColor(0x12, 0x14, 0x1C, alpha), null);
                    }

                    break;
                case ScoutingMarkShape.Ring:
                    Ring(canvas, at, mark.Size, color.WithAlpha(alpha), halo, null);
                    break;
                case ScoutingMarkShape.Cloud:
                    _fill.Color = color.WithAlpha((byte)(alpha * 0.18f));
                    canvas.DrawCircle(at, mark.Size, _fill);
                    Ring(canvas, at, mark.Size, color.WithAlpha(alpha), halo, _dash);
                    DrawText(canvas, at, mark.Text, color.WithAlpha(alpha), halo);
                    break;
                case ScoutingMarkShape.Link:
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _font.Dispose();
        _dash.Dispose();
    }

    /// <summary>The colour a mark draws in, from the scene palette where the theme has one.</summary>
    /// <param name="color">The mark's colour.</param>
    /// <param name="ctx">The pane's context.</param>
    public static SKColor ColorOf(ScoutingColor color, in SceneRenderContext ctx) => color switch
    {
        ScoutingColor.Ct => ctx.Palette.TeamCt,
        ScoutingColor.T => ctx.Palette.TeamT,
        ScoutingColor.Bad => _bad,
        ScoutingColor.Lurk => _lurk,
        >= ScoutingColor.Player1 and <= ScoutingColor.Player5 => _players[color - ScoutingColor.Player1],
        _ => _neutral
    };

    // A ring over a solid halo ring: the halo stays solid under a dashed (cloud) ring so the dashes read on any art.
    private void Ring(SKCanvas canvas, SKPoint at, float radius, SKColor color, SKColor halo, SKPathEffect? dash)
    {
        _stroke.PathEffect = null;
        _stroke.StrokeWidth = RingWidth + 2 * HaloWidth;
        _stroke.Color = halo;
        canvas.DrawCircle(at, radius, _stroke);
        _stroke.StrokeWidth = RingWidth;
        _stroke.PathEffect = dash;
        _stroke.Color = color;
        canvas.DrawCircle(at, radius, _stroke);
        _stroke.PathEffect = null;
    }

    // A dashed line over a solid halo line.
    private void Line(SKCanvas canvas, SKPoint from, SKPoint to, SKColor color, SKColor halo)
    {
        _stroke.PathEffect = null;
        _stroke.StrokeWidth = 1.5f + 2 * HaloWidth;
        _stroke.Color = halo;
        canvas.DrawLine(from, to, _stroke);
        _stroke.StrokeWidth = 1.5f;
        _stroke.PathEffect = _dash;
        _stroke.Color = color;
        canvas.DrawLine(from, to, _stroke);
        _stroke.PathEffect = null;
        _stroke.StrokeWidth = RingWidth;
    }

    private void DrawText(SKCanvas canvas, SKPoint at, string? text, SKColor color, SKColor? halo)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        float width = _font.MeasureText(text);
        float x = at.X - width / 2;
        float y = at.Y + _font.Size * 0.35f;
        if (halo is { } h)
        {
            _stroke.StrokeWidth = 2 * HaloWidth;
            _stroke.Color = h;
            canvas.DrawText(text, x, y, _font, _stroke);
            _stroke.StrokeWidth = RingWidth;
        }

        _fill.Color = color;
        canvas.DrawText(text, x, y, _font, _fill);
    }
}
