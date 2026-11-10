#region

using System.Text;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams;

/// <summary>Which of a Scouting Report's two forms an export prints.</summary>
public enum ScoutingReportForm
{
    /// <summary>The one-pager: the identity, the roster and the starred findings. The default export.</summary>
    OnePager,

    /// <summary>The working document: every finding not left out, starred ones marked.</summary>
    LongForm
}

/// <summary>The file an export writes.</summary>
public enum ScoutingReportFormat
{
    /// <summary>Self-contained HTML with a print stylesheet, the LAN Print route.</summary>
    Html,

    /// <summary>Markdown, for Discord and a text diff. Pictures are left out with a line saying so.</summary>
    Markdown
}

/// <summary>
///     One line of a Scouting Report as it will print: its key, the section it sits under, the text (the user's
///     rewrite when there is one), and whether it is on the one-pager. The key's scope
///     (<see cref="ScoutingReportStarKeys" />) places it under its map and side; a key with no scope prints under
///     its section. A line may carry a picture of its own (a setup heatmap), which the HTML form embeds.
/// </summary>
public sealed class ScoutingReportFinding
{
    public string Key { get; init; } = "";

    public string Section { get; init; } = "";

    public string Text { get; init; } = "";

    public bool IsStarred { get; init; }

    /// <summary>A PNG to print under the line in HTML, or null.</summary>
    public byte[]? ImagePng { get; init; }
}

/// <summary>One roster player as the export's roster line prints them.</summary>
/// <param name="Name">The player.</param>
/// <param name="Role">"AWP", "Entry", or "—".</param>
/// <param name="Rating">The composite rating ("1.14"), or "—" when there are no facts.</param>
public sealed record ScoutingReportRosterEntry(string Name, string Role, string Rating);

/// <summary>
///     A picture of the map stage for one map and side: the radar with the marks of every layer that has a starred
///     line there, on a light background for print.
/// </summary>
/// <param name="Map">The map.</param>
/// <param name="Side">"CT" or "T".</param>
/// <param name="Caption">"de_nuke CT · Setups, AWP": what the picture draws.</param>
/// <param name="Png">The picture; empty when it was not drawn (a Markdown export), which HTML then leaves out.</param>
public sealed record ScoutingReportStageImage(string Map, string Side, string Caption, byte[] Png);

/// <summary>A Scouting Report ready to export: the identity, the roster, the lines not left out, and the stage pictures.</summary>
public sealed class ScoutingReportDocument
{
    public string TeamName { get; init; } = "";

    /// <summary>"18 demos across 4 maps", the Map Pool Record's own line.</summary>
    public string SampleLine { get; init; } = "";

    /// <summary>"Aug 1, 2026 to Nov 8, 2026", the dates the sample spans; empty with no demos.</summary>
    public string DateRangeLine { get; init; } = "";

    /// <summary>"61% of 23 maps (14-9)"; empty prints nothing.</summary>
    public string RecordLine { get; init; } = "";

    /// <summary>"VRS 12 (set 2026-11-01)", one per rank that is set.</summary>
    public IReadOnlyList<string> Ranks { get; init; } = [];

    /// <summary>"11 vs top-30 · 10 other · 2 unknown".</summary>
    public string OpponentMixLine { get; init; } = "";

    /// <summary>"vs Us 1–2"; empty when there is no meeting.</summary>
    public string HeadToHeadLine { get; init; } = "";

    /// <summary>The roster, each with role and rating.</summary>
    public IReadOnlyList<ScoutingReportRosterEntry> Roster { get; init; } = [];

    /// <summary>Where the roster's ratings are counted: "every map" or "de_nuke".</summary>
    public string RosterScope { get; init; } = "";

    public string Summary { get; init; } = "";

    public IReadOnlyList<ScoutingReportFinding> Findings { get; init; } = [];

    /// <summary>The layer ids in registry order: the order lines print in under a side.</summary>
    public IReadOnlyList<string> LayerOrder { get; init; } = [];

    /// <summary>The stage pictures, one per map and side; the one-pager prints them under the side's heading.</summary>
    public IReadOnlyList<ScoutingReportStageImage> Stages { get; init; } = [];
}

/// <summary>
///     The Scouting Report's export: the one-pager (identity, roster, the starred findings, the default) and the
///     long form (the working document), each as self-contained HTML like LAN Print or as Markdown. Pure string
///     builders over <see cref="ScoutingReportDocument" />; the writer below is the only part that touches disk.
///     <para>
///         <b>Order.</b> Both forms print the identity block and the roster line, then the findings by map
///         (in the order the maps first appear), and under each map the CT lines, the T lines, then the
///         map-wide ones; under a side, lines follow the registry's layer order. Lines about the team as a
///         whole (deciders, Period Diff, veto) and the notes follow, under their own sections. A line's buy and
///         AWP window are printed after it, since no heading says them.
///     </para>
///     <para>
///         <b>Ratings.</b> The roster line prints each player's composite rating (spec Q5: the old "no rating,
///         no grade, no win probability" rule is dropped). The exporter still writes no line of its own: every
///         number in the document came from a layer, the players table or the user.
///     </para>
/// </summary>
public static class ScoutingReportExporter
{
    /// <summary>The one-pager's line when nothing is starred.</summary>
    public const string NothingStarredLine =
        "nothing starred yet: star findings in the long form to build the one-pager";

    /// <summary>The heading of a map's lines that are about neither side.</summary>
    public const string MapWideHeading = "Map-wide";

    /// <summary>What the Markdown form prints where the HTML form shows a stage picture.</summary>
    /// <param name="caption">The picture's caption.</param>
    public static string StageOmittedLine(string caption) =>
        $"stage picture ({caption}) is in the HTML export; Markdown carries no pictures";

    /// <summary>The document's title for a form: "Falcons one-pager" or "Falcons scouting report".</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form.</param>
    private static string Title(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string team = doc.TeamName.Length == 0 ? "Opponent" : doc.TeamName;
        return form == ScoutingReportForm.OnePager ? $"{team} one-pager" : $"{team} scouting report";
    }

    /// <summary>The identity lines, label and value: record, ranks, sample, opponents, head to head.</summary>
    /// <param name="doc">The document.</param>
    private static List<(string Label, string Value)> Identity(ScoutingReportDocument doc)
    {
        List<(string, string)> lines = [];
        if (doc.RecordLine.Length > 0)
        {
            lines.Add(("Record", doc.RecordLine));
        }

        if (doc.Ranks.Count > 0)
        {
            lines.Add(("Ranks", string.Join(" · ", doc.Ranks)));
        }

        string sample = string.Join(" · ", new[] { doc.SampleLine, doc.DateRangeLine }.Where(s => s.Length > 0));
        if (sample.Length > 0)
        {
            lines.Add(("Sample", sample));
        }

        if (doc.OpponentMixLine.Length > 0)
        {
            lines.Add(("Opponents", doc.OpponentMixLine));
        }

        if (doc.HeadToHeadLine.Length > 0)
        {
            lines.Add(("Head to head", doc.HeadToHeadLine));
        }

        return lines;
    }

    /// <summary>"Roster (rating over every map)" and "name (AWP, 1.14) · …"; null with no roster.</summary>
    /// <param name="doc">The document.</param>
    private static (string Label, string Value)? RosterLine(ScoutingReportDocument doc)
    {
        if (doc.Roster.Count == 0)
        {
            return null;
        }

        string label = doc.RosterScope.Length > 0 ? $"Roster (role, rating over {doc.RosterScope})" : "Roster (role, rating)";
        return (label, string.Join(" · ", doc.Roster.Select(p => $"{p.Name} ({p.Role}, {p.Rating})")));
    }

    /// <summary>The document as Markdown. Stage pictures are not carried: a line says the HTML export has them.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the one-pager is the default export.</param>
    public static string Markdown(ScoutingReportDocument doc, ScoutingReportForm form = ScoutingReportForm.OnePager) =>
        // Maps and places by their spoken names; the Markdown carries no pictures, so the whole text is prose.
        ScoutingNames.Humanise(RawMarkdown(doc, form));

    private static string RawMarkdown(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        ArgumentNullException.ThrowIfNull(doc);
        StringBuilder md = new();
        md.Append("# ").Append(Title(doc, form)).Append("\n\n");
        List<(string Label, string Value)> identity = Identity(doc);
        if (RosterLine(doc) is { } roster)
        {
            identity.Add(roster);
        }

        foreach ((string label, string value) in identity)
        {
            md.Append("**").Append(label).Append(":** ").Append(OneLine(value)).Append("  \n");
        }

        if (identity.Count > 0)
        {
            md.Append('\n');
        }

        if (doc.Summary.Length > 0)
        {
            md.Append(doc.Summary).Append("\n\n");
        }

        List<Group> groups = Layout(doc, form);
        if (form == ScoutingReportForm.OnePager && groups.Count == 0)
        {
            md.Append('_').Append(NothingStarredLine).Append("_\n");
            return md.ToString();
        }

        foreach (Group group in groups)
        {
            md.Append("## ").Append(group.Title).Append("\n\n");
            foreach (Part part in group.Parts)
            {
                if (part.Title is { } partTitle)
                {
                    md.Append("### ").Append(partTitle).Append("\n\n");
                }

                if (form == ScoutingReportForm.OnePager && part.Stage is { } stage)
                {
                    md.Append('_').Append(StageOmittedLine(stage.Caption)).Append("_\n\n");
                }

                string? layer = null;
                foreach (Placed line in part.Lines)
                {
                    if (form == ScoutingReportForm.LongForm && part.ByLayer && line.Label != layer)
                    {
                        if (layer is not null)
                        {
                            md.Append('\n');
                        }

                        layer = line.Label;
                        md.Append("#### ").Append(layer).Append("\n\n");
                    }

                    md.Append("- ");
                    if (form == ScoutingReportForm.LongForm && line.Finding.IsStarred)
                    {
                        md.Append("★ ");
                    }

                    if (form == ScoutingReportForm.OnePager && part.ByLayer)
                    {
                        md.Append("**").Append(line.Label).Append(":** ");
                    }

                    md.Append(OneLine(line.Finding.Text));
                    if (line.Suffix.Length > 0)
                    {
                        md.Append(" (").Append(line.Suffix).Append(')');
                    }

                    md.Append('\n');
                }

                md.Append('\n');
            }
        }

        return md.ToString();
    }

    /// <summary>
    ///     The document as one self-contained HTML page: no external reference, pictures inlined as data
    ///     URIs, so the file a coach emails renders with nothing else attached (the LAN Print rule).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the one-pager is the default export.</param>
    public static string Html(ScoutingReportDocument doc, ScoutingReportForm form = ScoutingReportForm.OnePager)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string title = Title(doc, form);
        StringBuilder html = new();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>")
            .Append(Escape(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        html.Append("<h1>").Append(Escape(title)).Append("</h1>\n");
        List<(string Label, string Value)> identity = Identity(doc);
        if (identity.Count > 0)
        {
            html.Append("<dl class=\"identity\">\n");
            foreach ((string label, string value) in identity)
            {
                html.Append("<dt>").Append(Escape(label)).Append("</dt><dd>").Append(Escape(value)).Append("</dd>\n");
            }

            html.Append("</dl>\n");
        }

        if (RosterLine(doc) is { } roster)
        {
            html.Append("<p class=\"roster\"><b>").Append(Escape(roster.Label)).Append(":</b> ")
                .Append(Escape(roster.Value)).Append("</p>\n");
        }

        if (doc.Summary.Length > 0)
        {
            foreach (string paragraph in doc.Summary.ReplaceLineEndings("\n")
                         .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                html.Append("<p class=\"summary\">")
                    .Append(Escape(paragraph).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>\n");
            }
        }

        List<Group> groups = Layout(doc, form);
        if (form == ScoutingReportForm.OnePager && groups.Count == 0)
        {
            html.Append("<p class=\"empty\">").Append(Escape(NothingStarredLine)).Append("</p>\n");
        }

        foreach (Group group in groups)
        {
            html.Append("<section>\n<h2>").Append(Escape(group.Title)).Append("</h2>\n");
            foreach (Part part in group.Parts)
            {
                if (part.Title is { } partTitle)
                {
                    html.Append("<h3>").Append(Escape(partTitle)).Append("</h3>\n");
                }

                if (form == ScoutingReportForm.OnePager && part.Stage is { Png.Length: > 0 } stage)
                {
                    html.Append("<figure class=\"stage\"><img alt=\"").Append(Escape(stage.Caption))
                        .Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(stage.Png))
                        .Append("\"><figcaption>").Append(Escape(stage.Caption)).Append("</figcaption></figure>\n");
                }

                string? layer = null;
                bool open = false;
                foreach (Placed line in part.Lines)
                {
                    if (!open || (form == ScoutingReportForm.LongForm && part.ByLayer && line.Label != layer))
                    {
                        if (open)
                        {
                            html.Append("</ul>\n");
                        }

                        if (form == ScoutingReportForm.LongForm && part.ByLayer)
                        {
                            html.Append("<h4>").Append(Escape(line.Label)).Append("</h4>\n");
                        }

                        layer = line.Label;
                        html.Append("<ul>\n");
                        open = true;
                    }

                    html.Append("<li")
                        .Append(form == ScoutingReportForm.LongForm && line.Finding.IsStarred ? " class=\"starred\"" : "")
                        .Append('>');
                    if (form == ScoutingReportForm.OnePager && part.ByLayer)
                    {
                        html.Append("<span class=\"layer\">").Append(Escape(line.Label)).Append(":</span> ");
                    }

                    html.Append(Escape(line.Finding.Text));
                    if (line.Suffix.Length > 0)
                    {
                        html.Append(" <span class=\"scope\">(").Append(Escape(line.Suffix)).Append(")</span>");
                    }

                    if (line.Finding.ImagePng is { Length: > 0 } png)
                    {
                        html.Append("<br><img class=\"line\" alt=\"").Append(Escape(line.Finding.Text))
                            .Append("\" src=\"data:image/png;base64,")
                            .Append(Convert.ToBase64String(png)).Append("\">");
                    }

                    html.Append("</li>\n");
                }

                if (open)
                {
                    html.Append("</ul>\n");
                }
            }

            html.Append("</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    // ---- Layout: the groups both forms print, in order. ----

    /// <summary>A line placed: the finding, its layer label, and the scope words no heading says.</summary>
    private sealed record Placed(ScoutingReportFinding Finding, string Label, string Suffix, int Rank, int Index);

    /// <summary>
    ///     A sub-heading's lines: a side ("CT"), the map-wide lines, or, under a team-wide section, the lines with
    ///     no sub-heading (null title). <see cref="ByLayer" /> is true where lines carry a layer label.
    /// </summary>
    private sealed record Part(string? Title, bool ByLayer, IReadOnlyList<Placed> Lines, ScoutingReportStageImage? Stage);

    /// <summary>A heading's parts: a map, or a team-wide section.</summary>
    private sealed record Group(string Title, IReadOnlyList<Part> Parts);

    /// <summary>
    ///     The groups to print: every map (first-appearance order) with its CT, T and map-wide parts, then each
    ///     team-wide section (first-appearance order). The one-pager keeps only starred lines and drops empty
    ///     groups; the long form keeps every line it is given.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form.</param>
    private static List<Group> Layout(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        List<string> maps = [];
        Dictionary<string, List<(ScoutingScope Scope, Placed Line)>> byMap = new(StringComparer.Ordinal);
        List<string> sections = [];
        Dictionary<string, List<Placed>> bySection = new(StringComparer.Ordinal);

        int index = 0;
        foreach (ScoutingReportFinding finding in doc.Findings)
        {
            index++;
            if (form == ScoutingReportForm.OnePager && !finding.IsStarred)
            {
                continue;
            }

            ScoutingScope? scope = ScoutingReportStarKeys.Parse(finding.Key)?.Scope;
            if (scope is { Map.Length: > 0 })
            {
                int rank = LayerRank(doc.LayerOrder, scope.Layer);
                Placed line = new(finding, LayerLabel(finding.Section), Suffix(scope), rank, index);
                if (!byMap.TryGetValue(scope.Map, out List<(ScoutingScope, Placed)>? list))
                {
                    maps.Add(scope.Map);
                    byMap[scope.Map] = list = [];
                }

                list.Add((scope, line));
            }
            else
            {
                Placed line = new(finding, finding.Section, scope is null ? "" : Suffix(scope), 0, index);
                if (!bySection.TryGetValue(finding.Section, out List<Placed>? list))
                {
                    sections.Add(finding.Section);
                    bySection[finding.Section] = list = [];
                }

                list.Add(line);
            }
        }

        List<Group> groups = [];
        foreach (string map in maps)
        {
            List<Part> parts = [];
            foreach (string side in (string[])["CT", "T", ""])
            {
                List<Placed> lines =
                [
                    .. byMap[map].Where(l => l.Scope.Side == side).Select(l => l.Line)
                        .OrderBy(l => l.Rank).ThenBy(l => l.Index)
                ];
                if (lines.Count == 0)
                {
                    continue;
                }

                ScoutingReportStageImage? stage = side.Length == 0
                    ? null
                    : doc.Stages.FirstOrDefault(s =>
                        string.Equals(s.Map, map, StringComparison.Ordinal) && s.Side == side);
                parts.Add(new Part(side.Length == 0 ? MapWideHeading : side, true, lines, stage));
            }

            groups.Add(new Group(map, parts));
        }

        groups.AddRange(sections.Select(s => new Group(s, [new Part(null, false, bySection[s], null)])));
        return groups;
    }

    // Where a layer sits in the registry's order; a layer the order does not name prints after the rest.
    private static int LayerRank(IReadOnlyList<string> order, string layer)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], layer, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return order.Count;
    }

    // "de_nuke CT · Setups" to "Setups": the map and side are the headings above the line.
    private static string LayerLabel(string section)
    {
        int at = section.LastIndexOf(" · ", StringComparison.Ordinal);
        return at >= 0 ? section[(at + 3)..] : section;
    }

    /// <summary>"Full buy, to 0:30": the scope words of a line that no heading says; empty for All and no window.</summary>
    /// <param name="scope">The line's scope.</param>
    public static string Suffix(ScoutingScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        List<string> parts = [];
        if (scope.Buy.Length > 0 && scope.Buy != "All")
        {
            parts.Add(scope.Buy + " buy");
        }

        if (scope.Window.Length > 0)
        {
            parts.Add(scope.Window == "contact" ? "to first contact" : "to " + scope.Window);
        }

        return string.Join(", ", parts);
    }

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    private const string Css = """
                               * { box-sizing: border-box; }
                               body { font-family: Arial, Helvetica, sans-serif; color: #1a1a1a; background: #fff; margin: 0 auto; padding: 24px 32px; max-width: 900px; }
                               h1 { margin: 0 0 6px; font-size: 22px; }
                               h2 { margin: 20px 0 6px; font-size: 17px; border-bottom: 1px solid #ccc; padding-bottom: 2px; }
                               h3 { margin: 12px 0 4px; font-size: 14px; color: #333; }
                               h4 { margin: 8px 0 2px; font-size: 13px; color: #444; }
                               dl.identity { display: grid; grid-template-columns: max-content 1fr; gap: 2px 12px; margin: 0 0 8px; font-size: 13px; }
                               dl.identity dt { color: #555; }
                               dl.identity dd { margin: 0; }
                               .roster { font-size: 13px; margin: 6px 0 10px; }
                               .summary { font-size: 14px; margin: 8px 0; }
                               .empty { font-size: 13px; color: #777; font-style: italic; }
                               ul { padding-left: 20px; margin: 4px 0; }
                               li { margin: 3px 0; font-size: 13px; }
                               li.starred { font-weight: bold; }
                               li.starred::before { content: "\2605  "; }
                               .layer { font-weight: bold; }
                               .scope { color: #555; }
                               figure.stage { margin: 6px 0 8px; }
                               figure.stage img { width: 100%; max-width: 640px; border: 1px solid #ccc; }
                               figcaption { font-size: 11px; color: #555; }
                               img.line { max-width: 320px; margin: 6px 0; border: 1px solid #ccc; }
                               section, figure { page-break-inside: avoid; }
                               @media print { body { padding: 0; } }
                               """
        + "\n";

    // Every printed string goes through here, so the page names maps and places the way the tab does.
    private static string Escape(string text) => ScoutingNames.Humanise(text)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}

/// <summary>
///     Writing an export to a fresh file under the temp directory and handing it to the OS, the LAN Print
///     route: an exported page is for a coach to read, print or email, and the browser prints it. One file per
///     export: the HTML form inlines its pictures, and the Markdown form carries none.
/// </summary>
public static class ScoutingReportExportWriter
{
    /// <summary>Writes the text to a fresh temp file and returns its path.</summary>
    /// <param name="text">The export.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem; a unique suffix is always added.</param>
    /// <param name="extension">The extension, with its dot.</param>
    private static string WriteTempFile(string text, string fileNameHint, string extension)
    {
        ArgumentNullException.ThrowIfNull(text);
        string path = Path.Combine(Path.GetTempPath(), $"{fileNameHint}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes the export and opens it; the path, or null when the OS launched no handler.</summary>
    /// <param name="text">The export.</param>
    /// <param name="fileNameHint">A short, filesystem-safe stem.</param>
    /// <param name="extension">The extension, with its dot.</param>
    public static string? WriteAndOpen(string text, string fileNameHint, string extension)
    {
        string path = WriteTempFile(text, fileNameHint, extension);
        return OpenExternal.OpenUri(path) ? path : null;
    }
}
