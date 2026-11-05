#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Theming;
using DemoViewer.NET.ViewModels.Settings;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     Theme Customization captures: the editor on a built-in, on a user theme with missing colours (filtered),
///     with a prompt up, the Settings THEME section, and the shell's missing-colours notice. Each one gets a
///     scratch config root holding a partial user theme and a broken file, so the missing and load-problem
///     states have something to show.
/// </summary>
public static partial class Variants
{
    internal static readonly IReadOnlyDictionary<string, Func<Control>> ThemeCaptures =
        new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
        {
            ["theme-editor"] = () => ThemeEditor("egirl", false, null),
            ["theme-editor-missing"] = () => ThemeEditor("user-pastel", true, null),
            ["theme-editor-edited"] = () => ThemeEditor("user-pastel", false, vm =>
            {
                vm.SelectedRow!.Hex = "#FF6F91";
                vm.ApplyToApp = false;
            }),
            ["theme-editor-unsaved"] = () => ThemeEditor("user-pastel", false, vm =>
            {
                vm.SelectedRow!.Hex = "#FF6F91";
                vm.CancelCommand.Execute(null);
            }),
            ["theme-editor-customize"] = () => ThemeEditor("egirl", false, vm => vm.CopyCommand.Execute(null)),
            ["theme-settings"] = ThemeSettings,
            ["theme-warning"] = ThemeWarning
        };

    private static (ThemeRegistry Registry, SettingsService Settings, IOptionsMonitor<AppSettings> Monitor)
        ThemeScratch(string theme)
    {
        string dir = Path.Combine(Path.GetTempPath(), "demoviewer-uicapture-themes", Guid.NewGuid().ToString("N"));
        string themes = Path.Combine(dir, "themes");
        Directory.CreateDirectory(themes);
        Environment.SetEnvironmentVariable(AppPaths.ConfigDirEnvVar, dir);
        File.WriteAllText(Path.Combine(themes, "user-pastel.json"), """
                                                                    {
                                                                      "id": "user-pastel",
                                                                      "name": "Pastel",
                                                                      "base": "light",
                                                                      "tokens": {
                                                                        "ShellBg": "#F6EEF7", "PanelBg": "#FBF5FB", "CardBg": "#FFFFFF", "AccentInteractive": "#B45C9C",
                                                                        "TextBright": "#4A2A44", "TextValue": "#3A1E36", "OldAccentName": "#123456"
                                                                      }
                                                                    }
                                                                    """);
        File.WriteAllText(Path.Combine(themes, "half-written.json"), """{ "id": "half", "tokens": { "ShellBg": """);

        SettingsService svc = new(dir);
        svc.Write(s => s.Theme = theme);
        ServiceCollection services = new();
        services.Configure<AppSettings>(svc.Configuration);
        IOptionsMonitor<AppSettings> monitor =
            services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<AppSettings>>();

        ThemeRegistry registry = new();
        registry.Reload();
        registry.Install(Application.Current!);
        return (registry, svc, monitor);
    }

    private static ThemeEditorView ThemeEditor(string themeId, bool missingOnly, Action<ThemeEditorViewModel>? then)
    {
        (ThemeRegistry registry, SettingsService svc, IOptionsMonitor<AppSettings> monitor) = ThemeScratch(themeId);
        ThemeEditorViewModel vm = new(new UserThemeStore(registry),
            new ThemeEditorHost
            {
                ActiveThemeId = () => monitor.CurrentValue.Theme,
                UseTheme = id => svc.Write(s => s.Theme = id),
                ShowAppIn = _ => { },
                RestoreApp = () => { }
            }, themeId, missingOnly);
        then?.Invoke(vm);
        return new ThemeEditorView { DataContext = vm };
    }

    private static StackPanel ThemeSettings()
    {
        (ThemeRegistry registry, SettingsService svc, IOptionsMonitor<AppSettings> monitor) =
            ThemeScratch("user-pastel");
        SettingsViewModel vm = new(svc, monitor, new FeatureGate(monitor), registry) { SettingsFilterText = "theme" };
        vm.AttachThemeEditor(_ => { });

        // The picker's list, drawn with the ComboBox's own item template, since a headless capture cannot
        // show the open drop-down.
        SettingsView view = new() { DataContext = vm };
        ListBox list = new()
        {
            ItemsSource = vm.Themes, ItemTemplate = FindThemeTemplate(view), Width = 260, Margin = new Thickness(12)
        };
        StackPanel stack = new();
        stack.Children.Add(view);
        stack.Children.Add(new TextBlock
        {
            Text = "Theme list (the drop-down's items):", Margin = new Thickness(12, 4), Foreground = Brushes.Gray
        });
        stack.Children.Add(list);
        return stack;
    }

    // The theme ComboBox's ItemTemplate, read off the real view once it has loaded its XAML.
    private static Avalonia.Controls.Templates.IDataTemplate? FindThemeTemplate(SettingsView view) =>
        view.GetLogicalDescendantsOfType<ComboBox>().FirstOrDefault(c =>
                ReferenceEquals(c.ItemsSource, ((SettingsViewModel)view.DataContext!).Themes))
            ?.ItemTemplate;

    private static Views.MainView ThemeWarning()
    {
        (ThemeRegistry registry, SettingsService svc, IOptionsMonitor<AppSettings> monitor) =
            ThemeScratch("user-pastel");
        ThemeWarningViewModel warning = new(registry, () => monitor.CurrentValue.Theme,
            () => monitor.CurrentValue.DismissedThemeWarning, sig => svc.Write(s => s.DismissedThemeWarning = sig),
            (_, _) => { });
        MainViewModel shell = new();
        shell.AttachThemeWarning(warning);
        return new Views.MainView { DataContext = shell };
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Avalonia.LogicalTree.ILogical root) where T : class
    {
        foreach (Avalonia.LogicalTree.ILogical child in root.LogicalChildren)
        {
            if (child is T t)
            {
                yield return t;
            }

            foreach (T d in child.GetLogicalDescendantsOfType<T>())
            {
                yield return d;
            }
        }
    }
}
