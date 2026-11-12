#region

using DemoViewer.NET.Modules.Library;
using DemoViewer.NET.ViewModels.Library;
using DemoViewer.NET.ViewModels.Transfer;
using static DemoViewer.NET.AppTests.Transfer.TransferWizardFakes;

#endregion

namespace DemoViewer.NET.AppTests.Transfer;

/// <summary>
///     The predicate the transfer picker shares with the Library tab gives the tab's answer: for each
///     search, map and player combination, the tab's filtered set is exactly the entries the predicate keeps.
/// </summary>
[NotInParallel]
public class LibraryFilterPredicateTests
{
    private static DemoEntry[] Library() =>
    [
        Entry("navi-faze.dem", "de_mirage", "s1mple,b1t"),
        Entry("g2-vit.dem", "de_mirage", "m0NESY,ZywOo"),
        Entry("spirit-mouz.dem", "de_nuke", "donk,torzsi"),
        Entry("faze-mouz.dem", "de_dust2", "ropz,torzsi"),
        Entry("unknown.dem", "", "someone")
    ];

    [Test]
    [Arguments("", new string[0], null)]
    [Arguments("faze", new string[0], null)]
    [Arguments("MIRAGE", new string[0], null)]
    [Arguments("torzsi", new string[0], null)]
    [Arguments("", new[] { "Mirage" }, null)]
    [Arguments("", new[] { "mirage", "Nuke" }, null)]
    [Arguments("mouz", new[] { "Nuke", "Dust2" }, null)]
    [Arguments("", new string[0], "torzsi")]
    [Arguments("dust", new string[0], "TORZSI")]
    [Arguments("  zywoo  ", new[] { "Mirage" }, "ZywOo")]
    public async Task TheLibraryTab_AndThePredicate_KeepTheSameDemos(string search, string[] maps, string? player)
    {
        using DemoLibraryService svc = new(Inline,
            Path.Combine(Path.GetTempPath(), "dvlibpred_" + Guid.NewGuid().ToString("N") + ".json"));
        LibraryTabViewModel tab = new(svc, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]));
        foreach (DemoEntry entry in Library())
        {
            svc.Entries.Add(entry);
        }

        tab.SearchText = search;
        foreach (MapFilterItem chip in tab.MapFilters)
        {
            chip.IsSelected = maps.Contains(chip.Display, StringComparer.OrdinalIgnoreCase);
        }

        if (player is not null)
        {
            tab.SelectedPlayer = tab.AvailablePlayers.First(p => string.Equals(p, player, StringComparison.OrdinalIgnoreCase));
        }

        LibraryFilterPredicate predicate = new(search,
            maps.ToHashSet(StringComparer.Ordinal), player);
        string[] expected = [.. svc.Entries.Where(predicate.Matches).Select(e => e.FileName).Order(StringComparer.Ordinal)];
        string[] shown = [.. tab.FilteredEntries.Select(e => e.FileName).Order(StringComparer.Ordinal)];

        await Assert.That(shown).IsEquivalentTo(expected);
        await Assert.That(predicate.IsEmpty).IsEqualTo(string.IsNullOrWhiteSpace(search) && maps.Length == 0 && player is null);
    }

    private sealed class TeamContribution : ILibraryContribution
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public LibraryFilter Filter { get; } = new("Team",
        [
            new LibraryFilterItem("", "All teams"), new LibraryFilterItem("faze", "FaZe"),
            new LibraryFilterItem("mouz", "MOUZ")
        ], (demo, key) => demo.FileName.Contains(key, StringComparison.OrdinalIgnoreCase));

        LibraryFilter? ILibraryContribution.Filter => Filter;
        public bool HasBadge => false;
        public IReadOnlyList<string> BadgeLabels => [];
        public string? BadgeResetLabel => null;
        public LibraryBadge? BadgeFor(LibraryDemo demo) => null;

        public void SetLabel(LibraryDemo demo, string? label)
        {
        }
    }

    [Test]
    [Arguments("", "")]
    [Arguments("faze", "")]
    [Arguments("mouz", "")]
    [Arguments("mouz", "nuke")]
    public async Task ATeamPicked_InTheLibraryTab_AndInTheExportPicker_KeepsTheSameDemos(string team, string search)
    {
        using DemoLibraryService svc = new(Inline,
            Path.Combine(Path.GetTempPath(), "dvlibpred_" + Guid.NewGuid().ToString("N") + ".json"));
        TeamContribution contribution = new();
        LibraryTabViewModel tab = new(svc, _ => Task.CompletedTask, () => Task.FromResult<IReadOnlyList<string>>([]),
            contributions: [contribution]);
        foreach (DemoEntry entry in Library())
        {
            svc.Entries.Add(entry);
        }

        LibraryFilterViewModel filter = tab.Filters.Single();
        filter.Selected = filter.Items.Single(i => i.Key == team);
        tab.SearchText = search;
        ExportWizardViewModel picker = new(new FakeExporter(), () => [.. svc.Entries], Inline,
            contributedFilters: tab.ContributedFilters);
        foreach (TransferFilterChip chip in picker.ContributedFilters.Single().Chips)
        {
            chip.IsSelected = chip.Key == team;
        }

        picker.SearchText = search;
        LibraryFilterPredicate predicate = new(search, new HashSet<string>(), null,
            [new ContributedFilterChoice(contribution.Filter, [team])]);
        string[] expected = [.. svc.Entries.Where(predicate.Matches).Select(e => e.FileName).Order(StringComparer.Ordinal)];

        using (Assert.Multiple())
        {
            await Assert.That(tab.FilteredEntries.Select(e => e.FileName).Order(StringComparer.Ordinal))
                .IsEquivalentTo(expected);
            await Assert.That(picker.FilteredRows.Select(r => r.DisplayName).Order(StringComparer.Ordinal))
                .IsEquivalentTo(expected);
            await Assert.That(predicate.IsEmpty).IsEqualTo(team.Length == 0 && search.Length == 0);
        }
    }

    [Test]
    public async Task MapGroups_LeaveOutUnknown_AndSortByDisplayName()
    {
        List<(string Display, string MapKey)> groups = LibraryFilterPredicate.MapGroups(Library());

        await Assert.That(groups.Select(g => g.Display)).IsEquivalentTo(["Dust2", "Mirage", "Nuke"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(groups[1].MapKey).IsEqualTo("de_mirage");
    }

    [Test]
    public async Task None_MatchesEverything()
    {
        await Assert.That(Library().All(LibraryFilterPredicate.None.Matches)).IsTrue();
        await Assert.That(LibraryFilterPredicate.None.IsEmpty).IsTrue();
    }
}
