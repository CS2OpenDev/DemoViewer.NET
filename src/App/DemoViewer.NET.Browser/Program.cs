#region

using Avalonia;
using Avalonia.Browser;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.StratBook;

#endregion

namespace DemoViewer.NET.Browser;

internal static class Program
{
    /// <summary>Build avalonia app.</summary>
    [UsedImplicitly]
    public static AppBuilder BuildAvaloniaApp()
    {
        // The XAML previewer calls this without Main, so it declares the same packs; a no-op after Main.
        FeaturePacks.ConfigureIfUnset([new StratBookPack(), new RuleCanvasPack()]);
        return AppBuilder.Configure<App>();
    }

    private static Task Main()
    {
        // Same list as the Desktop head: the browser compile-links the extension it was built with.
        FeaturePacks.Configure([new StratBookPack(), new RuleCanvasPack()]);
        return BuildAvaloniaApp()
            .WithInterFont()
            .StartBrowserAppAsync("out");
    }
}
