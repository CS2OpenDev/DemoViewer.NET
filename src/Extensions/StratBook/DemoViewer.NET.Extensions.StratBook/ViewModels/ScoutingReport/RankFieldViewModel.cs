#region

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoViewer.NET.Extensions.StratBook.Services.Teams;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.ViewModels.ScoutingReport;

/// <summary>
///     The inline editor for one ranking (VRS or HLTV) of the team in view. The text is committed on Enter or
///     when the box loses focus: empty clears the current rank, a positive whole number sets it as of today
///     (older entries stay as history), anything else is refused with a message and the old rank stays.
/// </summary>
public sealed partial class RankFieldViewModel : ObservableObject
{
    /// <summary>What the box says when the text is not a rank.</summary>
    public const string InvalidMessage = "Use a positive whole number, or leave it empty";

    private readonly Func<DateTime> _now;
    private readonly TeamRanksStore _store;
    private Guid? _teamId;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _setLine = "";

    [ObservableProperty]
    private string _text = "";

    public RankFieldViewModel(TeamRanksStore store, RankSystem system, Func<DateTime> now)
    {
        _store = store;
        System = system;
        _now = now;
    }

    public RankSystem System { get; }

    /// <summary>"VRS" or "HLTV".</summary>
    public string Label => System == RankSystem.Vrs ? "VRS" : "HLTV";

    public string AutomationName => $"{Label} rank";

    public bool HasError => Error.Length > 0;

    /// <summary>Points the editor at a team (null for none) and loads its current rank.</summary>
    /// <param name="teamId">The team in view.</param>
    public void SetTeam(Guid? teamId)
    {
        _teamId = teamId;
        Refresh();
    }

    /// <summary>Re-reads the current rank from the store, dropping what was typed.</summary>
    public void Refresh()
    {
        RankEntry? current = _teamId is { } id ? _store.Current(id, System) : null;
        Text = current?.Rank.ToString(CultureInfo.InvariantCulture) ?? "";
        SetLine = current is null ? "" : $"set {current.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
        Error = "";
    }

    [RelayCommand]
    private void Revert() => Refresh();

    /// <summary>Applies the text: the Enter key and the lost focus both end here.</summary>
    [RelayCommand]
    public void Commit()
    {
        if (_teamId is not { } id)
        {
            return;
        }

        if (!TeamRanksStore.TryParse(Text, out int? rank))
        {
            Error = InvalidMessage;
            return;
        }

        RankEntry? current = _store.Current(id, System);
        if (rank is null)
        {
            if (current is not null)
            {
                _store.ClearCurrent(id, System);
            }
        }
        else if (current?.Rank != rank)
        {
            _store.Set(id, System, rank.Value, DateOnly.FromDateTime(_now()));
        }

        Refresh();
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
}
