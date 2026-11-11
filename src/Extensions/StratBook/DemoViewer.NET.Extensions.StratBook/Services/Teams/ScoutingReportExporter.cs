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
///     (<see cref="ScoutingReportStarKeys" />) places it under its map and side, and on the card that shows its layer
///     there; a key with no scope prints under its section. A layer's row also carries its label, value and the
///     inspector section it sits in, so a card prints it in a table the way the inspector shows it.
/// </summary>
public sealed class ScoutingReportFinding
{
    public string Key { get; init; } = "";

    public string Section { get; init; } = "";

    public string Text { get; init; } = "";

    public bool IsStarred { get; init; }

    /// <summary>A layer's row: its inspector section's raw title ("" when untitled); null for a sentence.</summary>
    public string? Group { get; init; }

    /// <summary>A row's label as the card shows it: "CT Spawn · p0".</summary>
    public string Label { get; init; } = "";

    /// <summary>A row's number: "30 of 30 rounds".</summary>
    public string Value { get; init; } = "";

    /// <summary>The label a read is starred under ("read", "rotations"): which card leads with it.</summary>
    public string StarText { get; init; } = "";

    /// <summary>The user rewrote it: a rewritten row prints as its sentence.</summary>
    public bool IsEdited { get; init; }

    /// <summary>A row that prints in a card's table: not a sentence, not rewritten.</summary>
    public bool IsRow => Group is not null && !IsEdited;
}

/// <summary>One share of a card: a layer, and which of its inspector sections and reads the card shows (all when null).</summary>
/// <param name="LayerId">The layer id.</param>
/// <param name="Sections">Keeps a section by its raw title.</param>
/// <param name="Reads">Keeps a read by the label it is starred under.</param>
public sealed record ScoutingReportCardPart(
    string LayerId,
    Func<string, bool>? Sections = null,
    Func<string, bool>? Reads = null);

/// <summary>
///     One card of the export, one layer's: the side it sits under ("CT", "T", or "" for the
///     map-wide cards), its title, the layers it reads, the layer it pictures on the radar, and whether it spans
///     the page.
/// </summary>
/// <param name="Side">"CT", "T", or "".</param>
/// <param name="Title">"Starting setups".</param>
/// <param name="Parts">The layers it reads.</param>
/// <param name="MapLayerId">The layer whose marks it pictures, or null.</param>
/// <param name="IsWide">Spans both columns.</param>
public sealed record ScoutingReportCard(
    string Side,
    string Title,
    IReadOnlyList<ScoutingReportCardPart> Parts,
    string? MapLayerId = null,
    bool IsWide = false)
{
    /// <summary>Whether the card shows a line of this layer: a row by its section, a sentence by its star label.</summary>
    /// <param name="layer">The line's layer.</param>
    /// <param name="finding">The line.</param>
    public bool Shows(string layer, ScoutingReportFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return Parts.Any(p => string.Equals(p.LayerId, layer, StringComparison.Ordinal)
                              && (finding.IsRow
                                  ? p.Sections?.Invoke(finding.Group!) ?? true
                                  : p.Reads?.Invoke(finding.StarText) ?? true));
    }
}

/// <summary>One roster player as the export's roster line prints them.</summary>
/// <param name="Name">The player.</param>
/// <param name="Role">"AWP", "Entry", or "—".</param>
/// <param name="Rating">The composite rating ("1.14"), or "—" when there are no facts.</param>
/// <param name="Swatch">The colour their marks draw in on the pictures, as "#rrggbb"; empty prints no dot.</param>
public sealed record ScoutingReportRosterEntry(string Name, string Role, string Rating, string Swatch = "");

/// <summary>A card's picture: the radar with one layer's marks for a map and side, on a light background for print.</summary>
/// <param name="Caption">"Nuke CT · Setups": what the picture draws.</param>
/// <param name="Png">The picture.</param>
public sealed record ScoutingReportPicture(string Caption, byte[] Png);

/// <summary>
///     One map and side's row of pictures printed above its cards: the one-pager's radar with the setups, AWP
///     routes and utility together, once per buy.
/// </summary>
/// <param name="Map">The map.</param>
/// <param name="Side">"CT" or "T".</param>
/// <param name="Pictures">The pictures, left to right.</param>
public sealed record ScoutingReportStageRow(string Map, string Side, IReadOnlyList<ScoutingReportPicture> Pictures);

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

    /// <summary>The cards in print order: each side's layers.</summary>
    public IReadOnlyList<ScoutingReportCard> Cards { get; init; } = [];

    /// <summary>
    ///     Draws a card's picture for a map, a side ("CT" or "T") and a layer, or null when it has none; asked only
    ///     for a card that prints. Null on the document draws no pictures (Markdown).
    /// </summary>
    public Func<string, string, string, ScoutingReportPicture?>? Picture { get; init; }

    /// <summary>
    ///     Picture rows printed under a map's side heading before its cards, in map order; a map or side with a row
    ///     prints even when no line lands on it. Markdown leaves them out.
    /// </summary>
    public IReadOnlyList<ScoutingReportStageRow> Stages { get; init; } = [];
}

/// <summary>
///     The Scouting Report's export: the one-pager (identity, roster, the starred findings, the default) and the
///     long form (the working document), each as self-contained HTML like LAN Print or as Markdown. Pure string
///     builders over <see cref="ScoutingReportDocument" />; the writer below is the only part that touches disk.
///     <para>
///         <b>Laid out by layer.</b> The identity as tiles and the roster as a table, then each map (in
///         the order the maps first appear): its record, then under each side its picture row and the cards for
///         that side, one per layer, each leading with its sentences, then its picture, then its rows as tables
///         under their section titles. A line lands on its layer's card; one no card claims lands on a card named
///         for its section, so nothing is dropped. Lines about the team as a whole (deciders, Period Diff, veto) and the notes follow.
///         The one-pager keeps the starred lines (and the cards holding them); the long form keeps every line,
///         starred ones marked. A line's buy and AWP window are printed after it, since no heading says them.
///     </para>
///     <para>
///         <b>Ratings.</b> The roster prints each player's composite rating (spec Q5: the old "no rating, no grade,
///         no win probability" rule is dropped). The exporter still writes no line of its own: every number in the
///         document came from a layer, the players table or the user.
///     </para>
/// </summary>
public static class ScoutingReportExporter
{
    /// <summary>The one-pager's line when nothing is starred.</summary>
    public const string NothingStarredLine =
        "nothing starred yet: star findings in the long form to build the one-pager";

    /// <summary>The heading of a map's CT or T cards: "CT side".</summary>
    /// <param name="side">"CT" or "T".</param>
    public static string SideHeading(string side) => $"{side} side";

    /// <summary>The document's title for a form: "Falcons one-pager" or "Falcons scouting report".</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form.</param>
    private static string Title(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        ArgumentNullException.ThrowIfNull(doc);
        string team = doc.TeamName.Length == 0 ? "Opponent" : doc.TeamName;
        return form == ScoutingReportForm.OnePager ? $"{team} one-pager" : $"{team} scouting report";
    }

    /// <summary>The identity tiles, label and value: record, ranks, sample, opponents, head to head.</summary>
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

    // "Roster · role, rating over Nuke".
    private static string RosterHeading(ScoutingReportDocument doc) =>
        doc.RosterScope.Length > 0 ? $"Roster · role, rating over {doc.RosterScope}" : "Roster · role, rating";

    /// <summary>The document as Markdown: the same cards as the HTML, with no pictures.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the one-pager is the default export.</param>
    public static string Markdown(ScoutingReportDocument doc, ScoutingReportForm form = ScoutingReportForm.OnePager) =>
        // Maps and places by their spoken names; the Markdown carries no pictures, so the whole text is prose.
        ScoutingNames.Humanise(RawMarkdown(doc, form));

    private static string RawMarkdown(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        ArgumentNullException.ThrowIfNull(doc);
        bool longForm = form == ScoutingReportForm.LongForm;
        StringBuilder md = new();
        md.Append("# ").Append(Title(doc, form)).Append("\n\n");
        List<(string Label, string Value)> identity = Identity(doc);
        foreach ((string label, string value) in identity)
        {
            md.Append("**").Append(label).Append(":** ").Append(OneLine(value)).Append("  \n");
        }

        if (identity.Count > 0)
        {
            md.Append('\n');
        }

        if (doc.Roster.Count > 0)
        {
            md.Append("**").Append(RosterHeading(doc)).Append("**\n\n| Player | Role | Rating |\n|---|---|---:|\n");
            foreach (ScoutingReportRosterEntry p in doc.Roster)
            {
                md.Append("| ").Append(Cell(p.Name)).Append(" | ").Append(Cell(p.Role)).Append(" | ")
                    .Append(Cell(p.Rating)).Append(" |\n");
            }

            md.Append('\n');
        }

        if (doc.Summary.Length > 0)
        {
            md.Append(doc.Summary).Append("\n\n");
        }

        (List<MapOut> maps, List<CardOut> team) = Layout(doc, form);
        if (form == ScoutingReportForm.OnePager && maps.Count == 0 && team.Count == 0)
        {
            md.Append('_').Append(NothingStarredLine).Append("_\n");
            return md.ToString();
        }

        foreach (MapOut map in maps)
        {
            md.Append("## ").Append(map.Map).Append("\n\n");
            foreach (SideOut side in map.Sides.Where(s => s.Cards.Count > 0))
            {
                if (side.Heading is { } heading)
                {
                    md.Append("### ").Append(heading).Append("\n\n");
                }

                foreach (CardOut card in side.Cards)
                {
                    AppendMarkdownCard(md, card, "####", longForm);
                }
            }
        }

        foreach (CardOut card in team)
        {
            AppendMarkdownCard(md, card, "##", longForm);
        }

        return md.ToString();
    }

    private static void AppendMarkdownCard(StringBuilder md, CardOut card, string level, bool longForm)
    {
        md.Append(level).Append(' ').Append(card.Title).Append("\n\n");
        foreach (CardLine line in card.Reads)
        {
            md.Append("- ").Append(longForm && line.Finding.IsStarred ? "★ " : "").Append(OneLine(line.Finding.Text));
            if (line.Suffix.Length > 0)
            {
                md.Append(" (").Append(line.Suffix).Append(')');
            }

            md.Append('\n');
        }

        if (card.Reads.Count > 0)
        {
            md.Append('\n');
        }

        foreach ((string group, List<CardLine> rows) in card.Groups)
        {
            if (group.Length > 0)
            {
                md.Append("**").Append(group).Append("**\n\n");
            }

            md.Append("| | |\n|---|---:|\n");
            foreach (CardLine row in rows)
            {
                string label = (longForm && row.Finding.IsStarred ? "★ " : "") + row.Finding.Label
                               + (row.Suffix.Length > 0 ? $" ({row.Suffix})" : "");
                md.Append("| ").Append(Cell(label)).Append(" | ").Append(Cell(row.Finding.Value)).Append(" |\n");
            }

            md.Append('\n');
        }
    }

    private static string Cell(string text) => OneLine(text).Replace("|", "\\|", StringComparison.Ordinal);

    /// <summary>
    ///     The document as one self-contained HTML page: no external reference, pictures inlined as data
    ///     URIs, so the file a coach emails renders with nothing else attached (the LAN Print rule).
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form; the one-pager is the default export.</param>
    public static string Html(ScoutingReportDocument doc, ScoutingReportForm form = ScoutingReportForm.OnePager)
    {
        ArgumentNullException.ThrowIfNull(doc);
        bool longForm = form == ScoutingReportForm.LongForm;
        string title = Title(doc, form);
        StringBuilder html = new();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>")
            .Append(Escape(title)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n");
        html.Append("<header>\n<h1>").Append(Escape(title)).Append("</h1>\n");
        List<(string Label, string Value)> identity = Identity(doc);
        if (identity.Count > 0)
        {
            html.Append("<div class=\"tiles\">\n");
            foreach ((string label, string value) in identity)
            {
                html.Append("<div class=\"tile\"><div class=\"k\">").Append(Escape(label))
                    .Append("</div><div class=\"v\">").Append(Escape(value)).Append("</div></div>\n");
            }

            html.Append("</div>\n");
        }

        if (doc.Roster.Count > 0)
        {
            html.Append("<div class=\"card roster\">\n<h4>").Append(Escape(RosterHeading(doc))).Append("</h4>\n")
                .Append("<table class=\"rows\"><thead><tr><th>Player</th><th>Role</th><th class=\"v\">Rating</th></tr></thead><tbody>\n");
            foreach (ScoutingReportRosterEntry p in doc.Roster)
            {
                html.Append("<tr><td>");
                if (p.Swatch.Length > 0)
                {
                    html.Append("<span class=\"dot\" style=\"background:").Append(Escape(p.Swatch)).Append("\"></span>");
                }

                html.Append(Escape(p.Name)).Append("</td><td>").Append(Escape(p.Role))
                    .Append("</td><td class=\"v\">").Append(Escape(p.Rating)).Append("</td></tr>\n");
            }

            html.Append("</tbody></table>\n</div>\n");
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

        html.Append("</header>\n");
        (List<MapOut> maps, List<CardOut> team) = Layout(doc, form);
        if (form == ScoutingReportForm.OnePager && maps.Count == 0 && team.Count == 0)
        {
            html.Append("<p class=\"empty\">").Append(Escape(NothingStarredLine)).Append("</p>\n");
        }

        foreach (MapOut map in maps)
        {
            html.Append("<section class=\"map\">\n<h2>").Append(Escape(map.Map)).Append("</h2>\n");
            foreach (SideOut side in map.Sides)
            {
                if (side.Heading is { } heading)
                {
                    html.Append("<h3 class=\"side").Append(side.Side == "CT" ? " ct" : " t").Append("\">")
                        .Append(Escape(heading)).Append("</h3>\n");
                }

                if (side.Stage is { Pictures.Count: > 0 } stage)
                {
                    html.Append("<div class=\"stages\">\n");
                    foreach (ScoutingReportPicture picture in stage.Pictures)
                    {
                        html.Append("<figure><img alt=\"").Append(Escape(picture.Caption))
                            .Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(picture.Png))
                            .Append("\"><figcaption>").Append(Escape(picture.Caption)).Append("</figcaption></figure>\n");
                    }

                    html.Append("</div>\n");
                }

                if (side.Cards.Count == 0)
                {
                    continue;
                }

                html.Append("<div class=\"grid\">\n");
                foreach (CardOut card in side.Cards)
                {
                    ScoutingReportPicture? picture = card.MapLayerId is { } layer && doc.Picture is { } draw
                        ? draw(map.Map, side.Side, layer)
                        : null;
                    AppendHtmlCard(html, card, picture, longForm);
                }

                html.Append("</div>\n");
            }

            html.Append("</section>\n");
        }

        if (team.Count > 0)
        {
            html.Append("<section class=\"map\">\n<h2>Team</h2>\n<div class=\"grid\">\n");
            foreach (CardOut card in team)
            {
                AppendHtmlCard(html, card, null, longForm);
            }

            html.Append("</div>\n</section>\n");
        }

        html.Append("</body>\n</html>\n");
        return html.ToString();
    }

    private static void AppendHtmlCard(StringBuilder html, CardOut card, ScoutingReportPicture? picture, bool longForm)
    {
        bool split = card.IsWide && picture is { Png.Length: > 0 } && card.Groups.Count > 0;
        html.Append("<div class=\"card").Append(card.IsWide ? " wide" : "").Append("\">\n<h4>")
            .Append(Escape(card.Title)).Append("</h4>\n");
        foreach (CardLine line in card.Reads)
        {
            html.Append("<p class=\"read").Append(longForm && line.Finding.IsStarred ? " starred" : "").Append("\">")
                .Append(Escape(line.Finding.Text));
            if (line.Suffix.Length > 0)
            {
                html.Append(" <span class=\"scope\">(").Append(Escape(line.Suffix)).Append(")</span>");
            }

            html.Append("</p>\n");
        }

        if (split)
        {
            html.Append("<div class=\"split\">\n");
        }

        if (picture is { Png.Length: > 0 })
        {
            html.Append("<figure><img alt=\"").Append(Escape(picture.Caption))
                .Append("\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(picture.Png))
                .Append("\"><figcaption>").Append(Escape(picture.Caption)).Append("</figcaption></figure>\n");
        }

        if (card.Groups.Count > 0)
        {
            html.Append("<div class=\"tables\">\n");
            foreach ((string group, List<CardLine> rows) in card.Groups)
            {
                if (group.Length > 0)
                {
                    html.Append("<div class=\"group\">").Append(Escape(group)).Append("</div>\n");
                }

                html.Append("<table class=\"rows\"><tbody>\n");
                foreach (CardLine row in rows)
                {
                    html.Append("<tr").Append(longForm && row.Finding.IsStarred ? " class=\"starred\"" : "")
                        .Append("><td>").Append(Escape(row.Finding.Label));
                    if (row.Suffix.Length > 0)
                    {
                        html.Append(" <span class=\"scope\">(").Append(Escape(row.Suffix)).Append(")</span>");
                    }

                    html.Append("</td><td class=\"v\">").Append(Escape(row.Finding.Value)).Append("</td></tr>\n");
                }

                html.Append("</tbody></table>\n");
            }

            html.Append("</div>\n");
        }

        if (split)
        {
            html.Append("</div>\n");
        }

        html.Append("</div>\n");
    }

    // ---- Layout: the maps, sides and cards both forms print, in order. ----

    /// <summary>A line placed on a card: the finding and the scope words no heading says.</summary>
    private sealed record CardLine(ScoutingReportFinding Finding, string Suffix);

    /// <summary>A card as it prints: its sentences, then its rows by section title in the order they came.</summary>
    private sealed class CardOut(string title, string? mapLayerId, bool isWide)
    {
        public string Title { get; } = title;

        public string? MapLayerId { get; } = mapLayerId;

        public bool IsWide { get; } = isWide;

        public List<CardLine> Reads { get; } = [];

        public List<(string Group, List<CardLine> Rows)> Groups { get; } = [];

        public bool IsEmpty => Reads.Count == 0 && Groups.Count == 0;

        public void Add(CardLine line)
        {
            if (!line.Finding.IsRow)
            {
                Reads.Add(line);
                return;
            }

            string group = line.Finding.Group!;
            int at = Groups.FindIndex(g => string.Equals(g.Group, group, StringComparison.Ordinal));
            if (at < 0)
            {
                Groups.Add((group, [line]));
            }
            else
            {
                Groups[at].Rows.Add(line);
            }
        }
    }

    /// <summary>One map and side's cards: the document's (by index), the fallbacks by layer, and the map's record.</summary>
    private sealed class SideSlot(int cards)
    {
        public CardOut?[] Cards { get; } = new CardOut?[cards];

        public List<CardOut> Extra { get; } = [];

        public CardOut? Record { get; set; }
    }

    /// <summary>One side's cards under a map ("CT side", "T side"), or the map's record with no heading; its picture row first.</summary>
    private sealed record SideOut(string Side, string? Heading, List<CardOut> Cards, ScoutingReportStageRow? Stage = null);

    private sealed record MapOut(string Map, List<SideOut> Sides);

    /// <summary>
    ///     The maps to print (first-appearance order), each with its CT, T and map-wide cards in print order and
    ///     the cards holding no line dropped, then the team-wide sections as cards. The one-pager keeps only starred
    ///     lines; the long form keeps every line it is given.
    /// </summary>
    /// <param name="doc">The document.</param>
    /// <param name="form">The form.</param>
    private static (List<MapOut> Maps, List<CardOut> Team) Layout(ScoutingReportDocument doc, ScoutingReportForm form)
    {
        // A map with a picture row prints first, in the rows' order, then the maps the lines name.
        List<string> maps = [.. doc.Stages.Select(s => s.Map).Distinct(StringComparer.Ordinal)];
        Dictionary<(string Map, string Side), SideSlot> placed = [];
        List<CardOut> team = [];

        foreach (ScoutingReportFinding finding in doc.Findings)
        {
            if (form == ScoutingReportForm.OnePager && !finding.IsStarred)
            {
                continue;
            }

            ScoutingScope? scope = ScoutingReportStarKeys.Parse(finding.Key)?.Scope;
            if (scope is not { Map.Length: > 0 })
            {
                CardOut? section = team.Find(c => string.Equals(c.Title, finding.Section, StringComparison.Ordinal));
                if (section is null)
                {
                    team.Add(section = new CardOut(finding.Section, null, false));
                }

                section.Add(new CardLine(finding, scope is null ? "" : Suffix(scope)));
                continue;
            }

            if (!maps.Contains(scope.Map, StringComparer.Ordinal))
            {
                maps.Add(scope.Map);
            }

            if (!placed.TryGetValue((scope.Map, scope.Side), out SideSlot? slot))
            {
                placed[(scope.Map, scope.Side)] = slot = new SideSlot(doc.Cards.Count);
            }

            CardLine line = new(finding, Suffix(scope));
            if (scope is { Side.Length: 0, Layer: "pool" })
            {
                slot.Record ??= new CardOut("Map record", null, false);
                slot.Record.Add(line);
            }
            else if (FirstCard(doc.Cards, scope, finding) is { } index)
            {
                ScoutingReportCard spec = doc.Cards[index];
                (slot.Cards[index] ??= new CardOut(spec.Title, spec.MapLayerId, spec.IsWide)).Add(line);
            }
            else
            {
                string label = LayerLabel(finding.Section);
                CardOut? extra = slot.Extra.Find(c => string.Equals(c.Title, label, StringComparison.Ordinal));
                if (extra is null)
                {
                    slot.Extra.Add(extra = new CardOut(label, null, false));
                }

                extra.Add(line);
            }
        }

        List<MapOut> result = [];
        foreach (string map in maps)
        {
            List<SideOut> sides = [];
            foreach (string side in (string[])["", "CT", "T"])
            {
                ScoutingReportStageRow? stage = doc.Stages.FirstOrDefault(s =>
                    s.Pictures.Count > 0 && string.Equals(s.Map, map, StringComparison.Ordinal)
                                         && string.Equals(s.Side, side, StringComparison.Ordinal));
                List<CardOut> cards = [];
                if (placed.TryGetValue((map, side), out SideSlot? slot))
                {
                    if (slot.Record is { } record)
                    {
                        cards.Add(record);
                    }

                    cards.AddRange(slot.Cards.OfType<CardOut>().Where(c => !c.IsEmpty));
                    cards.AddRange(slot.Extra);
                }

                if (cards.Count > 0 || stage is not null)
                {
                    sides.Add(new SideOut(side, side.Length == 0 ? null : SideHeading(side), cards, stage));
                }
            }

            result.Add(new MapOut(map, sides));
        }

        return (result, team);
    }

    // The first card on the line's side that shows it; a map-wide card ("" side) takes either side's lines.
    private static int? FirstCard(IReadOnlyList<ScoutingReportCard> cards, ScoutingScope scope, ScoutingReportFinding finding)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if ((cards[i].Side.Length == 0 || string.Equals(cards[i].Side, scope.Side, StringComparison.Ordinal))
                && cards[i].Shows(scope.Layer, finding))
            {
                return i;
            }
        }

        return null;
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
                               body { font-family: "Segoe UI", Arial, Helvetica, sans-serif; color: #1a1a1a; background: #fff; margin: 0 auto; padding: 24px 28px; max-width: 1120px; }
                               h1 { margin: 0 0 10px; font-size: 24px; }
                               h2 { margin: 26px 0 8px; font-size: 20px; border-bottom: 2px solid #1a1a1a; padding-bottom: 3px; }
                               h3.side { margin: 16px 0 8px; font-size: 15px; display: flex; align-items: center; gap: 8px; }
                               h3.side::before { content: ""; width: 10px; height: 10px; border-radius: 2px; background: #888; }
                               h3.side.ct::before { background: #4a7fd6; }
                               h3.side.t::before { background: #d39a2c; }
                               h4 { margin: 0 0 6px; font-size: 14px; }
                               .tiles { display: flex; flex-wrap: wrap; gap: 8px; margin: 0 0 12px; }
                               .tile { border: 1px solid #d6d6d6; border-radius: 6px; padding: 6px 10px; min-width: 150px; }
                               .tile .k { font-size: 11px; color: #555; }
                               .tile .v { font-size: 14px; font-weight: 600; }
                               .summary { font-size: 14px; margin: 8px 0; }
                               .empty { font-size: 13px; color: #777; font-style: italic; }
                               .grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
                               .card { border: 1px solid #d6d6d6; border-radius: 8px; padding: 10px 12px; break-inside: avoid; page-break-inside: avoid; }
                               .card.wide { grid-column: 1 / -1; }
                               .card.roster { max-width: 560px; margin: 0 0 10px; }
                               .read { margin: 2px 0 6px; font-size: 13px; font-weight: 600; }
                               .starred::before, tr.starred td:first-child::before { content: "\2605  "; color: #b88a00; }
                               .scope { color: #666; font-weight: normal; }
                               figure { margin: 6px 0 8px; text-align: center; }
                               figure img { width: 100%; max-width: 520px; border-radius: 6px; border: 1px solid #ccc; }
                               figcaption { font-size: 11px; color: #555; }
                               .split { display: grid; grid-template-columns: 3fr 2fr; gap: 14px; align-items: start; }
                               .stages { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px; margin: 0 0 12px; break-inside: avoid; page-break-inside: avoid; }
                               .stages figure { margin: 0; }
                               .stages figure img { max-width: 100%; }
                               .split figure img { max-width: 100%; }
                               .dot { display: inline-block; width: 9px; height: 9px; border-radius: 50%; margin-right: 6px; vertical-align: 0; print-color-adjust: exact; -webkit-print-color-adjust: exact; }
                               .group { font-size: 12px; font-weight: 600; color: #444; margin: 8px 0 2px; }
                               table.rows { width: 100%; border-collapse: collapse; font-size: 12px; }
                               table.rows th { text-align: left; font-size: 11px; color: #555; font-weight: 600; padding: 2px 4px; }
                               table.rows td { border-top: 1px solid #ececec; padding: 3px 4px; vertical-align: top; }
                               table.rows .v { text-align: right; white-space: nowrap; }
                               @media (max-width: 760px) { .grid, .split, .stages { grid-template-columns: 1fr; } }
                               @media print { body { padding: 0; max-width: none; } h2, h3 { break-after: avoid; } }
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
