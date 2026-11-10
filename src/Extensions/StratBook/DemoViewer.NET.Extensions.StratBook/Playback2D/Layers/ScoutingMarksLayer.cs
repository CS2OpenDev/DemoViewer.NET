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
/// </summary>
public sealed class ScoutingMarksLayer : ISceneLayer
{
    // The player and lurk colours: the categorical set the mock's legend uses, readable on both radar themes.
    private static readonly SKColor[] _players =
    [
        new(0x39, 0x87, 0xE5), new(0xD9, 0x59, 0x26), new(0x19, 0x9E, 0x70), new(0xC9, 0x85, 0x00),
        new(0xD5, 0x51, 0x81)
    ];

    private static readonly SKColor _bad = new(0xEC, 0x7A, 0x72);
    private static readonly SKColor _lurk = new(0xD5, 0x51, 0x81);
    private static readonly SKColor _neutral = new(0xE4, 0xE7, 0xF2);

    private readonly ScoutingMarksDocument _document;
    private readonly SKPaint _fill = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKFont _font = new() { Size = 11f, Embolden = true };
    private readonly SKPaint _stroke = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, IsAntialias = true };
    private readonly SKPathEffect _dash = SKPathEffect.CreateDash([3f, 3f], 0);

    /// <param name="id">The layer's id.</param>
    /// <param name="document">What to draw; not owned.</param>
    public ScoutingMarksLayer(string id, ScoutingMarksDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Id = id;
        _document = document;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public LayerSlot Slot => LayerSlot.Overlay;

    /// <inheritdoc />
    public int Order => 116;

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
            switch (mark.Shape)
            {
                case ScoutingMarkShape.Dot:
                    _fill.Color = color.WithAlpha(alpha);
                    canvas.DrawCircle(at, mark.Size, _fill);
                    break;
                case ScoutingMarkShape.Ring:
                    _stroke.PathEffect = null;
                    _stroke.Color = color.WithAlpha(alpha);
                    canvas.DrawCircle(at, mark.Size, _stroke);
                    break;
                case ScoutingMarkShape.Badge:
                    _fill.Color = color.WithAlpha(alpha);
                    canvas.DrawCircle(at, mark.Size, _fill);
                    DrawText(canvas, at, mark.Text, new SKColor(0x12, 0x14, 0x1C, alpha));
                    break;
                case ScoutingMarkShape.Cloud:
                    _fill.Color = color.WithAlpha((byte)(alpha * 0.18f));
                    canvas.DrawCircle(at, mark.Size, _fill);
                    _stroke.PathEffect = _dash;
                    _stroke.Color = color.WithAlpha(alpha);
                    canvas.DrawCircle(at, mark.Size, _stroke);
                    _stroke.PathEffect = null;
                    DrawText(canvas, at, mark.Text, color.WithAlpha(alpha));
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

    private void DrawText(SKCanvas canvas, SKPoint at, string? text, SKColor color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _fill.Color = color;
        float width = _font.MeasureText(text);
        canvas.DrawText(text, at.X - width / 2, at.Y + _font.Size * 0.35f, _font, _fill);
    }
}
