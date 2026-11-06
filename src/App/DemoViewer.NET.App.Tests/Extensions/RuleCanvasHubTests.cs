#region

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.RuleCanvas.Modules;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using DemoViewer.NET.Views.Shell;
using Microsoft.Extensions.DependencyInjection;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;

#endregion

namespace DemoViewer.NET.AppTests.Extensions;

/// <summary>
///     The real Rule Canvas pack on the real shell: the hub comes from the declaration, the host draws the
///     rail, the one section realizes its empty state, and the pack's switch takes the whole tab away.
/// </summary>
[NotInParallel]
[Category("Render")]
public class RuleCanvasHubTests
{
    private static (MainViewModel Shell, FakeGate Gate, ServiceProvider Services) NewShell(bool packOn = true)
    {
        RuleCanvasPack pack = new();
        ServiceCollection services = new();
        pack.Register(services);
        ServiceProvider provider = services.BuildServiceProvider();
        PackContributionSet set = new([pack], provider);
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
        {
            registry.Register(module);
        }

        FakeGate gate = new();
        if (!packOn)
        {
            gate.Answers[RuleCanvasPack.PackFeatureId] = false;
        }

        MainViewModel vm = new(null, registry, TestLibraries.Empty(), null, gate, null, null, hubTabs: set.HubTabs);
        vm.RestoreSession();
        return (vm, gate, provider);
    }

    private static List<string> RenderedTexts(MainViewModel vm, string tabId) =>
        Rendered(vm, tabId, window =>
            window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList());

    private static T Rendered<T>(MainViewModel vm, string tabId, Func<Window, T> read)
    {
        Window window = new() { Width = 1280, Height = 800, Content = new MainView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.TrySelectTab(tabId);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        T result = read(window);
        window.Close();
        return result;
    }

    [Test]
    public async Task ThePack_HostsItsHubAndSection_WithTheTitlesTheOwnerSees() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _, ServiceProvider services) = NewShell();
            try
            {
                List<string> texts = RenderedTexts(vm, RuleCanvasPack.HubId);
                HubTabViewModel host = vm.HostViewModel(RuleCanvasPack.HubId)!;
                using (Assert.Multiple())
                {
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(RuleCanvasPack.HubId);
                    await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(CanvasModule.TabId)
                        .Because("a section sits on the rail, not on the strip");
                    await Assert.That(vm.Tabs.Single(t => t.TabId == RuleCanvasPack.HubId).Header).IsEqualTo("Rule Canvas");
                    await Assert.That(host.Sections.Sections.Select(s => s.TabId)).IsEquivalentTo([CanvasModule.TabId]);
                    await Assert.That(vm.Tabs.Single(t => t.TabId == RuleCanvasPack.HubId).ActiveContent).IsTypeOf<HubTabView>();
                    await Assert.That(texts).Contains("RULE CANVAS").Because("the band binds the declaration's rail label");
                    await Assert.That(texts).Contains("Canvas").Because("the rail lists the one section");
                    await Assert.That(texts).Contains("SAMPLE")
                        .Because("the first section is selected and realized when the host is");
                    await Assert.That(texts).Contains("Open Authoring");
                }
            }
            finally
            {
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    /// <summary>
    ///     The sample graph on the real canvas: a templated container per node, a connector per value read or
    ///     produced, and a connection per wire, all realized from a view-level style include of the Nodify theme.
    /// </summary>
    [Test]
    public async Task TheSampleCanvas_RealizesItsNodesConnectorsAndWires() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _, ServiceProvider services) = NewShell();
            try
            {
                (int containers, int connectors, int wires, List<string> texts) = Rendered(vm, RuleCanvasPack.HubId, window =>
                {
                    List<Avalonia.Visual> all = [.. window.GetVisualDescendants()];
                    return (all.OfType<ItemContainer>().Count(), all.OfType<Connector>().Count(),
                        all.OfType<Connection>().Count(),
                        all.OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0).ToList());
                });
                using (Assert.Multiple())
                {
                    await Assert.That(containers).IsEqualTo(5).Because("five nodes in the sample");
                    await Assert.That(wires).IsEqualTo(2).Because("the flag reads two siblings");
                    await Assert.That(connectors).IsEqualTo(4).Because("two outputs read, two inputs reading");
                    await Assert.That(texts).Contains("round_kast");
                    await Assert.That(texts).Contains("enemy").Because("the detailed style shows the facet rows");
                    await Assert.That(texts).Contains("while player.alive").Because("the gate is a chip");
                }
            }
            finally
            {
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task ThePacksSwitch_TakesTheHubAway_AndBringsItBack() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, FakeGate gate, ServiceProvider services) = NewShell();
            try
            {
                await Assert.That(vm.TrySelectTab(CanvasModule.TabId)).IsTrue();
                await Assert.That(vm.SelectedTab!.TabId).IsEqualTo(RuleCanvasPack.HubId);
                gate.Answers[RuleCanvasPack.PackFeatureId] = false;
                gate.RaiseChanged();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(RuleCanvasPack.HubId);
                gate.Answers[RuleCanvasPack.PackFeatureId] = true;
                gate.RaiseChanged();
                await Assert.That(vm.Tabs.Select(t => t.TabId)).Contains(RuleCanvasPack.HubId);
            }
            finally
            {
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    [Test]
    public async Task WithThePackOff_TheHubIsNeverBuilt() =>
        await HeadlessSession.RunOnUi(async () =>
        {
            (MainViewModel vm, _, ServiceProvider services) = NewShell(packOn: false);
            try
            {
                await Assert.That(vm.Tabs.Select(t => t.TabId)).DoesNotContain(RuleCanvasPack.HubId);
                await Assert.That(vm.TrySelectTab(CanvasModule.TabId)).IsFalse();
            }
            finally
            {
                vm.Dispose();
                await services.DisposeAsync();
            }
        });

    private sealed class FakeGate : IFeatureGate
    {
        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public UserCategory Category => UserCategory.Developer;

        public int HiddenCount => 0;

        public bool IsEnabled(string featureId) => !Answers.TryGetValue(featureId, out bool value) || value;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
