#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The editors on a node face under real pointer input: the node container must not take the press away
///     from a picker or a checkbox inside it.
/// </summary>
[NotInParallel]
[Category("Render")]
public class RuleCanvasInputTests
{
    [Test]
    public async Task ClickingTheAddFacetPicker_OpensIt_AndAPickWrites() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                ComboBox picker = window.GetVisualDescendants().OfType<ComboBox>()
                    .First(c => c.PlaceholderText == "add facet…" && c.IsEffectivelyVisible
                                && c.DataContext is CanvasNodeViewModel { Id: "round_kills" });
                Point at = Center(picker, window);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(picker.IsDropDownOpen).IsTrue().Because("the click reached the picker rather than the node container");

                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                ComboBoxItem headshot = (ComboBoxItem?)picker.ContainerFromItem("headshot")
                    ?? throw new InvalidOperationException("the open dropdown has no item for headshot");
                Point item = Center(headshot, window);
                window.MouseDown(item, MouseButton.Left);
                window.MouseUp(item, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                using (Assert.Multiple())
                {
                    await Assert.That(File.ReadAllText(corpus.UserFile)).Contains("headshot: true")
                        .Because("the picked facet was written with its type's initial value");
                    await Assert.That(picker.SelectedItem).IsNull().Because("the picker clears for the next pick");
                }
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task ClickingAFacetCheckbox_WritesTheFile() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            using EditableCorpus corpus = new();
            (MainViewModel vm, ServiceProvider services) = corpus.NewShell();
            Window window = new() { Width = 1400, Height = 900, Content = new MainView { DataContext = vm } };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                vm.TrySelectTab(RuleCanvasPack.HubId);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();

                CheckBox box = window.GetVisualDescendants().OfType<CheckBox>()
                    .First(c => c.DataContext is CanvasFacetViewModel { Name: "enemy" } && c.IsEffectivelyVisible);
                Point at = Center(box, window);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();

                await Assert.That(File.ReadAllText(corpus.UserFile)).Contains("enemy: false")
                    .Because("the toggle wrote through to the user's file");
            }
            finally
            {
                window.Close();
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    private static Point Center(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("control is not in the window");

    private sealed class EverythingOn : IFeatureGate
    {
        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => true;

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }
    }

    /// <summary>player_round copied into a throwaway user directory, so the canvas opens it editable.</summary>
    private sealed class EditableCorpus : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rulecanvas-input-" + Guid.NewGuid().ToString("N"));

        public string UserFile => Path.Combine(_root, "user", "player_round.rules.yaml");

        public (MainViewModel Shell, ServiceProvider Services) NewShell()
        {
            string user = Path.Combine(_root, "user");
            Directory.CreateDirectory(user);
            string shipped = CS2DemoKit.Analysis.Yaml.RuleSetLocator.ResolveShippedRulesDirectory();
            File.Copy(Path.Combine(shipped, "player_round.rules.yaml"), UserFile);

            RuleCanvasPack pack = new();
            ServiceCollection services = new();
            pack.Register(services);
            services.AddSingleton(new CanvasTabViewModel(_ => true,
                () => RulesetCorpus.Load([(shipped, "shipped"), (user, "user")]), () => Path.Combine(_root, "own")));
            ServiceProvider provider = services.BuildServiceProvider();
            PackContributionSet set = new([pack], provider);
            ModuleRegistry registry = new();
            foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
            {
                registry.Register(module);
            }

            MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, new EverythingOn(), null, null, hubTabs: set.HubTabs);
            vm.RestoreSession();
            return (vm, provider);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // A temp tree that outlives the test is tolerable.
            }
        }
    }
}
