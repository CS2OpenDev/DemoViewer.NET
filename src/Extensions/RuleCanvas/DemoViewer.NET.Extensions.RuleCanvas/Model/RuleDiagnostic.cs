#region

using System.Globalization;

#endregion

namespace DemoViewer.NET.Extensions.RuleCanvas.Model;

/// <summary>
///     How much a diagnostic weighs. An error is something that cannot run: an unresolved reference, a
///     ruleset left out of the chain, a view that does not bind. A warning is something the engine tolerated
///     or the canvas noticed: a key the loader dropped, a define nothing fires on, a table with no columns.
///     A warning never blanks anything.
/// </summary>
public enum DiagnosticWeight
{
    Error,
    Warning
}

/// <summary>
///     One thing said about a composition, with its weight and, when it is about one line of one file, where.
///     <see cref="Text" /> is the whole line for the strip's list; <see cref="Message" /> is the part the
///     node's badge repeats, without the file and the code.
/// </summary>
/// <param name="Text">The line as the strip lists it.</param>
/// <param name="Message">The message alone, as the node shows it.</param>
/// <param name="Weight">Error or warning.</param>
/// <param name="RulesetId">The ruleset it is about, or null for the composition as a whole.</param>
/// <param name="Line">The 1-based line in that ruleset's file, or zero when it names none.</param>
public sealed record RuleDiagnostic(string Text, string Message, DiagnosticWeight Weight, string? RulesetId = null, int Line = 0)
{
    /// <summary>The theme token an error badge is painted in.</summary>
    public const string ErrorToken = "AccentError";

    /// <summary>The theme token a warning badge is painted in.</summary>
    public const string WarningToken = "RuleDiagnosticWarning";

    /// <summary>The node this is about, when the canvas knows it outright; filed by line otherwise.</summary>
    public RulesetDocumentNodeKey? Node { get; init; }

    /// <summary>Whether this names a line of a file, so it can be filed on the entry that holds the line.</summary>
    public bool IsPositioned => RulesetId is not null && Line > 0;

    public bool IsError => Weight == DiagnosticWeight.Error;

    public bool IsWarning => Weight == DiagnosticWeight.Warning;

    /// <summary>The badge token for this weight.</summary>
    public string Token => TokenFor(Weight);

    /// <summary>A diagnostic about the composition as a whole, with no line.</summary>
    public static RuleDiagnostic Of(string text, DiagnosticWeight weight) => new(text, text, weight);

    public static string TokenFor(DiagnosticWeight weight) => weight == DiagnosticWeight.Warning ? WarningToken : ErrorToken;

    /// <summary>The token of the heaviest weight among <paramref name="weights" />: an error outweighs any number of warnings.</summary>
    public static string WorstToken(IEnumerable<DiagnosticWeight> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        return weights.Any(w => w == DiagnosticWeight.Error) ? ErrorToken : WarningToken;
    }

    /// <summary>The strip's count: <c>2 errors, 1 warning</c>, <c>1 error</c>, <c>3 warnings</c>; empty for none.</summary>
    public static string Summary(IEnumerable<DiagnosticWeight> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        int errors = 0, warnings = 0;
        foreach (DiagnosticWeight weight in weights)
        {
            if (weight == DiagnosticWeight.Error)
            {
                errors++;
            }
            else
            {
                warnings++;
            }
        }

        List<string> parts = [];
        if (errors > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{errors} {(errors == 1 ? "error" : "errors")}"));
        }

        if (warnings > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{warnings} {(warnings == 1 ? "warning" : "warnings")}"));
        }

        return string.Join(", ", parts);
    }
}
