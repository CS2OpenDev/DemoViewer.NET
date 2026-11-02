#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Theme Customization window's view-model, the shell's missing-colours notice, and the Built-in chip in
///     Settings. All on a scratch config root; the live-preview test drives the real application's variant.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ThemeEditorTests
{
    private const string Partial = """{ "id": "partial", "name": "Partial", "tokens": { "ShellBg": "#010203", "PanelBg": "#040506" } }""";

    private sealed class FakeHost
    {
        public string Active { get; set; } = "dark";

        public List<string> Used { get; } = [];

        public ThemeVariant? AppVariant { get; set; }

        public int Restores { get; set; }

        public ThemeEditorHost Build() => new()
        {
            ActiveThemeId = () => Active,
            UseTheme = id =>
            {
                Used.Add(id);
                Active = id;
            },
            ShowAppIn = v => AppVariant = v,
            RestoreApp = () =>
            {
                AppVariant = null;
                Restores++;
            }
        };
    }

    private static (ThemeEditorViewModel Vm, ThemeRegistry Registry, FakeHost Host) Open(string? themeId, bool missingOnly = false)
    {
        ThemeRegistry registry = new();
        registry.Reload();
        FakeHost host = new();
        return (new ThemeEditorViewModel(new UserThemeStore(registry), host.Build(), themeId, missingOnly), registry, host);
    }

    [Test]
    public async Task ABuiltIn_IsReadOnly_AndCustomizeMakesANamedCopy()
    {
        using TempThemesFolder folder = new();
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, _) = Open("egirl");
            using (vm)
            {
                await Assert.That(vm.IsReadOnly).IsTrue();
                ThemeVariant? firstPreview = vm.PreviewVariant;
                await Assert.That(vm.MissingCount).IsEqualTo(0).Because("a built-in is complete");

                ThemeTokenRowViewModel shell = vm.Sections[0].Rows[0];
                shell.Hex = "#FF0000";
                await Assert.That(shell.Value.Color).IsEqualTo(Color.Parse("#0A0008")).Because("a built-in refuses edits");
                await Assert.That(vm.IsDirty).IsFalse();

                vm.CopyCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.NameCopy);
                await Assert.That(vm.CopyName).IsEqualTo("E-Girl (Pink / Black) copy");

                vm.CopyName = "light";
                await Assert.That(vm.CopyNameError).IsEqualTo("There is already a theme called \"Light\".");
                vm.ConfirmCopyCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.NameCopy).Because("a clashing name is not saved");

                vm.CopyName = "Mine";
                vm.ConfirmCopyCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.None);
                await Assert.That(vm.Theme.Id).IsEqualTo("user-mine");
                await Assert.That(vm.PreviewVariant).IsNotEqualTo(firstPreview).Because("a new theme gets a new preview variant, so the pane re-resolves");
                await Assert.That(vm.CanEdit).IsTrue();
                await Assert.That(File.Exists(Path.Combine(folder.Themes, "user-mine.json"))).IsTrue();
                await Assert.That(vm.Themes.Single(t => t.Theme.Id == "user-mine").IsBuiltIn).IsFalse();
                await Assert.That(registry.Find("egirl")!.IsBuiltIn).IsTrue();
            }
        });
    }

    [Test]
    public async Task MissingTokens_AreFlaggedFiltered_AndFillMissingWritesThem()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, _) = Open("partial", missingOnly: true);
            using (vm)
            {
                int expected = ThemeTokens.All.Count - 2;
                await Assert.That(vm.MissingOnly).IsTrue();
                await Assert.That(vm.MissingCount).IsEqualTo(expected);
                await Assert.That(vm.Sections.Sum(s => s.VisibleRows.Count)).IsEqualTo(expected);
                await Assert.That(vm.Sections.SelectMany(s => s.VisibleRows).All(r => r.IsMissing)).IsTrue();
                await Assert.That(vm.Themes.Single(t => t.Theme.Id == "partial").MissingText).IsEqualTo($"{expected} missing");

                vm.SearchText = "PanelHeaderBg";
                await Assert.That(vm.Sections.SelectMany(s => s.VisibleRows).Select(r => r.Key)).IsEquivalentTo(["PanelHeaderBg"]);
                vm.SearchText = "";

                vm.FillMissingCommand.Execute(null);
                await Assert.That(vm.MissingCount).IsEqualTo(0);
                await Assert.That(vm.IsDirty).IsTrue();
                vm.SaveCommand.Execute(null);

                await Assert.That(registry.Find("partial")!.MissingTokens).IsEmpty();
                await Assert.That(registry.PaletteFor("partial")!.Colors["ShellBg"]).IsEqualTo(Color.Parse("#010203"));
                await Assert.That(vm.IsDirty).IsFalse();
            }
        });
    }

    [Test]
    public async Task UndoAndReset_PutValuesBack()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, _, _) = Open("partial");
            using (vm)
            {
                ThemeTokenRowViewModel cardBg = vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "CardBg");
                Color before = cardBg.Value.Color;
                await Assert.That(cardBg.IsMissing).IsTrue();

                cardBg.Hex = "#123456";
                await Assert.That(cardBg.IsMissing).IsFalse();
                await Assert.That(cardBg.CanReset).IsTrue();
                vm.UndoCommand.Execute(null);
                await Assert.That(cardBg.Value.Color).IsEqualTo(before);
                await Assert.That(cardBg.IsMissing).IsTrue().Because("undo puts the missing state back too");
                await Assert.That(vm.IsDirty).IsFalse();

                ThemeTokenRowViewModel shell = vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "ShellBg");
                shell.ResetCommand.Execute(null);
                await Assert.That(shell.Value.Color).IsEqualTo(BuiltInThemes.Dark.Colors["ShellBg"]);
            }
        });
    }

    // A picker drag is many edits but one undo step; undoing it, or editing a colour back by hand, leaves
    // nothing to save.
    [Test]
    public async Task Dirty_FollowsTheValues_NotTheNumberOfEdits()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, _, _) = Open("partial");
            using (vm)
            {
                ThemeTokenRowViewModel cardBg = vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "CardBg");
                cardBg.Hex = "#111111"; // a drag: several values inside a second
                cardBg.Hex = "#222222";
                cardBg.Hex = "#333333";

                await Assert.That(vm.IsDirty).IsTrue();
                vm.UndoCommand.Execute(null);
                await Assert.That(cardBg.IsMissing).IsTrue();
                await Assert.That(vm.CanUndo).IsFalse().Because("the drag was one undo step");
                await Assert.That(vm.IsDirty).IsFalse().Because("the theme matches its file again");

                ThemeTokenRowViewModel shell = vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "ShellBg");
                shell.Hex = "#FFFFFF";
                await Assert.That(vm.IsDirty).IsTrue();
                shell.Hex = "#010203";
                await Assert.That(vm.IsDirty).IsFalse().Because("the colour is back to the saved one");
            }
        });
    }

    // Discard has to put the rows and the preview back, not just the name and base: Use theme and a
    // Reveal of the same theme stay on it after the prompt, and a later Save would write the discarded colours.
    [Test]
    public async Task Discard_PutsTheSavedColoursBack_WhenTheEditorStaysOnTheTheme()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, FakeHost host) = Open("partial");
            using (vm)
            {
                ThemeTokenRowViewModel Shell() => vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "ShellBg");

                Shell().Hex = "#ABCDEF";
                ThemeVariant? edited = vm.PreviewVariant;
                vm.UseThemeCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.UnsavedChanges);
                vm.DiscardAndContinueCommand.Execute(null);
                await Assert.That(host.Used).IsEquivalentTo(["partial"]);
                await Assert.That(Shell().Value.Color).IsEqualTo(Color.Parse("#010203"));
                await Assert.That(vm.CurrentPalette().Colors["ShellBg"]).IsEqualTo(Color.Parse("#010203"));
                await Assert.That(vm.PreviewVariant).IsNotEqualTo(edited).Because("the preview restarts from the saved colours");
                await Assert.That(registry.PreviewVariant).IsEqualTo(vm.PreviewVariant);
                await Assert.That(vm.IsDirty).IsFalse();

                Shell().Hex = "#FEDCBA";
                vm.Reveal("partial", missingOnly: true);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.UnsavedChanges);
                vm.DiscardAndContinueCommand.Execute(null);
                await Assert.That(vm.MissingOnly).IsTrue();
                await Assert.That(Shell().Value.Color).IsEqualTo(Color.Parse("#010203"));

                vm.Name = "Partial renamed";
                vm.SaveCommand.Execute(null);
                await Assert.That(registry.PaletteFor("partial")!.Colors["ShellBg"]).IsEqualTo(Color.Parse("#010203"))
                    .Because("the next save writes the file as it was, not the discarded edit");
            }
        });
    }

    // The editor's own prompt holds the user closing the window, never the main window closing it.
    [Test]
    public async Task ClosingTheOwner_DoesNotWaitForTheUnsavedPrompt_AndEndsThePreview()
    {
        using TempThemesFolder folder = new();
        string file = folder.Write("partial.json", Partial);
        string onDisk = await File.ReadAllTextAsync(file);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, FakeHost host) = Open("partial");
            Window owner = new() { Width = 400, Height = 300 };
            owner.Show();
            ThemeEditorWindow editor = new() { DataContext = vm };
            bool ownerClosed = false;
            bool editorClosed = false;
            owner.Closed += (_, _) => ownerClosed = true;
            editor.Closed += (_, _) =>
            {
                editorClosed = true;
                vm.Dispose(); // as DesktopWindowService does
            };
            editor.Show(owner);
            Dispatcher.UIThread.RunJobs();

            vm.ApplyToApp = true;
            vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "ShellBg").Hex = "#ABCDEF";

            editor.Close();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(editorClosed).IsFalse().Because("the user's own close waits for the prompt");
            await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.UnsavedChanges);

            owner.Close();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ownerClosed).IsTrue();
            await Assert.That(editorClosed).IsTrue();
            await Assert.That(registry.PreviewVariant).IsNull();
            await Assert.That(host.AppVariant).IsNull().Because("the app goes back to its theme");
            await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(onDisk).Because("the edits are discarded");
        });
    }

    [Test]
    public async Task LivePreview_FollowsEdits_AndRevertsOnCancel()
    {
        using TempThemesFolder folder = new();
        string file = folder.Write("partial.json", Partial);
        string onDisk = await File.ReadAllTextAsync(file);
        await HeadlessSession.RunOnUi(async () =>
        {
            ThemeRegistry registry = new();
            registry.Reload();
            Application app = Application.Current!;
            registry.Install(app);
            ThemeVariant? prior = app.RequestedThemeVariant;
            app.RequestedThemeVariant = registry.VariantFor("partial");
            ThemeEditorHost host = new()
            {
                ActiveThemeId = () => "partial",
                UseTheme = _ => { },
                ShowAppIn = v => app.RequestedThemeVariant = v,
                RestoreApp = () => app.RequestedThemeVariant = registry.VariantFor("partial")
            };
            try
            {
                ThemeEditorViewModel vm = new(new UserThemeStore(registry), host, "partial");
                bool closed = false;
                vm.CloseRequested += (_, _) => closed = true;

                vm.ApplyToApp = true;
                await Assert.That(app.RequestedThemeVariant).IsEqualTo(vm.PreviewVariant);

                vm.Sections.SelectMany(s => s.Rows).Single(r => r.Key == "ShellBg").Hex = "#ABCDEF";
                app.TryGetResource("ShellBg", app.ActualThemeVariant, out object? live);
                await Assert.That(((ISolidColorBrush)live!).Color).IsEqualTo(Color.Parse("#ABCDEF"));

                vm.CancelCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.UnsavedChanges);
                await Assert.That(closed).IsFalse();
                vm.DiscardAndContinueCommand.Execute(null);
                await Assert.That(closed).IsTrue();
                vm.Dispose();

                await Assert.That(app.RequestedThemeVariant).IsEqualTo(registry.VariantFor("partial"));
                await Assert.That(registry.PreviewVariant).IsNull();
                app.TryGetResource("ShellBg", app.ActualThemeVariant, out object? after);
                await Assert.That(((ISolidColorBrush)after!).Color).IsEqualTo(Color.Parse("#010203"));
                await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(onDisk);
            }
            finally
            {
                app.RequestedThemeVariant = prior;
                registry.Uninstall(app);
            }
        });
    }

    [Test]
    public async Task Delete_AsksFirst_AndFallsBackToTheBaseWhenItWasInUse()
    {
        using TempThemesFolder folder = new();
        string file = folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, FakeHost host) = Open("partial");
            host.Active = "partial";
            using (vm)
            {
                vm.DeleteCommand.Execute(null);
                await Assert.That(vm.Prompt).IsEqualTo(ThemeEditorPrompt.ConfirmDelete);
                await Assert.That(vm.DeletePromptText).Contains("can't be undone");
                vm.DismissPromptCommand.Execute(null);
                await Assert.That(File.Exists(file)).IsTrue();

                vm.DeleteCommand.Execute(null);
                vm.ConfirmDeleteCommand.Execute(null);
                await Assert.That(File.Exists(file)).IsFalse();
                await Assert.That(registry.Contains("partial")).IsFalse();
                await Assert.That(host.Used).IsEquivalentTo(["dark"]);
                await Assert.That(vm.Theme.Id).IsEqualTo("dark");
            }
        });
    }

    [Test]
    public async Task Renaming_ToATakenName_IsRefused()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            (ThemeEditorViewModel vm, ThemeRegistry registry, _) = Open("partial");
            using (vm)
            {
                vm.Name = "High Contrast";
                await Assert.That(vm.NameError).IsNotNull();
                vm.SaveCommand.Execute(null);
                await Assert.That(registry.Find("partial")!.DisplayName).IsEqualTo("Partial");

                vm.Name = "Renamed";
                vm.SaveCommand.Execute(null);
                await Assert.That(registry.Find("partial")!.DisplayName).IsEqualTo("Renamed");
            }
        });
    }

    // ── Missing-colours notice ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Warning_ShowsForASelectedThemeMissingTokens_AndItsActionOpensTheEditorFiltered()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        folder.Write("complete.json", ThemeExport.ToJson(BuiltInThemes.Light, "complete", "Complete"));
        ThemeRegistry registry = new();
        registry.Reload();
        string selected = "partial";
        string? dismissed = null;
        RecordingWindows windows = new();
        ThemeEditorLauncher launcher = new(windows, new UserThemeStore(registry), new FakeHost().Build());
        ThemeWarningViewModel warning = new(registry, () => selected, () => dismissed, s => dismissed = s,
            (id, missingOnly) => launcher.Open(id, missingOnly));

        await Assert.That(warning.IsVisible).IsTrue();
        await Assert.That(warning.Message).IsEqualTo(
            $"Your theme \"Partial\" is missing {ThemeTokens.All.Count - 2} colours, so they use the Dark defaults for now. Open Theme Customization to fill them in.");

        await HeadlessSession.RunOnUi(async () =>
        {
            warning.OpenCommand.Execute(null);
            await Assert.That(windows.Opened.Single()).IsEqualTo(("partial", true));
            using ThemeEditorViewModel vm = windows.Create!("partial", true);
            await Assert.That(vm.Theme.Id).IsEqualTo("partial");
            await Assert.That(vm.MissingOnly).IsTrue();
        });

        foreach (string id in new[] { "complete", "egirl", "dark", "system", "nope" })
        {
            selected = id;
            warning.Reevaluate();
            await Assert.That(warning.IsVisible).IsFalse().Because(id);
        }
    }

    [Test]
    public async Task Warning_StaysDismissed_UntilTheMissingSetChanges()
    {
        using TempThemesFolder folder = new();
        string file = folder.Write("partial.json", Partial);
        ThemeRegistry registry = new();
        registry.Reload();
        string? dismissed = null;
        ThemeWarningViewModel warning = new(registry, () => "partial", () => dismissed, s => dismissed = s, (_, _) => { });

        warning.DismissCommand.Execute(null);
        await Assert.That(warning.IsVisible).IsFalse();
        await Assert.That(dismissed).StartsWith("partial|");

        // A later launch with the same file: still quiet.
        registry.Reload();
        warning.Reevaluate();
        await Assert.That(warning.IsVisible).IsFalse();

        // One more token set: a different missing set, so it shows again.
        await File.WriteAllTextAsync(file, Partial.Replace("\"PanelBg\"", "\"CardBg\": \"#070809\", \"PanelBg\""));
        registry.Reload();
        warning.Reevaluate();
        await Assert.That(warning.IsVisible).IsTrue();
    }

    private sealed class RecordingWindows : IWindowService
    {
        public List<(string?, bool)> Opened { get; } = [];

        public Func<string?, bool, ThemeEditorViewModel>? Create { get; private set; }

        public void ShowThemeEditor(Func<string?, bool, ThemeEditorViewModel> create, string? themeId, bool missingOnly)
        {
            Create = create;
            Opened.Add((themeId, missingOnly));
        }

        public void OpenParseChainInspector(object dataContext)
        {
        }

        public void OpenSettings(SettingsViewModel viewModel)
        {
        }

        public void ShowErrorReport(ViewModels.ErrorReport.ErrorReportViewModel viewModel)
        {
        }

        public void ShowFirstRunWizard(ViewModels.Setup.FirstRunWizardViewModel viewModel)
        {
        }

        public void ShowUpdateNotice(ViewModels.Update.UpdateNoticeViewModel viewModel)
        {
        }

        public void ShowWhatsNew(ViewModels.Update.WhatsNewViewModel viewModel)
        {
        }
    }

    // ── Settings chip ────────────────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TheBuiltInChip_ShowsOnlyOnBuiltIns()
    {
        using TempThemesFolder folder = new();
        folder.Write("partial.json", Partial);
        await HeadlessSession.RunOnUi(async () =>
        {
            SettingsService svc = new(folder.Root);
            ServiceCollection services = new();
            services.Configure<AppSettings>(svc.Configuration);
            using ServiceProvider sp = services.BuildServiceProvider();
            IOptionsMonitor<AppSettings> monitor = sp.GetRequiredService<IOptionsMonitor<AppSettings>>();
            ThemeRegistry registry = new();
            registry.Reload();
            using SettingsViewModel vm = new(svc, monitor, new FeatureGate(monitor, false), registry);
            SettingsView view = new() { DataContext = vm };
            StackPanel items = new();
            Window window = new() { Width = 600, Height = 900, Content = new StackPanel { Children = { view, items } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            ComboBox picker = view.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, vm.Themes));
            Dictionary<string, bool> chip = [];
            foreach (Theme theme in vm.Themes)
            {
                Control item = picker.ItemTemplate!.Build(theme)!;
                item.DataContext = theme;
                items.Children.Add(item);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                chip[theme.Id] = item.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Built-in" && t.IsEffectivelyVisible);
            }

            window.Close();
            await Assert.That(chip.Where(c => c.Value).Select(c => c.Key))
                .IsEquivalentTo(["dark", "light", "system", "high-contrast", "egirl"]);
            await Assert.That(chip["partial"]).IsFalse();
            await Assert.That(vm.SelectedThemeMissingText).IsNull().Because("Dark is selected and complete");
        });
    }
}
