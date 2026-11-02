#region

using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;

#endregion

namespace DemoViewer.NET.ViewModels.Settings;

/// <summary>Opens the Theme Customization window, from Settings or from the missing-colours notice.</summary>
public sealed class ThemeEditorLauncher(IWindowService windows, UserThemeStore store, ThemeEditorHost host)
{
    /// <summary>False on the browser head, which has no themes folder to save into.</summary>
    public static bool IsSupported => !OperatingSystem.IsBrowser() && UserThemeStore.IsAvailable;

    /// <summary>Opens the window on <paramref name="themeId" />, or re-targets the open one.</summary>
    public void Open(string? themeId, bool missingOnly = false) =>
        windows.ShowThemeEditor((id, missing) => new ThemeEditorViewModel(store, host, id, missing), themeId, missingOnly);
}
