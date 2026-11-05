#region

using System.Text.RegularExpressions;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Features;
using DemoViewer.NET.Services;
using DemoViewer.NET.Services.Tutorial;
using DemoViewer.NET.TestSupport;
using DemoViewer.NET.ViewModels.Setup;
using DemoViewer.NET.ViewModels.Tutorial;
using Microsoft.Extensions.Options;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The walkthrough's per-profile cut, through a real <see cref="FeatureGate" />: which steps each category
///     sees and in what order, a gated step following an explicit override either way, the notes folded into a
///     step's body only for the profiles they are written for, the browser head dropping its desktop-only
///     steps, and the indicator counting only what is shown. Also guards every tour and wizard string against
///     an em-dash.
/// </summary>
public class TutorialAudienceTests
{
    private const string EmDash = "\u2014";

    private static readonly string[] _everyProfile =
    [
        "Welcome to DemoViewer",
        "Move between areas",
        "Your demo library",
        "Follow one player",
        "Open a demo",
        "Review the match",
        "Report an analysis error",
        "Watch it back in 2D",
        "Play, pause and seek",
        "Draw on the map",
        "Strat Book is on"
    ];

    private sealed class MutableMonitor(AppSettings value) : IOptionsMonitor<AppSettings>
    {
        public AppSettings CurrentValue { get; set; } = value;
        public AppSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AppSettings, string?> listener) => null;
    }

    private static AppSettings Settings(UserCategory category, params (string Id, bool On)[] overrides)
    {
        AppSettings settings = new() { UserCategory = category };
        foreach ((string id, bool on) in overrides)
        {
            settings.Features.Overrides[id] = on;
        }

        return settings;
    }

    private static IReadOnlyList<TutorialStep> Shown(AppSettings settings, bool isBrowser = false) =>
        new TutorialAudienceFilter(new FeatureGate(new MutableMonitor(settings), false), isBrowser)
            .Apply(TutorialSteps.Default);

    private static string[] Titles(IReadOnlyList<TutorialStep> steps) => [.. steps.Select(s => s.Title)];

    private static string Outro(IReadOnlyList<TutorialStep> steps) => steps[^1].Body;

    [Test]
    public async Task Consumer_SeesTheSharedTour_WithoutTheWorkbench_AndTheReelsAndThemeLines()
    {
        IReadOnlyList<TutorialStep> steps = Shown(Settings(UserCategory.Consumer));

        await Assert.That(Titles(steps)).IsEquivalentTo([.. _everyProfile, "You're all set"],
            CollectionOrdering.Matching);
        await Assert.That(Outro(steps)).Contains("Reels start CS2 with -insecure")
            .Because("Reels are on for a consumer and launch CS2 with VAC off, so the tour says so");
        await Assert.That(Outro(steps)).DoesNotContain("Live Sync")
            .Because("Live Sync is off for a consumer, so its launch notice is not theirs to read");
        await Assert.That(Outro(steps)).Contains("Customize");
    }

    [Test]
    public async Task PowerUser_AddsTheWorkbench_BeforeTheOutro_WithoutTheLiveSyncLine()
    {
        IReadOnlyList<TutorialStep> steps = Shown(Settings(UserCategory.PowerUser));

        await Assert.That(Titles(steps)).IsEquivalentTo(
            [.. _everyProfile, "Write your own rules", "You're all set"],
            CollectionOrdering.Matching);
        await Assert.That(Outro(steps)).Contains("Reels start CS2 with -insecure");
        await Assert.That(Outro(steps)).DoesNotContain("Live Sync");
        await Assert.That(Outro(steps)).Contains("Customize");
    }

    [Test]
    public async Task Developer_SeesEveryStep_AndEveryOutroLine()
    {
        IReadOnlyList<TutorialStep> steps = Shown(Settings(UserCategory.Developer));

        await Assert.That(Titles(steps)).IsEquivalentTo(
            [.. _everyProfile, "Write your own rules", "You're all set"],
            CollectionOrdering.Matching);
        await Assert.That(Outro(steps)).Contains("Reels start CS2 with -insecure");
        await Assert.That(Outro(steps)).Contains("Live Sync starts CS2 with -insecure");
        await Assert.That(Outro(steps)).Contains("Customize");
        await Assert.That(Outro(steps)).StartsWith(TutorialSteps.Default[^1].Body)
            .Because("notes are appended after the authored body, never in place of it");
    }

    // The gate decides, not the category alone: an override moves a gated step either way.
    [Test]
    public async Task GatedSteps_FollowAnExplicitOverride()
    {
        IReadOnlyList<TutorialStep> consumerWithAuthoring =
            Shown(Settings(UserCategory.Consumer, ("tab.authoring", true)));
        IReadOnlyList<TutorialStep> developerWithoutStratBook =
            Shown(Settings(UserCategory.Developer, ("pack.stratbook", false)));
        IReadOnlyList<TutorialStep> withoutPlayerOverview =
            Shown(Settings(UserCategory.PowerUser, ("tab.playeroverview", false)));

        await Assert.That(Titles(consumerWithAuthoring)).Contains("Write your own rules");
        await Assert.That(Titles(developerWithoutStratBook)).DoesNotContain("Strat Book is on");
        await Assert.That(Titles(withoutPlayerOverview)).DoesNotContain("Follow one player");
    }

    // A note has its own audience: Live Sync switched on for a power user brings its line, Reels switched off
    // takes the Reels line with it; export switched off drops the export sentence and keeps the step.
    [Test]
    public async Task Notes_FollowTheirOwnAudience_AndLeaveTheStepInPlace()
    {
        IReadOnlyList<TutorialStep> liveSyncPower =
            Shown(Settings(UserCategory.PowerUser, ("chrome.livesync", true)));
        IReadOnlyList<TutorialStep> noReels = Shown(Settings(UserCategory.Consumer, ("tab.highlights", false)));
        IReadOnlyList<TutorialStep> noExport = Shown(Settings(UserCategory.Consumer, ("playback2d.export", false)));
        IReadOnlyList<TutorialStep> withExport = Shown(Settings(UserCategory.Consumer));

        await Assert.That(Outro(liveSyncPower)).Contains("Live Sync starts CS2 with -insecure");
        await Assert.That(Outro(noReels)).DoesNotContain("-insecure")
            .Because("with Reels and Live Sync both off nothing in the app launches CS2");
        await Assert.That(Outro(noReels)).Contains("Customize");

        TutorialStep drawWith = withExport.Single(s => s.Target == TutorialTarget.AnnotationTools);
        TutorialStep drawWithout = noExport.Single(s => s.Target == TutorialTarget.AnnotationTools);
        await Assert.That(drawWith.Body).Contains("GIF");
        await Assert.That(drawWith.Body).Contains("MP4 needs a full ffmpeg build you install yourself")
            .Because("the app's own ffmpeg download is the LGPL build, which cannot write H.264");
        await Assert.That(drawWithout.Body).IsEqualTo(
            TutorialSteps.Default.Single(s => s.Target == TutorialTarget.AnnotationTools).Body);
    }

    [Test]
    public async Task BrowserHead_DropsItsDesktopOnlySteps_AndNotes()
    {
        IReadOnlyList<TutorialStep> steps = Shown(Settings(UserCategory.Developer), true);

        await Assert.That(Titles(steps)).DoesNotContain("Follow one player")
            .Because("the browser keeps no library index across demos to show a player over");
        await Assert.That(Titles(steps)).DoesNotContain("Report an analysis error")
            .Because("a report has nowhere to be stored in the browser");
        await Assert.That(steps.Single(s => s.Target == TutorialTarget.AnnotationTools).Body)
            .DoesNotContain("Export video");
        await Assert.That(Outro(steps)).IsEqualTo(TutorialSteps.Default[^1].Body);
    }

    // The indicator counts the filtered script, and a category change between replays takes effect on the
    // next Start without a new controller.
    [Test]
    public async Task Controller_CountsOnlyShownSteps_AndRecomposesOnEachStart()
    {
        MutableMonitor monitor = new(Settings(UserCategory.Consumer));
        FeatureGate gate = new(monitor, false);
        TutorialController controller = new(
            TutorialSteps.Default, _ => { }, () => true, () => { }, () => false, () => false,
            new TutorialAudienceFilter(gate, false));

        controller.Start();
        await Assert.That(controller.ViewModel.StepCount).IsEqualTo(12);

        // Walk the consumer's demo run: the step after Strat Book is the outro, never the workbench.
        while (controller.ViewModel.CurrentStep!.Target != TutorialTarget.StratBookHub)
        {
            controller.ViewModel.NextCommand.Execute(null);
        }

        controller.ViewModel.NextCommand.Execute(null);
        await Assert.That(controller.ViewModel.CurrentStep!.Title).IsEqualTo("You're all set");
        await Assert.That(controller.ViewModel.StepIndicator).IsEqualTo("12 of 12");

        monitor.CurrentValue = Settings(UserCategory.Developer);
        controller.Start();
        await Assert.That(controller.ViewModel.StepCount).IsEqualTo(13);
    }

    // Every string the tour and the wizard put on screen: the authored script with all its notes, the
    // controller's waiting hints, the wizard VM's computed copy, and the three XAML files with their
    // comments removed (comments are not shown, and several explain the layout with one).
    [Test]
    public async Task NoTourOrWizardString_ContainsAnEmDash()
    {
        List<string> strings = [];
        foreach (TutorialStep step in TutorialSteps.Default)
        {
            strings.Add(step.Title);
            strings.Add(step.Body);
            strings.Add(step.WaitingHint ?? string.Empty);
            strings.Add(step.NextLabelOverride ?? string.Empty);
            strings.AddRange(step.Notes.Select(n => n.Text));
        }

        strings.AddRange(Shown(Settings(UserCategory.Developer)).Select(s => s.Body));
        strings.AddRange(GatewayHints());

        string dir = Path.Combine(Path.GetTempPath(), "dvtutdash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            SettingsService settings = new(dir);
            foreach (Cs2DemosLookup lookup in new[]
                     {
                         new Cs2DemosLookup(null, []), new Cs2DemosLookup(null, ["C:/Steam"]),
                         new Cs2DemosLookup("C:/Steam/replays", [])
                     })
            {
                FirstRunWizardViewModel wizard = new(settings, () => lookup);
                strings.Add(wizard.NotFoundMessage);
                strings.Add(wizard.StepIndicatorText);
                strings.AddRange(wizard.Categories.SelectMany(c => new[] { c.Title, c.Description }));
                strings.AddRange(wizard.PackOptions.SelectMany(p => new[] { p.Title, p.Copy }));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // best-effort cleanup
            }
        }

        string root = DemoTestHelper.FindRepoRoot()
            ?? throw new InvalidOperationException("repo root not found");
        string views = Path.Combine(root, "src", "App", "DemoViewer.NET", "Views");
        foreach (string xaml in new[]
                 {
                     Path.Combine(views, "Setup", "FirstRunWizardView.axaml"),
                     Path.Combine(views, "Setup", "FirstRunWizardWindow.axaml"),
                     Path.Combine(views, "Tutorial", "TutorialView.axaml")
                 })
        {
            string text = await File.ReadAllTextAsync(xaml);
            strings.Add(Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline));
        }

        string[] offenders = [.. strings.Where(s => s.Contains(EmDash, StringComparison.Ordinal))];
        await Assert.That(offenders).IsEmpty();
    }

    // The three gateway hints are private to the controller, so they are read the way the user meets them.
    private static IEnumerable<string> GatewayHints()
    {
        foreach ((bool card, bool sample) in new[] { (true, false), (false, true), (false, false) })
        {
            TutorialController controller = new(
                TutorialSteps.Default, _ => { }, () => false, () => { }, () => card, () => sample);
            controller.Start();
            while (!controller.ViewModel.IsWaiting)
            {
                controller.ViewModel.NextCommand.Execute(null);
            }

            yield return controller.ViewModel.WaitingHint;
        }
    }
}
