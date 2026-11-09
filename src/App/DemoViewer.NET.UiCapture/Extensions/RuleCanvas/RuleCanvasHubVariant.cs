#region

using DemoViewer.NET.Configuration;
using DemoViewer.NET.Extensions;
using DemoViewer.NET.Extensions.RuleCanvas;
using DemoViewer.NET.Extensions.RuleCanvas.Model;
using DemoViewer.NET.Extensions.RuleCanvas.Model.Live;
using DemoViewer.NET.Extensions.RuleCanvas.ViewModels;
using DemoViewer.NET.Features;
using DemoViewer.NET.Modules;
using DemoViewer.NET.Modules.Abstractions;
using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Shell;
using DemoViewer.NET.Views;
using Microsoft.Extensions.DependencyInjection;

#endregion

namespace DemoViewer.NET.UiCapture;

/// <summary>
///     The whole shell with the Rule Canvas hub selected: the strip tab, the rail the host draws, and the
///     Canvas section's empty state. The pack is composed the way a shell test composes it, over an empty
///     library and a gate that answers on for everything.
/// </summary>
public static partial class Variants
{
    private static MainView RuleCanvasHub(bool showFacets = true, string ruleset = "player_round", bool editable = false,
        string? focus = null, bool live = false, bool walkthrough = false)
    {
        RuleCanvasPack pack = new();
        ServiceCollection services = new();
        pack.Register(services);
        if (editable || walkthrough)
        {
            // The ruleset copied into a throwaway user directory, so the face shows its editors rather than
            // the read-only text a shipped ruleset gets.
            string root = Path.Combine(Path.GetTempPath(), "dv-uicapture-rules-" + Guid.NewGuid().ToString("N"));
            string user = Path.Combine(root, "user");
            Directory.CreateDirectory(user);
            string shipped = CS2DemoKit.Analysis.Yaml.RuleSetLocator.ResolveShippedRulesDirectory();
            if (!walkthrough)
            {
                File.Copy(Path.Combine(shipped, ruleset + ".rules.yaml"), Path.Combine(user, ruleset + ".rules.yaml"));
            }

            // With live on, a host that names the demo and has no queue: the strip shows the file, the badges
            // come from the canned run below, and Re-run has nothing to run on.
            LiveValuesHost? host = live
                ? new LiveValuesHost(static () => null, static () => CannedDemoPath, static () => null, static action => action())
                : null;
            services.AddSingleton(new CanvasTabViewModel(_ => true,
                () => RulesetCorpus.Load([(shipped, "shipped"), (user, "user")]), () => Path.Combine(root, "own"), null, host));
        }

        ServiceProvider provider = services.BuildServiceProvider();
        PackContributionSet set = new([pack], provider);
        ModuleRegistry registry = new();
        foreach (IWorkspaceModule module in set.Packs.SelectMany(p => p.Modules))
        {
            registry.Register(module);
        }

        // A throwaway library file, so the capture never opens the developer's real one; posts run inline,
        // as the other variants do, since nothing here is on a dispatcher.
        DemoLibraryService library = new(a => a(),
            Path.Combine(Path.GetTempPath(), "dvlib_uicapture_" + Guid.NewGuid().ToString("N") + ".json"));
        // The section's view model is the pack's singleton, so the style is set before the view is built.
        CanvasTabViewModel canvas = provider.GetRequiredService<CanvasTabViewModel>();
        canvas.ShowFacets = showFacets;
        canvas.PreferredRulesetId = ruleset;
        MainViewModel vm = new(null, registry, library, null, new EverythingOn(), null, null, hubTabs: set.HubTabs);
        vm.RestoreSession();
        vm.TrySelectTab(RuleCanvasPack.HubId);
        if (focus is not null)
        {
            canvas.OnActivated(null!);
            canvas.FocusNode(canvas.Nodes.Single(n => n.Id == focus));
        }

        if (live)
        {
            // A canned run stands in for the demo: badges and tooltips render without a parse or a queue.
            canvas.OnActivated(null!);
            canvas.ShowLiveRun(CannedLiveRun(ruleset));
        }

        if (walkthrough)
        {
            // The walkthrough test's ruleset, built in the empty user directory through the canvas's own calls.
            canvas.OnActivated(null!);
            CanvasWalkthrough.BuildAll(canvas);
        }

        return new MainView { DataContext = vm };
    }

    private const string CannedDemoPath = "/demos/benchmarks/003816248937665266002_0544286934.dem";

    // player_round over the reference demo, as stage 0 measured it: the game and eight players, the
    // busiest first, with round_side left unbound so the no-data badge shows too.
    private static LiveValuesRun CannedLiveRun(string ruleset)
    {
        string[] names = ["Goonicus", "fonzieguy", "Little Michael Jackson", "JAM7374", "Vitaltiger8", "zoah", "Cantgobacc", "I LOVE TANKS"];
        List<LiveInstance> instances = [new LiveInstance(0, null, null)];
        for (int i = 0; i < names.Length; i++)
        {
            instances.Add(new LiveInstance(i + 1, i, names[i]));
        }

        Dictionary<(RulesetDocumentNodeKey Key, int Instance), NodeTrack> nodes = [];
        (string Id, int Fires, int Last, float Final)[] stats =
        [
            ("round_kills", 23, 89289, 1), ("round_deaths", 17, 87948, 0), ("round_assists", 6, 82350, 0),
            ("round_damage", 78, 89289, 54), ("round_survived", 11, 87447, 1), ("round_kast_assists", 4, 83342, 0)
        ];
        for (int instance = 1; instance < instances.Count; instance++)
        {
            foreach ((string id, int fires, int last, float final) in stats)
            {
                int scaled = Math.Max(0, fires - instance * 2);
                float value = instance == 1 ? final : Math.Max(0, final - instance % 3);
                nodes[(new RulesetDocumentNodeKey(ruleset, id), instance)] =
                    new NodeTrack(scaled, scaled == 0 ? null : 2400 + instance * 310, scaled == 0 ? null : last - instance * 1200,
                        value.ToString(System.Globalization.CultureInfo.InvariantCulture), value, value != 0);
            }

            bool kast = instance % 2 == 1;
            nodes[(new RulesetDocumentNodeKey(ruleset, "round_kast"), instance)] =
                new NodeTrack(kast ? 19 : 0, kast ? 2600 : null, kast ? 88100 : null, kast ? "1" : "0", kast ? 1 : 0, kast);
        }

        return new LiveValuesRun(CannedDemoPath, "Cs2GotvProfile", 64, -1, 90568,
            instances, nodes, [new RulesetDocumentNodeKey(ruleset, "round_side")],
            TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(3.7), TimeSpan.FromMilliseconds(7), 72L * 1_048_576);
    }

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
}
