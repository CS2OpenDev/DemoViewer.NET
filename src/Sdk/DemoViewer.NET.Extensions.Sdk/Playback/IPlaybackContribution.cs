#region

using DemoViewer.NET.Modules.Abstractions;

#endregion

namespace DemoViewer.NET.Extensions.Sdk.Playback;

/// <summary>
///     Added to every 2D Playback tab. <see cref="Attach" /> runs on the tab's first activation and whenever
///     the extension is switched back on; <see cref="Detach" /> must undo exactly what <see cref="Attach" /> added,
///     which disposing every handle the surface returned does.
/// </summary>
[PublicAPI]
public interface IPlaybackContribution
{
    /// <summary>Adds the contribution to one tab. <paramref name="context" /> is that tab's playback.</summary>
    void Attach(IPlaybackSurface surface, IModuleContext context);

    /// <summary>Removes it again.</summary>
    void Detach();
}
