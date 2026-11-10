#region

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Modules.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;
using DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;
using DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport.Layers;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The players table: the roster with a suggested role and its evidence, the CT spot, the T role, and the
///     numbers (<see cref="PlayerStatsService" />) on the map in view or over every map. The rates read the
///     library's <c>player_match</c> facts, which fill in the background; a demo they have not reached yet is
///     counted out loud ("4 of 6 demos"). The IGL is the user's pick, kept in the notes store.
/// </summary>
public sealed partial class ScoutingReportTabViewModel
{
    // The facts status per team demo the players load read under; a library change that moves one reloads.
    private Dictionary<string, FactStatus> _factStatuses = new(StringComparer.OrdinalIgnoreCase);
    private bool _projectingIgl;
    private PlayerStatsSet? _playerStats;
    private int _playersGeneration;

    /// <summary>The players' numbers over every map instead of the map in view.</summary>
    [ObservableProperty]
    private bool _playersAllMaps;

    /// <summary>"Player stats cover 4 of 6 demos …", or empty when every demo is in.</summary>
    [ObservableProperty]
    private string _playersStatusLine = "";

    [ObservableProperty]
    private IglOptionViewModel? _selectedIgl;

    /// <summary>"Nobody" and the roster: the IGL picker.</summary>
    public ObservableCollection<IglOptionViewModel> IglOptions { get; } = [];

    public bool HasPlayersStatus => PlayersStatusLine.Length > 0;

    /// <summary>"de_nuke" or "All maps": the stats scope toggle's caption.</summary>
    public string PlayersScopeLabel => PlayersAllMaps ? "All maps" : SelectedMap?.MapLabel ?? "This map";

    partial void OnPlayersAllMapsChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayersTitle));
        OnPropertyChanged(nameof(PlayersScopeLabel));
        ProjectPlayers();
    }

    partial void OnPlayersStatusLineChanged(string value) => OnPropertyChanged(nameof(HasPlayersStatus));

    partial void OnSelectedIglChanged(IglOptionViewModel? value)
    {
        if (_projectingIgl || SelectedTeam is not { } team || value is null)
        {
            return;
        }

        _notes.SetIgl(team.Id, value.SteamId.Length == 0 ? null : value.SteamId);
        ProjectPlayers();
    }

    [RelayCommand]
    private void TogglePlayersScope() => PlayersAllMaps = !PlayersAllMaps;

    private void ResetPlayerStats()
    {
        Interlocked.Increment(ref _playersGeneration);
        _playerStats = null;
        _factStatuses = new Dictionary<string, FactStatus>(StringComparer.OrdinalIgnoreCase);
        PlayersStatusLine = "";
    }

    // Builds the roster's numbers on a worker, then re-projects the table on the UI thread.
    private Task StartPlayerStats(ScoutingReportData data)
    {
        ResetPlayerStats();
        int generation = Volatile.Read(ref _playersGeneration);
        IReadOnlyList<string> roster = [.. ScoutingPlayerColors.Roster(_teams, data.TeamId).Select(p => p.SteamId)];
        if (roster.Count == 0)
        {
            return Task.CompletedTask;
        }

        IAnalysisFacts? facts = _sources.AnalysisFacts;
        foreach (TeamDemoFacts demo in data.Demos)
        {
            _factStatuses[demo.Path] = facts?.Status(demo.Path, PlayerStatsService.PlayerMatch) ?? FactStatus.Absent;
        }

        PlayerStatsSet? built = null;
        Task run = _runSection("players")(() =>
        {
            try
            {
                built = PlayerStatsService.Build(_library, facts, _sources.RoundFacts, data.Demos, roster);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                built = null;
            }
        });

        void Take()
        {
            if (_disposed || generation != Volatile.Read(ref _playersGeneration) || !ReferenceEquals(data, _data))
            {
                return;
            }

            _playerStats = built;
            ProjectPlayers();
        }

        if (run.IsCompleted)
        {
            Take();
            return Task.CompletedTask;
        }

        return run.ContinueWith(_ => _post(Take), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // A demo's player stats landing (or going stale) reloads the numbers alone, not the whole report.
    private void OnLibraryChangedForPlayers(LibraryChange change)
    {
        if (_disposed || _data is not { } data || _sources.AnalysisFacts is not { } facts || _factStatuses.Count == 0)
        {
            return;
        }

        bool moved = change.Path is { } path
            ? _factStatuses.TryGetValue(path, out FactStatus had) && facts.Status(path, PlayerStatsService.PlayerMatch) != had
            : _factStatuses.Any(p => facts.Status(p.Key, PlayerStatsService.PlayerMatch) != p.Value);
        if (moved)
        {
            LoadTask = StartPlayerStats(data);
        }
    }

    // The current roster's players, most appearances first, with the role the demos suggest on the map in view,
    // the CT spot the setups held most there, and the numbers on that map (or every map).
    private void ProjectPlayers()
    {
        Players.Clear();
        if (SelectedTeam is not { } row)
        {
            ProjectIglOptions([], null);
            return;
        }

        IReadOnlyList<ScoutingRosterPlayer> roster = ScoutingPlayerColors.Roster(_teams, row.Id);
        if (_isolated is not null && roster.All(p => p.SteamId != _isolated))
        {
            SetIsolated(null);
        }

        string? igl = _notes.For(row.Id).Igl;
        ProjectIglOptions(roster, igl);
        if (roster.Count == 0)
        {
            PlayersStatusLine = "";
            return;
        }

        string mapName = SelectedMap?.Map ?? "";
        OpeningTendencies? t = SelectedMap is { } map && _data is { } data && Landed(data.Openings) is { } set
            ? set.Blocks.FirstOrDefault(b => b.Map == map.Map && b.Side == 2)
            : null;
        Dictionary<string, (int Count, string Site)> entries = new(StringComparer.Ordinal);
        foreach (TendencyBucket bucket in t?.EntryBySite ?? [])
        {
            int at = bucket.Label.LastIndexOf(" at ", StringComparison.Ordinal);
            string name = at > 0 ? bucket.Label[..at] : bucket.Label;
            string site = at > 0 ? bucket.Label[(at + 4)..] : "";
            entries.TryGetValue(name, out (int Count, string Site) had);
            entries[name] = (had.Count + bucket.Count, had.Count >= bucket.Count ? had.Site : site);
        }

        Dictionary<string, int> lurks = t?.Lurkers.ToDictionary(b => b.Label, b => b.Count, StringComparer.Ordinal)
                                        ?? new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<string, SetupPlayerPlace> spots = CtSpots();
        string spotBuy = Buy == ScoutingBuy.All ? "all buys" : $"{Buy.ToString().ToLowerInvariant()} buy";
        PlayerStatsSet? stats = _playerStats;
        string statsMap = PlayersAllMaps ? PlayerStatsSet.AllMaps : mapName;
        string where = PlayersAllMaps ? "every map" : mapName;

        // Roles read the map in view, whatever the stats scope: the CT spot and the T evidence are per map.
        List<PlayerRoleEvidence> evidence = [];
        foreach (ScoutingRosterPlayer player in roster)
        {
            string name = DisplayText.Sanitize(player.Member.LastName);
            PlayerStatLine? line = stats?.For(mapName, player.SteamId);
            SetupPlayerPlace? spot = spots.GetValueOrDefault(player.SteamId);
            (int Count, string Site) entry = entries.GetValueOrDefault(name);
            evidence.Add(new PlayerRoleEvidence(player.SteamId, line?.AwpKills ?? 0,
                line?.Openings.Count(o => o.Side == 2) ?? 0, entry.Count, entry.Site ?? "", lurks.GetValueOrDefault(name),
                spot?.Place, spot?.Rounds.Count ?? 0, spot?.Of ?? 0));
        }

        PlayerRoleTotals totals = new(stats?.TeamAwpKills(mapName) ?? 0, evidence.Sum(e => e.TOpenings),
            t?.LurkRounds ?? 0);
        IReadOnlyDictionary<string, PlayerRoleSuggestion> roles = PlayerStatsRoles.Suggest(evidence, totals, igl);

        int demosInScope = 0, withFacts = 0, pending = 0, failed = 0;
        foreach (ScoutingRosterPlayer player in roster)
        {
            string name = DisplayText.Sanitize(player.Member.LastName);
            PlayerStatLine? line = stats?.For(statsMap, player.SteamId);
            if (line is not null)
            {
                demosInScope = Math.Max(demosInScope, line.Demos);
                withFacts = Math.Max(withFacts, line.WithFacts);
                pending = Math.Max(pending, line.Pending);
                failed = Math.Max(failed, line.Failed);
            }

            SetupPlayerPlace? spot = spots.GetValueOrDefault(player.SteamId);
            PlayerRoleSuggestion role = roles[player.SteamId];
            PlayerStatCells cells = Cells(stats, line, name, player.SteamId, where, stats?.TeamAwpKills(statsMap) ?? 0);
            Players.Add(new PlayerRowViewModel
            {
                Name = name,
                SteamId = player.SteamId,
                Color = player.Color,
                Role = role.Role,
                RoleNote = role.Note,
                TRole = role.TRole,
                CtSpot = spot is null ? "—" : ScoutingNames.Place(spot.Place),
                CtSpotNote = spot is null
                    ? "no CT setup positions for this player on this map"
                    : $"held in {spot.Rounds.Count} of {ScoutingLayerBase.Plural(spot.Of, "CT round")} ({spotBuy})",
                IsIsolated = _isolated == player.SteamId,
                IsolateCommand = IsolatePlayerCommand,
                Appearances = ScoutingLayerBase.Plural(player.Member.Count, "demo"),
                Rating = cells.Rating,
                KillDeath = cells.KillDeath,
                Adr = cells.Adr,
                Kast = cells.Kast,
                Opening = cells.Opening,
                Clutch = cells.Clutch,
                OpenOverviewCommand = OverviewCommand(player.SteamId, $"with {row.Name}",
                    stats?.For(PlayerStatsSet.AllMaps, player.SteamId)?.DemoKeys),
                OverviewToolTip = _openPlayerOverview is null
                    ? "this host has no Player Overview"
                    : $"Opens {name} in the Player Overview, narrowed to this team's demos"
            });
        }

        PlayersStatusLine = StatusLine(stats, demosInScope, withFacts, pending, failed, where);
    }

    private void ProjectIglOptions(IReadOnlyList<ScoutingRosterPlayer> roster, string? igl)
    {
        _projectingIgl = true;
        try
        {
            IglOptions.Clear();
            IglOptions.Add(new IglOptionViewModel("", "IGL: not set"));
            foreach (ScoutingRosterPlayer player in roster)
            {
                IglOptions.Add(new IglOptionViewModel(player.SteamId, "IGL: " + DisplayText.Sanitize(player.Member.LastName)));
            }

            SelectedIgl = IglOptions.FirstOrDefault(o => o.SteamId == (igl ?? "")) ?? IglOptions[0];
        }
        finally
        {
            _projectingIgl = false;
        }
    }

    // "Player stats cover 4 of 6 demos on de_nuke. The library is computing the rest in the background."
    private static string StatusLine(PlayerStatsSet? stats, int demos, int withFacts, int pending, int failed,
        string where)
    {
        if (stats is null)
        {
            return "";
        }

        if (!stats.FactsAvailable)
        {
            return "This host has no player stats: rating, ADR and KAST stay empty. Opening duels and 1vX read Round Facts.";
        }

        if (demos == 0 || withFacts >= demos)
        {
            return "";
        }

        string line = $"Player stats cover {withFacts} of {ScoutingLayerBase.Plural(demos, "demo")} on {where}.";
        if (pending > 0)
        {
            line += $" {ScoutingLayerBase.Plural(pending, "demo")} still to compute: the library does it in the background and the table fills in.";
        }

        if (failed > 0)
        {
            line += $" The player stats ruleset failed on {ScoutingLayerBase.Plural(failed, "demo")}; re-analyse them from the Library.";
        }

        return line;
    }

    private sealed record PlayerStatCells(
        PlayerStatCellViewModel Rating,
        PlayerStatCellViewModel KillDeath,
        PlayerStatCellViewModel Adr,
        PlayerStatCellViewModel Kast,
        PlayerStatCellViewModel Opening,
        PlayerStatCellViewModel Clutch);

    private PlayerStatCells Cells(PlayerStatsSet? stats, PlayerStatLine? line, string name, string steamId,
        string where, int teamAwp)
    {
        if (stats is null)
        {
            PlayerStatCellViewModel reading = PlayerStatCellViewModel.Reading;
            return new PlayerStatCells(reading, reading, reading, reading, reading, reading);
        }

        if (line is null || line.Demos == 0)
        {
            PlayerStatCellViewModel none =
                PlayerStatCellViewModel.Missing("—", $"no demo of this team on {where} holds {name}");
            return new PlayerStatCells(none, none, none, none, none, none);
        }

        string coverage = line.WithFacts >= line.Demos ? "" : $"{line.WithFacts} of {line.Demos} demos";
        string why = RateMissing(stats, line);
        string from = coverage.Length > 0 ? $" Over {coverage}: the rest have no player stats yet." : "";
        System.Windows.Input.ICommand? overview = OverviewCommand(steamId,
            PlayersAllMaps ? $"with {SelectedTeam?.Name}" : $"with {SelectedTeam?.Name} on {where}", line.DemoKeys);
        string opens = overview is null ? "" : " Opens the player's demos here in the Player Overview.";

        PlayerStatCellViewModel rating = line.Rating is { } r
            ? new PlayerStatCellViewModel(r.ToString("0.00", CultureInfo.InvariantCulture),
                $"HLTV 2.0 rating, rounds-weighted over {ScoutingLayerBase.Plural((int)line.Rounds, "round")} on {where}.{from}{opens}",
                overview, coverage)
            : PlayerStatCellViewModel.Missing("—", why, coverage);
        PlayerStatCellViewModel adr = line.Adr is { } a
            ? new PlayerStatCellViewModel(a.ToString("0.0", CultureInfo.InvariantCulture),
                $"enemy damage per round on {where}.{from}{opens}", overview)
            : PlayerStatCellViewModel.Missing("—", why);
        PlayerStatCellViewModel kast = line.Kast is { } k
            ? new PlayerStatCellViewModel(ScoutingLayerBase.Percent(k),
                $"rounds with a kill, assist, survival or trade on {where}.{from}{opens}", overview)
            : PlayerStatCellViewModel.Missing("—", why);

        string awp = teamAwp > 0 && line.WithFacts > 0
            ? $" AWP: {line.AwpKills} kills, {ScoutingLayerBase.Percent((double)line.AwpKills / teamAwp)} of the team's."
            : "";
        string fallback = line.KdFromRoundFacts > 0
            ? $" Kills from Round Facts on {ScoutingLayerBase.Plural(line.KdFromRoundFacts, "demo")} without player stats."
            : "";
        PlayerStatCellViewModel kd = line.KillDeath is { } kdv
            ? new PlayerStatCellViewModel(kdv.ToString("0.00", CultureInfo.InvariantCulture),
                $"{line.Kills} kills, {line.Deaths} deaths on {where}.{awp}{fallback}{opens}", overview)
            : PlayerStatCellViewModel.Missing("—", stats.RoundFactsAvailable || stats.FactsAvailable
                ? $"no kill or death counted on {where} yet. {why}"
                : "this host has neither player stats nor Round Facts");

        PlayerStatCellViewModel opening = OpeningCell(stats, line, name, where);
        PlayerStatCellViewModel clutch = ClutchCell(stats, line, name, where);
        return new PlayerStatCells(rating, kd, adr, kast, opening, clutch);
    }

    // Why a rate is empty, worded so the user knows what fills it.
    private static string RateMissing(PlayerStatsSet stats, PlayerStatLine line)
    {
        if (!stats.FactsAvailable)
        {
            return "this host has no player stats (analysis facts)";
        }

        if (line.Pending > 0)
        {
            return $"player stats are not computed yet for {ScoutingLayerBase.Plural(line.Pending, "demo")}; the library computes them in the background and this fills in";
        }

        return line.Failed > 0
            ? $"the player stats ruleset failed on {ScoutingLayerBase.Plural(line.Failed, "demo")}; re-analyse them from the Library"
            : "no player stats row for this player in these demos";
    }

    private PlayerStatCellViewModel OpeningCell(PlayerStatsSet stats, PlayerStatLine line, string name, string where)
    {
        if (!stats.RoundFactsAvailable)
        {
            return PlayerStatCellViewModel.Missing("—", "opening duels read Round Facts, which this host does not have");
        }

        if (line.Openings.Count == 0)
        {
            return PlayerStatCellViewModel.Missing("0–0", $"{name} took no opening duel on {where}");
        }

        int won = line.Openings.Count(o => o.Won);
        int lost = line.Openings.Count - won;
        string Split(int side) =>
            $"{line.Openings.Count(o => o.Side == side && o.Won)}–{line.Openings.Count(o => o.Side == side && !o.Won)}";
        IReadOnlyList<TendencyRound> rounds = [.. line.Openings.Select(o => o.Round)];
        string title = $"{where} · {name} opening duels";
        return new PlayerStatCellViewModel($"{won}–{lost}",
            $"opening kills–deaths on {where}: CT {Split(3)}, T {Split(2)}. Opens the duels in Review.",
            new RelayCommand(() => ReviewLine = _opener.Open(SelectedTeam?.Name ?? "", SelectedTeam?.Id, rounds, title)));
    }

    private PlayerStatCellViewModel ClutchCell(PlayerStatsSet stats, PlayerStatLine line, string name, string where)
    {
        if (!stats.RoundFactsAvailable)
        {
            return PlayerStatCellViewModel.Missing("—", "1vX reads Round Facts, which this host does not have");
        }

        if (line.Clutches.Count == 0)
        {
            return PlayerStatCellViewModel.Missing("0 of 0",
                $"{name} was never the last alive of the side with an enemy standing on {where}");
        }

        int won = line.Clutches.Count(c => c.Won);
        string split = string.Join(", ", line.Clutches.GroupBy(c => c.Versus).OrderBy(g => g.Key)
            .Select(g => $"1v{g.Key} {g.Count(c => c.Won)} of {g.Count()}"));
        IReadOnlyList<TendencyRound> rounds = [.. line.Clutches.Select(c => c.Round)];
        string title = $"{where} · {name} 1vX";
        return new PlayerStatCellViewModel($"{won} of {line.Clutches.Count}",
            $"won {won} of {ScoutingLayerBase.Plural(line.Clutches.Count, "round")} left the last alive of the side with an enemy standing ({split}). Opens them in Review.",
            new RelayCommand(() => ReviewLine = _opener.Open(SelectedTeam?.Name ?? "", SelectedTeam?.Id, rounds, title)));
    }

    // Opens the Player Overview on the player, narrowed to the demos; null without a host or a SteamID.
    private RelayCommand? OverviewCommand(string steamId, string label, IReadOnlyList<string>? demos)
    {
        if (_openPlayerOverview is not { } open
            || !ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0)
        {
            return null;
        }

        IReadOnlyCollection<string> scope = demos is { Count: > 0 }
            ? [.. demos]
            : [.. (_data?.Demos ?? []).Select(d => _library.Find(d.Path)?.Sha256 ?? d.Path)];
        return new RelayCommand(() =>
        {
            if (!open(id, new PlayerOverviewScope(label, scope)))
            {
                ReviewLine = "the Player Overview could not open on this host";
            }
        });
    }

    // Each player's most-held CT setup place on the map in view, at the buy in view; empty until the setups land.
    private Dictionary<string, SetupPlayerPlace> CtSpots()
    {
        if (SelectedMap is not { } map || _data is not { } data || Landed(data.Heatmaps) is not { } set)
        {
            return new Dictionary<string, SetupPlayerPlace>(StringComparer.Ordinal);
        }

        IEnumerable<SetupHeatmapRound> rounds = set.Heatmaps
            .Where(h => string.Equals(h.Map, map.Map, StringComparison.OrdinalIgnoreCase)
                        && (Buy == ScoutingBuy.All || h.Buy.ToString() == Buy.ToString()))
            .SelectMany(h => h.Rounds);
        return SetupsDerivation.PlacesByPlayer(rounds).Where(p => p.IsMain)
            .ToDictionary(p => p.Key, StringComparer.Ordinal);
    }
}
