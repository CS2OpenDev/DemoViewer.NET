#region

using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>One team in the Scouting Report's picker.</summary>
public sealed class ScoutingReportTeamRow(Team team)
{
    public Guid Id { get; } = team.Id;

    public string Name { get; } = DisplayText.Sanitize(team.Name);

    public bool IsUs { get; } = team.IsUs;

    /// <summary>Whether <paramref name="other" /> shows the same team the same way.</summary>
    public bool SameAs(ScoutingReportTeamRow other) =>
        Id == other.Id && IsUs == other.IsUs && string.Equals(Name, other.Name, StringComparison.Ordinal);

    public override string ToString() => IsUs ? $"{Name} (us)" : Name;
}

/// <summary>
///     One map of the selected team's pool, worded for the map tabs, the pool table and the long form. The CT and
///     T columns are the team's own round win % (<see cref="TeamMapFacts" />), filled in when Round Facts land.
/// </summary>
public sealed partial class MapPoolRowViewModel : ObservableObject
{
    /// <summary>A map with this many maps played or fewer is a thin sample.</summary>
    public const int ThinSample = 2;

    [ObservableProperty]
    private string _ctLabel = "…";

    [ObservableProperty]
    private string _ctToolTip = "reading Round Facts";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The team-scoped columns are still being read: the pool draws skeleton bars in them.</summary>
    [ObservableProperty]
    private bool _isReading = true;

    [ObservableProperty]
    private string _pistolsLabel = "…";

    [ObservableProperty]
    private string _plantLabel = "…";

    [ObservableProperty]
    private string _tLabel = "…";

    [ObservableProperty]
    private string _tToolTip = "reading Round Facts";

    /// <param name="row">The Map Pool Record's row.</param>
    /// <param name="lastPlayed">The newest demo on the map, or null.</param>
    /// <param name="now">Today, for "5d ago".</param>
    public MapPoolRowViewModel(MapPoolMapRow row, DateTime? lastPlayed, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(row);
        Map = row.Map;
        MapLabel = ScoutingNames.Map(row.Map);
        Played = row.Played;
        PlayedLabel = $"{row.Played} played";
        RecordLabel = row.Wins + row.Losses == 0
            ? "no resolved score"
            : $"{row.Wins}-{row.Losses}" + (row.Undetermined > 0 ? $" ({row.Undetermined} undetermined)" : "");
        WinLossLabel = $"{row.Wins}–{row.Losses}";
        WinRateLabel = row.WinRate is { } wr ? ScoutingLayerBase.Percent(wr) : "—";
        LastPlayedLabel = lastPlayed is { } last ? Ago(last, now) : "—";
        LastPlayedShort = lastPlayed is { } l ? Ago(l, now).Replace(" ago", "", StringComparison.Ordinal) : "—";
    }

    public string Map { get; }

    /// <summary>The map as people say it: "Mirage".</summary>
    public string MapLabel { get; }

    public int Played { get; }

    /// <summary>"6 played".</summary>
    public string PlayedLabel { get; }

    /// <summary>"4-2", with undetermined maps named.</summary>
    public string RecordLabel { get; }

    /// <summary>"4–2".</summary>
    public string WinLossLabel { get; }

    /// <summary>"67%".</summary>
    public string WinRateLabel { get; }

    /// <summary>"5d ago".</summary>
    public string LastPlayedLabel { get; }

    /// <summary>"5d", the map tab's form.</summary>
    public string LastPlayedShort { get; }

    /// <summary>Two maps or fewer.</summary>
    public bool IsThin => Played <= ThinSample;

    /// <summary>The map tab's sub-line: "6 · 5d".</summary>
    public string TabDetail => $"{Played} · {LastPlayedShort}";

    /// <summary>"82%": the non-pistol rounds the team bought an AWP in, from the round captures' purchases.</summary>
    [ObservableProperty]
    private string _awpLabel = "…";

    [ObservableProperty]
    private string _awpToolTip = "reading round captures";

    /// <summary>The long form's line for the map.</summary>
    public string FindingText =>
        $"{Map}: {PlayedLabel}, {RecordLabel}, win rate {WinRateLabel}, CT rounds {CtLabel}, T rounds {TLabel}";

    /// <summary>Fills the team-scoped columns from Round Facts; null says they are not available.</summary>
    /// <param name="facts">The map's facts, or null.</param>
    /// <param name="available">False when the host has no Round Facts at all.</param>
    public void SetFacts(TeamMapFacts? facts, bool available)
    {
        IsReading = false;
        if (facts is null)
        {
            CtLabel = TLabel = PistolsLabel = PlantLabel = AwpLabel = "—";
            CtToolTip = TToolTip = AwpToolTip =
                available ? "no rounds with Round Facts on this map" : "Round Facts are unavailable";
            return;
        }

        CtLabel = Share(facts.CtWon, facts.CtPlayed);
        TLabel = Share(facts.TWon, facts.TPlayed);
        CtToolTip = $"won {facts.CtWon} of {ScoutingLayerBase.Plural(facts.CtPlayed, "CT round")} on their own CT side";
        TToolTip = $"won {facts.TWon} of {ScoutingLayerBase.Plural(facts.TPlayed, "T round")} on their own T side";
        PistolsLabel = facts.PistolsPlayed == 0 ? "—" : $"{facts.PistolsWon} of {facts.PistolsPlayed}";
        PlantLabel = facts.PlantMedianSeconds is { } s ? ScoutingLayerBase.Clock(s) : "—";
        AwpLabel = Share(facts.AwpBought, facts.AwpRoundsRead);
        AwpToolTip = facts.AwpRoundsRead == 0
            ? "no round captures on this map; rebuild the round index"
            : $"an AWP bought in {facts.AwpBought} of {ScoutingLayerBase.Plural(facts.AwpRoundsRead, "non-pistol round")}, both sides";
    }

    private static string Share(int won, int played) =>
        played == 0 ? "—" : ScoutingLayerBase.Percent((double)won / played);

    /// <summary>"today", "5d ago", "3mo ago".</summary>
    /// <param name="when">The date.</param>
    /// <param name="now">Today.</param>
    public static string Ago(DateTime when, DateTime now)
    {
        int days = (int)Math.Floor((now - when).TotalDays);
        return days switch
        {
            < 1 => "today",
            < 60 => $"{days}d ago",
            _ => $"{days / 30}mo ago"
        };
    }
}

/// <summary>One demo on the header's sample timeline: filled when won, a ring when lost; the selected map bright.</summary>
/// <param name="X">Left edge in pixels inside the strip.</param>
/// <param name="Y">Top edge in pixels inside the strip.</param>
/// <param name="Won">Won.</param>
/// <param name="IsSelectedMap">On the map in view.</param>
/// <param name="ToolTip">"de_nuke · 2026-03-11 · won".</param>
/// <param name="TopTier">The opponent was ranked 30 or better then; the dot is drawn larger.</param>
public sealed record TimelineDotViewModel(double X, double Y, bool Won, bool IsSelectedMap, string ToolTip,
    bool TopTier = false)
{
    /// <summary>The dot's diameter: larger against a top-30 opponent.</summary>
    public double Size => TopTier ? 11 : 7;

    /// <summary>The strip's width in pixels.</summary>
    public const double Width = 220;

    public double Opacity => IsSelectedMap ? 1 : 0.3;
}

/// <summary>One demo of the team's match history, newest first: when, where, against whom, and how it ended.</summary>
/// <param name="DateLabel">"2026-03-11".</param>
/// <param name="MapLabel">"Nuke".</param>
/// <param name="Opponent">The other team's name, or "unknown opponent".</param>
/// <param name="ResultLabel">"Won", "Lost", or "—" when the score did not resolve.</param>
/// <param name="Won">The team won; null when the score did not resolve.</param>
/// <param name="ScoreLabel">"13–9" from the team's side, or "—".</param>
/// <param name="TierLabel">"top 30", "below 30", or "" when the opponent's rank is unknown.</param>
public sealed record MatchHistoryRowViewModel(
    string DateLabel,
    string MapLabel,
    string Opponent,
    string ResultLabel,
    bool? Won,
    string ScoreLabel,
    string TierLabel)
{
    public bool IsWin => Won == true;

    public bool IsLoss => Won == false;

    public bool IsUnresolved => Won is null;
}

/// <summary>One player of the team's current roster, with the role the demos suggest and why, and the numbers.</summary>
public sealed class PlayerRowViewModel
{
    public required string Name { get; init; }

    /// <summary>The SteamID64 as text: the members table's key, and the Setups layer's isolate key.</summary>
    public string SteamId { get; init; } = "";

    public required ScoutingColor Color { get; init; }

    /// <summary>"AWP", "Entry", "Lurk", "Anchor", "Support", or "—" when nothing points to a role; " · IGL" when picked.</summary>
    public string Role { get; init; } = "—";

    /// <summary>Why the role: "41% of AWP kills".</summary>
    public string RoleNote { get; init; } = "";

    public bool HasRoleNote => RoleNote.Length > 0;

    /// <summary>"entry A", "lurk", "support".</summary>
    public string TRole { get; init; } = "—";

    /// <summary>The place the player held most in the CT setup window on the map in view: "B site".</summary>
    public string CtSpot { get; init; } = "—";

    /// <summary>"held in 47 of 50 CT rounds (all buys)", or why there is no spot.</summary>
    public string CtSpotNote { get; init; } = "no CT setup positions for this player on this map";

    /// <summary>The player's CT setup is shown alone on the map.</summary>
    public bool IsIsolated { get; init; }

    /// <summary>"Show CT setup", or "Show all" while isolated.</summary>
    public string IsolateLabel => IsIsolated ? "Show all" : "Show CT setup";

    /// <summary>Isolates the player's CT setup on the map, or puts everyone back.</summary>
    public System.Windows.Input.ICommand? IsolateCommand { get; init; }

    public bool CanIsolate => IsolateCommand is not null && SteamId.Length > 0;

    /// <summary>"12 demos".</summary>
    public string Appearances { get; init; } = "";

    /// <summary>Opens the Player Overview narrowed to this team's demos; null without one.</summary>
    public System.Windows.Input.ICommand? OpenOverviewCommand { get; init; }

    public string OverviewToolTip { get; init; } = "";

    public PlayerStatCellViewModel Rating { get; init; } = PlayerStatCellViewModel.Missing("—", "");

    public PlayerStatCellViewModel KillDeath { get; init; } = PlayerStatCellViewModel.Missing("—", "");

    public PlayerStatCellViewModel Adr { get; init; } = PlayerStatCellViewModel.Missing("—", "");

    public PlayerStatCellViewModel Kast { get; init; } = PlayerStatCellViewModel.Missing("—", "");

    public PlayerStatCellViewModel Opening { get; init; } = PlayerStatCellViewModel.Missing("—", "");

    public PlayerStatCellViewModel Clutch { get; init; } = PlayerStatCellViewModel.Missing("—", "");
}

/// <summary>One number of the players table: its text, why (or why it is empty), and what it opens.</summary>
/// <param name="Label">"1.14", "31–22", "4 of 19", or "—".</param>
/// <param name="ToolTip">What the number counts, or what fills an empty one.</param>
/// <param name="Command">Opens the rounds or the demos behind it; null when there is nothing to open.</param>
/// <param name="Detail">"4 of 6 demos" while the facts are partial; empty otherwise.</param>
public sealed record PlayerStatCellViewModel(
    string Label,
    string ToolTip,
    System.Windows.Input.ICommand? Command = null,
    string Detail = "")
{
    public bool CanOpen => Command is not null;

    public bool HasDetail => Detail.Length > 0;

    /// <summary>Still being read: the table draws a skeleton bar in its place.</summary>
    public bool IsLoading { get; init; }

    public bool ShowsPlainLabel => !CanOpen && !IsLoading;

    /// <summary>A number still being read.</summary>
    public static PlayerStatCellViewModel Reading { get; } = new("…", "reading player stats") { IsLoading = true };

    /// <summary>A number with nothing to open.</summary>
    /// <param name="label">"—".</param>
    /// <param name="toolTip">What would fill it.</param>
    /// <param name="detail">"4 of 6 demos", or empty.</param>
    public static PlayerStatCellViewModel Missing(string label, string toolTip, string detail = "") =>
        new(label, toolTip, null, detail);
}

/// <summary>One choice of the IGL picker: a roster player, or nobody (an empty SteamID).</summary>
/// <param name="SteamId">The SteamID64 as text, or empty.</param>
/// <param name="Label">"IGL: name".</param>
public sealed record IglOptionViewModel(string SteamId, string Label)
{
    public override string ToString() => Label;
}
