#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>Where a layer's build stands.</summary>
public enum ScoutingLayerState
{
    Building,
    Ready,

    /// <summary>Built, with nothing to show; the message says why.</summary>
    Empty,

    /// <summary>Needs work the extension cannot do yet; the message says what, the gap id where it is tracked.</summary>
    Placeholder
}

/// <summary>A layer's state and the sentence the inspector shows for it.</summary>
/// <param name="State">The state.</param>
/// <param name="Message">Why it is empty, or what a placeholder is waiting for; empty when ready.</param>
/// <param name="GapId">A placeholder's register id: R1–R6 and K1–K5 (plan §3), or the phase item (P3.1) that brings it.</param>
public sealed record ScoutingLayerStatus(ScoutingLayerState State, string Message = "", string? GapId = null)
{
    public static ScoutingLayerStatus Ready { get; } = new(ScoutingLayerState.Ready);

    public static ScoutingLayerStatus Building { get; } = new(ScoutingLayerState.Building, "building");

    /// <summary>
    ///     The message as the inspector and the strip show it, in one voice: a sentence, capitalised and stopped,
    ///     with a placeholder's gap id in brackets unless the message already names it. The layers write
    ///     <see cref="Message" /> in lower case so a test can match it whole; only the display is reworded.
    /// </summary>
    public string Display
    {
        get
        {
            if (State == ScoutingLayerState.Building)
            {
                return "Building…";
            }

            if (Message.Length == 0)
            {
                return "";
            }

            string text = char.ToUpperInvariant(Message[0]) + Message[1..];
            if (!text.EndsWith('.') && !text.EndsWith('…'))
            {
                text += ".";
            }

            return GapId is { } gap && !text.Contains(gap, StringComparison.Ordinal) ? $"{text} [{gap}]" : text;
        }
    }
}

/// <summary>The theme colour a mark, swatch or bar is drawn in; the stage and the view resolve it from the theme.</summary>
public enum ScoutingColor
{
    Neutral,
    Ct,
    T,

    /// <summary>Deaths and lost rounds.</summary>
    Bad,

    /// <summary>The lurk colour.</summary>
    Lurk,
    Player1,
    Player2,
    Player3,
    Player4,
    Player5
}

/// <summary>How a mark is drawn.</summary>
public enum ScoutingMarkShape
{
    Dot,
    Ring,

    /// <summary>A filled disc with <see cref="ScoutingMapMark.Text" /> on it: a numbered route end, a lettered strat.</summary>
    Badge,

    /// <summary>A dashed disc with a letter: a grenade landing (S, M, F, H).</summary>
    Cloud
}

/// <summary>A chip's and a legend entry's picture of what the layer draws.</summary>
/// <param name="Shape">The shape.</param>
/// <param name="Color">The colour.</param>
public sealed record ScoutingSwatch(ScoutingMarkShape Shape, ScoutingColor Color);

/// <summary>One thing a layer draws, in world units on the map's own floors.</summary>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z, which picks the floor pane.</param>
/// <param name="Shape">How it is drawn.</param>
/// <param name="Color">Its colour.</param>
/// <param name="Size">Its radius in screen pixels.</param>
/// <param name="Text">A badge's or a cloud's letter or number.</param>
/// <param name="Opacity">Its own opacity, before the layer's fade.</param>
public sealed record ScoutingMapMark(
    float X,
    float Y,
    float Z,
    ScoutingMarkShape Shape,
    ScoutingColor Color,
    float Size = 4f,
    string? Text = null,
    float Opacity = 1f);

/// <summary>One line under the map saying what a mark means.</summary>
/// <param name="Swatch">The mark.</param>
/// <param name="Text">"ring = first CT death".</param>
public sealed record ScoutingLegendEntry(ScoutingSwatch Swatch, string Text);

/// <summary>A layer's one-line finding: the sentence the inspector leads with, starrable with its scope.</summary>
/// <param name="Text">The sentence.</param>
/// <param name="StarText">The label it is starred under; "read" unless a layer has more than one.</param>
/// <param name="Suggested">Starts starred the first time it is built (once ever per team).</param>
/// <param name="Buy">Files the star under this buy instead of the scope's; for a layer whose reads are per buy.</param>
/// <param name="Image">The picture exported beside the line (a setup heatmap), read at export time.</param>
public sealed record ScoutingRead(
    string Text,
    string StarText = "read",
    bool Suggested = false,
    ScoutingBuy? Buy = null,
    Func<byte[]?>? Image = null);

/// <summary>One inspector row: a label, its number, and what the number opens.</summary>
public sealed record ScoutingInspectorRow
{
    /// <summary>What the row counts, as shown: "5-10 s", "A site".</summary>
    public required string Label { get; init; }

    /// <summary>The number: "4 of 10 rounds"; empty for a row that is only a label.</summary>
    public string Value { get; init; } = "";

    /// <summary>The label the row is starred under, never its number; <see cref="Label" /> when null.</summary>
    public string? StarText { get; init; }

    /// <summary>A bar under the label, 0 to 100; null draws none.</summary>
    public double? BarPercent { get; init; }

    /// <summary>A colour swatch before the label; null draws none.</summary>
    public ScoutingColor? Swatch { get; init; }

    /// <summary>A numbered or lettered badge before the label, matching the map's; null draws none.</summary>
    public string? Badge { get; init; }

    /// <summary>A sub-note under the label.</summary>
    public string? Note { get; init; }

    /// <summary>The rounds behind the number; the number opens them in Review. Empty opens nothing.</summary>
    public IReadOnlyList<TendencyRound> Rounds { get; init; } = [];

    /// <summary>The Review title card and clip note for the rounds: "de_nuke T · first utility 5-10 s".</summary>
    public string OpenTitle { get; init; } = "";

    /// <summary>Plays a moment in 2D: the demo and the frame-clock tick. Null shows no play button.</summary>
    public (string DemoPath, int Tick)? Play { get; init; }

    /// <summary>Files the star under this buy instead of the scope's.</summary>
    public ScoutingBuy? Buy { get; init; }

    /// <summary>A row that stands in for a number the extension cannot produce yet; its note names the gap.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>The text a star files and the long form prints: "first utility 5-10 s".</summary>
    public string KeyText => StarText ?? Label;
}

/// <summary>A titled group of inspector rows.</summary>
/// <param name="Title">The heading, or empty for an untitled group.</param>
/// <param name="Rows">The rows.</param>
public sealed record ScoutingInspectorSection(string Title, IReadOnlyList<ScoutingInspectorRow> Rows);

/// <summary>Everything a layer built for one scope.</summary>
public sealed record ScoutingLayerResult
{
    public ScoutingLayerStatus Status { get; init; } = ScoutingLayerStatus.Ready;

    /// <summary>The chip's count: "12 rds", "3 routes"; empty shows none.</summary>
    public string CountLabel { get; init; } = "";

    public IReadOnlyList<ScoutingMapMark> Marks { get; init; } = [];

    public IReadOnlyList<ScoutingRead> Reads { get; init; } = [];

    public IReadOnlyList<ScoutingInspectorSection> Sections { get; init; } = [];

    public IReadOnlyList<ScoutingLegendEntry> Legend { get; init; } = [];

    /// <summary>The strip mini's rows under the map (strip layers only); the first section's first rows when empty.</summary>
    public IReadOnlyList<ScoutingInspectorRow> Mini { get; init; } = [];

    /// <summary>A layer that waits on work outside the extension.</summary>
    /// <param name="gapId">The register id.</param>
    /// <param name="message">What is missing, in the user's words.</param>
    public static ScoutingLayerResult Placeholder(string gapId, string message) =>
        new() { Status = new ScoutingLayerStatus(ScoutingLayerState.Placeholder, message, gapId) };

    /// <summary>Why a layer is empty on a host that does not build its source (the browser).</summary>
    public const string NotOnThisHost = "not built on this host; the desktop app builds it";

    /// <summary>A load that failed: what failed and the way to try again.</summary>
    /// <param name="what">What could not be read: "setups".</param>
    /// <param name="why">The failure, or null.</param>
    public static ScoutingLayerResult Unreadable(string what, string? why) =>
        Empty(why is null
            ? $"{what} could not be read; select the team again to retry"
            : $"{what} could not be read ({why}); select the team again to retry");

    /// <summary>A layer that built and found nothing.</summary>
    /// <param name="reason">Why.</param>
    public static ScoutingLayerResult Empty(string reason) =>
        new() { Status = new ScoutingLayerStatus(ScoutingLayerState.Empty, reason) };
}
