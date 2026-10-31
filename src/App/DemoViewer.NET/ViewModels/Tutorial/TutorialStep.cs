#region

using DemoViewer.NET.Configuration;

#endregion

namespace DemoViewer.NET.ViewModels.Tutorial;

/// <summary>
///     Where the callout bubble should sit relative to the spotlight hole. A <b>hint</b> the overlay
///     honours when there is room; the view clamps the bubble on-screen for spotlights near an edge, so
///     the hint can be overridden by the responsive layout. <see cref="Center" /> is used by the
///     no-spotlight welcome / outro steps (the bubble is centred in the window).
/// </summary>
public enum CalloutPlacement
{
    /// <summary>Centre of the window (used when <see cref="TutorialStep.HasSpotlight" /> is false).</summary>
    Center,

    /// <summary>Above the spotlight (bubble's bottom edge sits over the hole's top).</summary>
    Above,

    /// <summary>Below the spotlight.</summary>
    Below,

    /// <summary>Left of the spotlight.</summary>
    Left,

    /// <summary>Right of the spotlight.</summary>
    Right
}

/// <summary>
///     Names the coarse UI <b>region</b> a step points at. The follow-up tour-engine phase owns an anchor
///     registry that maps each value to a live control and measures its rectangle into
///     <see cref="TutorialViewModel.SpotlightRect" /> (in overlay coordinates). The presentation layer
///     never resolves these itself: it only renders the rect it is handed. <see cref="None" /> = a
///     no-spotlight step (welcome / outro).
/// </summary>
public enum TutorialTarget
{
    /// <summary>No anchored region: a centred welcome/outro card over the plain scrim.</summary>
    None,

    /// <summary>The workspace tab strip (the row of tab headers you switch areas with).</summary>
    TabNav,

    /// <summary>The Library tab / landing region ("where your demos live").</summary>
    LibraryTab,

    /// <summary>The "Open Demo…" affordance (toolbar button): the gateway's picker fallback.</summary>
    OpenDemo,

    /// <summary>
    ///     The first demo card in the Library grid: the gateway's preferred target (double-click loads it,
    ///     no file dialog). Falls back to <see cref="SampleDemo" /> / <see cref="OpenDemo" /> when the library is empty.
    /// </summary>
    FirstLibraryCard,

    /// <summary>
    ///     The Library hero's "Try a sample match" CTA (opens the bundled sample demo). The gateway's
    ///     second preference: an empty library with a resolvable sample spotlights this, and one click continues the
    ///     tour with real match data, no files needed. Falls back to <see cref="OpenDemo" /> when no sample ships
    ///     (e.g. Browser/WASM).
    /// </summary>
    SampleDemo,

    /// <summary>The Stats tab content area (the match review).</summary>
    StatsContent,

    /// <summary>The 2D Playback tab / map viewport.</summary>
    PlaybackTab,

    /// <summary>The NavStrip transport cluster (play/pause, speed, seek).</summary>
    PlaybackTransport,

    /// <summary>The Player Overview tab's content area.</summary>
    PlayerOverviewTab,

    /// <summary>The Stats toolbar's "Report Analysis Errors" (bug) button.</summary>
    ReportError,

    /// <summary>The 2D Playback annotation toolbar.</summary>
    AnnotationTools,

    /// <summary>The Authoring (Rule Workbench) tab's content area.</summary>
    RuleWorkbenchTab,

    /// <summary>
    ///     The Strat Book hub tab. A backdrop only: the hub's view belongs to the extension and carries no
    ///     anchor, so a step naming it is a centred card over that tab.
    /// </summary>
    StratBookHub
}

/// <summary>
///     Who a tour step or note is for. A step must never point at a surface the user cannot see, so each one
///     can name the feature it shows, the categories it is written for, and whether it needs a desktop head.
///     Everything left unset admits everyone. <see cref="Services.Tutorial.TutorialAudienceFilter" /> decides.
/// </summary>
public interface ITutorialAudience
{
    /// <summary>The feature-catalog id this shows; hidden when the gate has it off.</summary>
    string? FeatureId { get; }

    /// <summary>The effective categories this is written for; null is every category.</summary>
    IReadOnlyList<UserCategory>? Categories { get; }

    /// <summary>True when the surface does not exist on the WASM head.</summary>
    bool DesktopOnly { get; }
}

/// <summary>
///     A sentence appended to its step's <see cref="TutorialStep.Body" /> only when its own audience admits the
///     user, so one step can carry a line that only some profiles should read.
/// </summary>
public sealed record TutorialNote : ITutorialAudience
{
    /// <summary>The sentence, appended after the body with a single space.</summary>
    public required string Text { get; init; }

    /// <inheritdoc />
    public string? FeatureId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<UserCategory>? Categories { get; init; }

    /// <inheritdoc />
    public bool DesktopOnly { get; init; }
}

/// <summary>
///     When a step is eligible to fire. The two segments run at different moments, so the engine, not a
///     linear index walk, decides the boundary: <see cref="FirstRun" /> steps need no demo and play right
///     after first-time setup; <see cref="DemoLoaded" /> steps play against an open demo.
/// </summary>
public enum TutorialSegment
{
    /// <summary>Plays at first run, with no demo loaded (welcome, library, open-a-demo).</summary>
    FirstRun,

    /// <summary>Plays against a loaded demo (stats, 2D playback, controls, outro).</summary>
    DemoLoaded
}

/// <summary>
///     One authored step of the first-run Visual Walkthrough: pure content + anchoring metadata, no live
///     state. The engine feeds these into a <see cref="TutorialViewModel" /> one at a time; the overlay
///     binds <see cref="Title" /> / <see cref="Body" /> and reads <see cref="HasSpotlight" /> /
///     <see cref="Placement" /> for its layout. The canonical set is <see cref="TutorialSteps.Default" />.
/// </summary>
public sealed record TutorialStep : ITutorialAudience
{
    /// <summary>Short step heading (consumer-friendly, jargon-free).</summary>
    public required string Title { get; init; }

    /// <summary>One or two sentences of body copy.</summary>
    public required string Body { get; init; }

    /// <summary>Which segment this step belongs to (engine sequencing metadata).</summary>
    public required TutorialSegment Segment { get; init; }

    /// <summary>
    ///     False for the centred welcome / outro cards (scrim only, no cut-out); true for a step that frames a
    ///     region. Drives <see cref="Controls.SpotlightScrim.HasHole" /> and the callout placement.
    /// </summary>
    public bool HasSpotlight { get; init; }

    /// <summary>The region this step anchors to (resolved to a live rect by the engine's anchor registry).</summary>
    public TutorialTarget Target { get; init; } = TutorialTarget.None;

    /// <summary>Preferred bubble placement relative to the spotlight (the view clamps for edges).</summary>
    public CalloutPlacement Placement { get; init; } = CalloutPlacement.Center;

    /// <summary>
    ///     Optional label for the advance button on this step (e.g. "Get started", "Finish"). Null → the
    ///     engine's default ("Next"). Authored here so the CTA wording lives with the content.
    /// </summary>
    public string? NextLabelOverride { get; init; }

    /// <summary>
    ///     Marks the "gateway" step that hands the tour from the first-run segment into the demo segment: it
    ///     needs an open demo to continue. When the user reaches it with no demo loaded, the engine parks the
    ///     tour here in a <b>visible waiting state</b> (spotlight stays on the Open-Demo affordance, the advance
    ///     button is disabled, <see cref="WaitingHint" /> shows) and auto-advances the instant a demo loads,
    ///     rather than hiding the overlay and stranding the user with no guidance. If a demo is already open
    ///     when the step is reached (e.g. replay-from-Settings), it behaves like a normal Next-able step.
    /// </summary>
    public bool WaitsForDemo { get; init; }

    /// <summary>
    ///     Copy shown in place of the advance affordance while a <see cref="WaitsForDemo" /> step is waiting for
    ///     a demo to be opened. Null → the engine's default hint. Ignored on every non-waiting step.
    /// </summary>
    public string? WaitingHint { get; init; }

    /// <inheritdoc />
    public string? FeatureId { get; init; }

    /// <inheritdoc />
    public IReadOnlyList<UserCategory>? Categories { get; init; }

    /// <inheritdoc />
    public bool DesktopOnly { get; init; }

    /// <summary>Sentences appended to <see cref="Body" /> when their own audience admits the user.</summary>
    public IReadOnlyList<TutorialNote> Notes { get; init; } = [];
}

/// <summary>
///     The canonical first-run walkthrough script. This is the engine's <b>input</b>: the presentation
///     layer does not own it, so the engine can sequence, filter by <see cref="TutorialSegment" /> and by each
///     step's audience, or A/B the copy without touching the view.
/// </summary>
public static class TutorialSteps
{
    /// <summary>
    ///     The full script (5 first-run + 8 demo-loaded, including the outro). A user sees the steps whose
    ///     audience admits them; the eight ungated ones are the tour every profile gets.
    /// </summary>
    public static IReadOnlyList<TutorialStep> Default { get; } =
    [
        new()
        {
            Segment = TutorialSegment.FirstRun,
            HasSpotlight = false,
            Target = TutorialTarget.None,
            Placement = CalloutPlacement.Center,
            NextLabelOverride = "Get started",
            Title = "Welcome to DemoViewer",
            Body =
                "Here's a quick tour of the essentials. It takes about a minute, and you can skip it any time "
                + "and reopen it later from Settings."
        },
        new()
        {
            Segment = TutorialSegment.FirstRun,
            HasSpotlight = true,
            Target = TutorialTarget.TabNav,
            Placement = CalloutPlacement.Below,
            Title = "Move between areas",
            Body =
                "These tabs are how you get around: your Library, a match's Stats, 2D Playback and more. Click "
                + "a tab any time to switch. The tour hops between them for you as we go."
        },
        new()
        {
            Segment = TutorialSegment.FirstRun,
            HasSpotlight = true,
            Target = TutorialTarget.LibraryTab,
            Placement = CalloutPlacement.Right,
            Title = "Your demo library",
            Body =
                "The Library is home base. Every demo in the folders you add here shows up as a card with its "
                + "map, players and final score."
        },
        new()
        {
            Segment = TutorialSegment.FirstRun,
            HasSpotlight = true,
            Target = TutorialTarget.PlayerOverviewTab,
            Placement = CalloutPlacement.Left,
            FeatureId = "tab.playeroverview",
            Title = "Follow one player",
            Body =
                "Player Overview follows one player across your whole library: their match history, the maps "
                + "they play and how their stats change over time."
        },
        new()
        {
            Segment = TutorialSegment.FirstRun,
            HasSpotlight = true,
            Target = TutorialTarget.OpenDemo,
            Placement = CalloutPlacement.Below,
            WaitsForDemo = true,
            WaitingHint = "Open a demo to keep going. The tour picks back up automatically.",
            Title = "Open a demo",
            Body =
                "This is where you load demos. Open one now and the tour continues into the match stats and "
                + "2D playback."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.StatsContent,
            Placement = CalloutPlacement.Left,
            Title = "Review the match",
            Body =
                "The Stats tab breaks down the whole match: the scoreboard, how each player performed, and a "
                + "round-by-round timeline."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.ReportError,
            Placement = CalloutPlacement.Below,
            // The button is wired only where a report can be stored, which the browser head cannot do.
            DesktopOnly = true,
            Title = "Report an analysis error",
            Body =
                "If the analysis gets a round or a kill wrong, click this bug button or press Ctrl+Shift+R and "
                + "write down what you saw. The report is saved with the demo."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.PlaybackTab,
            Placement = CalloutPlacement.Right,
            Title = "Watch it back in 2D",
            Body =
                "The 2D Playback tab replays the match on a top-down map, so you can follow every move, kill "
                + "and piece of utility."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.PlaybackTransport,
            Placement = CalloutPlacement.Above,
            Title = "Play, pause and seek",
            Body =
                "These controls run the replay. Play or pause, change the speed, and jump between rounds and "
                + "key moments."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.AnnotationTools,
            Placement = CalloutPlacement.Below,
            FeatureId = "playback2d.annotations",
            Title = "Draw on the map",
            Body =
                "Sketch lines, arrows, shapes and text over the replay. Anchor a drawing to the current moment "
                + "so it fades with the clock, or start a stroke on a player and it follows them.",
            Notes =
            [
                new()
                {
                    // The app's own ffmpeg download is the LGPL build, which has VP9 but no H.264.
                    FeatureId = "playback2d.export",
                    Text =
                        "Export video saves the result as a GIF with nothing else installed. WebM needs ffmpeg, "
                        + "and MP4 needs a full ffmpeg build you install yourself."
                }
            ]
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = false,
            Target = TutorialTarget.StratBookHub,
            Placement = CalloutPlacement.Center,
            FeatureId = "pack.stratbook",
            Title = "Strat Book is on",
            Body =
                "Search your library for situations, tag rounds, build a utility book, scout opponents and "
                + "queue rounds to review later. You can turn it off any time in Settings."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = true,
            Target = TutorialTarget.RuleWorkbenchTab,
            Placement = CalloutPlacement.Left,
            FeatureId = "tab.authoring",
            Title = "Write your own rules",
            Body =
                "Highlight and stat rules are plain YAML. In the Authoring tab you can open one, save your own "
                + "copy, check it for problems and evaluate it against the open demo."
        },
        new()
        {
            Segment = TutorialSegment.DemoLoaded,
            HasSpotlight = false,
            Target = TutorialTarget.None,
            Placement = CalloutPlacement.Center,
            NextLabelOverride = "Finish",
            Title = "You're all set",
            Body =
                "That's the tour. Explore at your own pace, and reopen this walkthrough any time from Settings.",
            Notes =
            [
                new()
                {
                    FeatureId = "chrome.livesync",
                    DesktopOnly = true,
                    Text =
                        "Live Sync and Reels start CS2 with -insecure and patch your CS2 install while they run, "
                        + "so the app asks before each launch."
                },
                new()
                {
                    // No themes folder exists on the browser head.
                    Categories = [UserCategory.Developer],
                    DesktopOnly = true,
                    Text =
                        "To restyle the app, drop a theme .json into the themes folder shown in Settings and "
                        + "click Reload themes."
                }
            ]
        }
    ];
}
