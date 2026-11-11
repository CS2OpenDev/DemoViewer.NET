namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     What one layer draws on the stage right now: its marks, whether it shows, and the fade it draws at (1, or
///     0.3 while another layer is inspected). The scene layer reads it on the UI thread; every change bumps
///     <see cref="Version" /> and raises <see cref="Changed" /> so the map redraws.
/// </summary>
public sealed class ScoutingMarksDocument
{
    /// <summary>The fade of a layer that is drawn but not the one inspected.</summary>
    public const float FadedOpacity = 0.3f;

    public IReadOnlyList<ScoutingMapMark> Marks { get; private set; } = [];

    public bool Visible { get; private set; }

    public float Fade { get; private set; } = 1f;

    public int Version { get; private set; }

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    /// <summary>Replaces what is drawn; raises <see cref="Changed" /> only when something differs.</summary>
    /// <param name="marks">The marks.</param>
    /// <param name="visible">Whether the layer draws at all.</param>
    /// <param name="fade">Its opacity multiplier.</param>
    public void Set(IReadOnlyList<ScoutingMapMark> marks, bool visible, float fade)
    {
        ArgumentNullException.ThrowIfNull(marks);
        if (ReferenceEquals(marks, Marks) && visible == Visible && Math.Abs(fade - Fade) < 0.001f)
        {
            return;
        }

        Marks = marks;
        Visible = visible;
        Fade = fade;
        Version++;
        Changed?.Invoke();
    }
}
