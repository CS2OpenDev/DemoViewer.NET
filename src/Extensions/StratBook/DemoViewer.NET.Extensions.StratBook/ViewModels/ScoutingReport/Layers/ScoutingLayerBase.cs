#region

using System.Globalization;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     The shared shape of a layer: the fixed members as constructor arguments and the row helpers every
///     layer over a <see cref="TendencyBucket" /> list uses. A layer file derives from it and writes
///     <see cref="BuildAsync" />; it can override any member that differs by side.
/// </summary>
public abstract class ScoutingLayerBase : IScoutingLayer
{
    /// <param name="id">The stable id.</param>
    /// <param name="name">The chip name, both sides.</param>
    /// <param name="placement">Map or strip.</param>
    /// <param name="sides">The sides it applies to.</param>
    /// <param name="swatch">What the map draws.</param>
    /// <param name="buildCode">Plan §2's code for the build-status chip.</param>
    protected ScoutingLayerBase(string id, string name, ScoutingLayerPlacement placement, ScoutingSides sides,
        ScoutingSwatch swatch, string buildCode)
    {
        Id = id;
        Name = name;
        Placement = placement;
        Sides = sides;
        Swatch = swatch;
        BuildCode = buildCode;
    }

    /// <summary>The chip name when it does not differ by side.</summary>
    protected string Name { get; }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public ScoutingLayerPlacement Placement { get; }

    /// <inheritdoc />
    public ScoutingSides Sides { get; }

    /// <inheritdoc />
    public virtual bool UsesBuy => false;

    /// <inheritdoc />
    public ScoutingSwatch Swatch { get; }

    /// <inheritdoc />
    public string BuildCode { get; }

    /// <inheritdoc />
    public virtual string NameFor(ScoutingSide side) => Name;

    /// <inheritdoc />
    public virtual string TitleFor(ScoutingSide side) => NameFor(side);

    /// <inheritdoc />
    public virtual bool OnByDefault(ScoutingSide side) => false;

    /// <inheritdoc />
    public abstract Task<ScoutingLayerResult> BuildAsync(ScoutingLayerContext context,
        CancellationToken cancellationToken);

    /// <summary>
    ///     One row per bucket with rounds: the label, "n of m rounds", a bar of the share, and the rounds behind it
    ///     under the Review title "de_nuke T · {word} {label}". The star text is the title past the prefix, the
    ///     same words the Opening Tendencies, Post-Plant and Situational findings were filed under, so a remapped
    ///     schema-1 star lands on the row.
    /// </summary>
    /// <param name="context">The scope.</param>
    /// <param name="buckets">The numbers.</param>
    /// <param name="of">The sample they are counted in; 0 when it is unknown.</param>
    /// <param name="word">What they count ("first utility"); empty puts the label straight after the prefix.</param>
    /// <param name="color">The bar's colour.</param>
    protected static IReadOnlyList<ScoutingInspectorRow> Rows(ScoutingLayerContext context,
        IEnumerable<TendencyBucket> buckets, int of, string word, ScoutingColor? color = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(buckets);
        return [.. buckets.Where(b => b.Count > 0).Select(b => Row(context, b, of, word, color))];
    }

    /// <summary>One bucket's row; see <see cref="Rows" />.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="bucket">The number.</param>
    /// <param name="of">Its sample; 0 when unknown.</param>
    /// <param name="word">What it counts.</param>
    /// <param name="color">The bar's colour.</param>
    /// <param name="label">The label shown, when it is not the bucket's.</param>
    protected static ScoutingInspectorRow Row(ScoutingLayerContext context, TendencyBucket bucket, int of, string word,
        ScoutingColor? color = null, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(bucket);
        string text = word.Length == 0 ? bucket.Label : $"{word} {bucket.Label}";
        string title = word.Length == 0 ? $"{context.Prefix} {bucket.Label}" : $"{context.Prefix} · {text}";
        return new ScoutingInspectorRow
        {
            Label = label ?? bucket.Label,
            Value = CountText(bucket.Count, of),
            StarText = text,
            BarPercent = of > 0 ? Math.Min(100, 100.0 * bucket.Count / of) : null,
            Swatch = color,
            Rounds = bucket.Rounds,
            OpenTitle = title
        };
    }

    /// <summary>The sample row: "rounds", every round of the scope, itself opening them.</summary>
    /// <param name="context">The scope.</param>
    /// <param name="rounds">The rounds.</param>
    /// <param name="word">What the sample counts: "rounds", "plants".</param>
    protected static ScoutingInspectorRow Sample(ScoutingLayerContext context, TendencyBucket rounds, string word)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rounds);
        return new ScoutingInspectorRow
        {
            Label = word,
            Value = Plural(rounds.Count, "round"),
            StarText = word,
            Rounds = rounds.Rounds,
            OpenTitle = $"{context.Prefix} · {word}"
        };
    }

    /// <summary>The bucket with the most rounds, skipping the ones named in <paramref name="except" />; null with none.</summary>
    /// <param name="buckets">The buckets.</param>
    /// <param name="except">Labels that are not an answer ("no plant", "no contact").</param>
    protected static TendencyBucket? Top(IEnumerable<TendencyBucket> buckets, params string[] except) =>
        buckets.Where(b => b.Count > 0 && !except.Contains(b.Label, StringComparer.Ordinal))
            .OrderByDescending(b => b.Count).FirstOrDefault();

    /// <summary>"4 of 10 rounds", or "4 rounds" when the sample is unknown.</summary>
    /// <param name="count">The rounds counted.</param>
    /// <param name="of">The sample; 0 when unknown.</param>
    public static string CountText(int count, int of) =>
        of > 0 ? $"{count.ToString(CultureInfo.InvariantCulture)} of {Plural(of, "round")}" : Plural(count, "round");

    /// <summary>"1 round", "12 rounds".</summary>
    /// <param name="count">The count.</param>
    /// <param name="noun">The singular noun.</param>
    public static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}s";

    /// <summary>"61%" from a share, with no space before the sign whatever the culture.</summary>
    /// <param name="share">0 to 1.</param>
    public static string Percent(double share) =>
        ((int)Math.Round(share * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>"0:52" from seconds.</summary>
    /// <param name="seconds">The seconds.</param>
    public static string Clock(double seconds)
    {
        int total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }
}
