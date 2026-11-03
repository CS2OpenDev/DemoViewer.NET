namespace DemoViewer.NET.Playback2D.Pipeline.Rendering;

/// <summary>Which Skia backend an <see cref="IRenderSurfaceProvider" /> hands out surfaces from.</summary>
public enum RenderBackend
{
    /// <summary>Software raster. Always available, and the contract baseline goldens are authored on it.</summary>
    CpuRaster,

    /// <summary>A windowless desktop GL context.</summary>
    OpenGl,

    /// <summary>ANGLE over D3D11 via an EGL pbuffer, the Windows GPU path.</summary>
    Angle
}
