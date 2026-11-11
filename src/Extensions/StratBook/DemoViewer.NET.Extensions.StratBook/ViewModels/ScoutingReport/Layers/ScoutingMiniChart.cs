namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

/// <summary>
///     A strip mini's compact chart, drawn under the map in place of its text lines. Plain records: the layer
///     builds one off the UI thread, and the view picks a template by its type. The inspector stays the place where
///     numbers open their rounds; the chart is the glance.
/// </summary>
public abstract record ScoutingMiniChart
{
    /// <summary>One dim line under the chart saying how to read it.</summary>
    public string Caption { get; init; } = "";
}

/// <summary>One segment of a 100% stacked bar.</summary>
/// <param name="Label">What the segment counts: "Eco", "Force".</param>
/// <param name="Share">Its share of the bar, 0 to 1.</param>
/// <param name="Step">Its place on the one-hue ordinal ramp, 0 (lightest) to <see cref="ScoutingStackSegment.Steps" /> - 1.</param>
public sealed record ScoutingStackSegment(string Label, double Share, int Step)
{
    /// <summary>The ramp's length: eco, semi, force, full.</summary>
    public const int Steps = 4;
}

/// <summary>One row of a stacked-bar chart.</summary>
/// <param name="Label">The row: "Lost pistol → R2".</param>
/// <param name="Segments">The segments, ramp order.</param>
/// <param name="Trailing">The number at the end of the row: "won 17%".</param>
public sealed record ScoutingStackRow(string Label, IReadOnlyList<ScoutingStackSegment> Segments, string Trailing)
{
    /// <summary>"Eco 61% · Force 30%": the row as text, for a tooltip and the accessible name.</summary>
    public string Text => string.Join(" · ", Segments.Select(s => $"{s.Label} {ScoutingLayerBase.Percent(s.Share)}"));
}

/// <summary>Rows of 100% stacked bars on one ordinal ramp.</summary>
/// <param name="Rows">The rows.</param>
public sealed record ScoutingStackChart(IReadOnlyList<ScoutingStackRow> Rows) : ScoutingMiniChart;

/// <summary>One dot of a strip plot.</summary>
/// <param name="Value">Its position on the axis.</param>
/// <param name="Filled">Filled (a won round) or a ring (a lost one).</param>
public sealed record ScoutingStripDot(double Value, bool Filled);

/// <summary>A shaded band behind a strip plot's lanes: "fast" from 0 to 35 s.</summary>
/// <param name="From">Where it starts on the axis.</param>
/// <param name="To">Where it ends.</param>
public sealed record ScoutingStripBand(double From, double To);

/// <summary>One lane of a strip plot: its dots and its median, on an axis from 0 to <see cref="Max" />.</summary>
/// <param name="Label">The lane: "A".</param>
/// <param name="Dots">The dots.</param>
/// <param name="Median">The median line, or null with no dot.</param>
/// <param name="Max">The axis end; a dot past it is drawn at the end.</param>
/// <param name="Bands">The shaded bands.</param>
/// <param name="Color">The dots' colour.</param>
public sealed record ScoutingStripLane(
    string Label,
    IReadOnlyList<ScoutingStripDot> Dots,
    double? Median,
    double Max,
    IReadOnlyList<ScoutingStripBand> Bands,
    ScoutingColor Color)
{
    /// <summary>"A: 12 rounds, median 0:52", for the accessible name.</summary>
    public string Text { get; init; } = "";
}

/// <summary>A strip plot, one lane per site.</summary>
/// <param name="Lanes">The lanes.</param>
public sealed record ScoutingStripChart(IReadOnlyList<ScoutingStripLane> Lanes) : ScoutingMiniChart;

/// <summary>One dumbbell: a ring at <see cref="Low" />, a dot at <see cref="High" />, on 0 to 100.</summary>
/// <param name="Label">The row: "CT".</param>
/// <param name="Color">The side colour.</param>
/// <param name="Low">The ring's value (after losing the opening), or null with no sample.</param>
/// <param name="High">The dot's value (after winning it), or null.</param>
/// <param name="Value">The number at the end: "31% → 78%".</param>
public sealed record ScoutingDumbbell(string Label, ScoutingColor Color, double? Low, double? High, string Value);

/// <summary>Dumbbell rows on a 0–100 axis.</summary>
/// <param name="Rows">The rows.</param>
public sealed record ScoutingDumbbellChart(IReadOnlyList<ScoutingDumbbell> Rows) : ScoutingMiniChart;

/// <summary>One row of a small table.</summary>
/// <param name="Label">The row's name.</param>
/// <param name="Cells">One value per column.</param>
public sealed record ScoutingTableRow(string Label, IReadOnlyList<string> Cells)
{
    /// <summary>The first cell, or empty.</summary>
    public string First => Cells.Count > 0 ? Cells[0] : "";

    /// <summary>The second cell, or empty.</summary>
    public string Second => Cells.Count > 1 ? Cells[1] : "";
}

/// <summary>A small table with two value columns.</summary>
/// <param name="First">The first column's heading: "CT".</param>
/// <param name="Second">The second's: "T".</param>
/// <param name="Rows">The rows.</param>
public sealed record ScoutingTableChart(string First, string Second, IReadOnlyList<ScoutingTableRow> Rows)
    : ScoutingMiniChart;
