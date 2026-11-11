#region

using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Themes.Fluent;

#endregion

[assembly: AvaloniaTestApplication(typeof(DemoViewer.NET.Extensions.RuleCanvas.Tests.TestAppBuilder))]

namespace DemoViewer.NET.Extensions.RuleCanvas.Tests;

/// <summary>
///     The headless application the gesture cases run in: Fluent for the face editors' templates, Skia so
///     a connection's stroke hit-tests, no app of the host's. The canvas's own tokens come with its view;
///     the host's brushes resolve to nothing, which a gesture does not need.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<CanvasTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}

/// <summary>
///     An application with the Fluent theme and the one host brush the geometry depends on: a wire draws its
///     label box only when it has a background, and the editor is anchored on that box.
/// </summary>
public sealed class CanvasTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources["PanelBg"] = new SolidColorBrush(Colors.White);
    }
}
