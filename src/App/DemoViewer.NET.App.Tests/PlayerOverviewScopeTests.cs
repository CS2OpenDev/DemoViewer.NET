#region

using System.Globalization;
using DemoViewer.NET.Configuration;
using DemoViewer.NET.Services.DemoCache;
using DemoViewer.NET.Services.Identity;
using DemoViewer.NET.Services.Players;
using DemoViewer.NET.ViewModels.PlayerOverview;
using DemoViewer.NET.ViewModels.PlayerOverview.Sections;
using TUnit.Assertions.Enums;

#endregion

namespace DemoViewer.NET.AppTests;

/// <summary>
///     The Player Overview narrowed to a <see cref="PlayerOverviewScope" />: history, summary and filter options read
///     only the scoped demos, matched on hash or path, the chip names the scope and its count, and removing the chip
///     or opening someone else shows every demo again. Every store is in memory.
/// </summary>
public class PlayerOverviewScopeTests
{
    private const ulong Grim = PlayerOverviewHistoryTests.Grim;
    private const ulong Mate = 76561190000000012;

    private sealed class Shell : IDisposable
    {
        public Shell(int demos)
        {
            DemoCacheStore store = PlayerOverviewHistoryTests.Library(demos);
            PlayerDirectory directory = new(store, _ => true);
            Identity = new IdentityService(new SettingsService(null));
            Identity.SetAccounts([
                new IdentityAccount(Grim.ToString(CultureInfo.InvariantCulture), null, DateTime.UtcNow)
            ]);
            Cache = PlayerOverviewHistoryTests.Cache(store, directory);
            Tab = new PlayerOverviewTabViewModel(new PlayerOverviewServices(directory, Identity, Cache, _ => { },
                (_, _) => { }, _ => { }));
            Tab.OnActivated(null!);
        }

        private IdentityService Identity { get; }
        public PlayerRowCache Cache { get; }
        public PlayerOverviewTabViewModel Tab { get; }
        public HistorySectionViewModel History => Tab.Reveal<HistorySectionViewModel>();
        public SummarySectionViewModel Summary => Tab.Reveal<SummarySectionViewModel>();

        public string[] Paths => [.. History.Rows.Select(r => r.Match.Row.Path).Order(StringComparer.Ordinal)];

        public void Dispose()
        {
            Tab.Dispose();
            Identity.Dispose();
        }
    }

    [Test]
    public async Task AScope_NarrowsHistoryAndAggregates_ToItsDemos()
    {
        using Shell shell = new(6);
        await shell.Tab.Loading;
        PlayerOverviewScope scope = new("vs Team X", ["sha1", "SHA3", "sha5", "sha-not-in-library"]);

        shell.Tab.OpenPlayer(Grim, scope);
        await shell.Tab.Loading;

        PlayerRows rows = shell.Cache.TryGet(Grim)!;
        HashSet<string> scoped = ["/lib/1.dem", "/lib/3.dem", "/lib/5.dem"];
        PlayerAggregate expected = PlayerAggregate.Of([.. rows.Demos.Where(d => scoped.Contains(d.Match.Row.Path))]);
        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.Scope).IsSameReferenceAs(scope);
            await Assert.That(shell.Paths).IsEquivalentTo(scoped.Order(StringComparer.Ordinal),
                CollectionOrdering.Matching);
            await Assert.That(shell.History.CountText).IsEqualTo("3 matches");
            await Assert.That(shell.Summary.AllTime!.Matches).IsEqualTo(3);
            await Assert.That(shell.Summary.AllTime!.Values).IsEquivalentTo(expected.Values,
                CollectionOrdering.Matching);
            await Assert.That(shell.Tab.Filter.HasScope).IsTrue();
            await Assert.That(shell.Tab.Filter.ScopeText).IsEqualTo("vs Team X · 3 demos");
            await Assert.That(shell.Tab.Filter.IsFiltered).IsFalse()
                .Because("the scope is its own chip, not one of the bar's narrowings");
        }
    }

    [Test]
    public async Task AScope_MatchesPathsIgnoringCase_AndCountsOneDemo()
    {
        using Shell shell = new(4);
        await shell.Tab.Loading;

        shell.Tab.OpenPlayer(Grim, new PlayerOverviewScope("with Team Y", ["/LIB/2.DEM"]));
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Paths).IsEquivalentTo(["/lib/2.dem"], CollectionOrdering.Matching);
            await Assert.That(shell.Tab.Filter.ScopeText).IsEqualTo("with Team Y · 1 demo");
        }
    }

    [Test]
    public async Task ClearingTheScope_RestoresEveryDemo()
    {
        using Shell shell = new(5);
        await shell.Tab.Loading;
        shell.Tab.OpenPlayer(Grim, new PlayerOverviewScope("vs Team X", ["sha0", "sha4"]));
        await shell.Tab.Loading;
        await Assert.That(shell.History.Rows.Count).IsEqualTo(2);

        shell.Tab.Filter.ClearScopeCommand.Execute(null);
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.Scope).IsNull();
            await Assert.That(shell.Tab.Filter.HasScope).IsFalse();
            await Assert.That(shell.Tab.Filter.ScopeText).IsEqualTo("");
            await Assert.That(shell.History.Rows.Count).IsEqualTo(5);
            await Assert.That(shell.Summary.AllTime!.Matches).IsEqualTo(5);
            await Assert.That(shell.Tab.SubjectSteamId64).IsEqualTo(Grim).Because("removing the chip keeps the player");
        }
    }

    [Test]
    public async Task OpeningAnotherPlayer_DropsTheScope()
    {
        using Shell shell = new(4);
        await shell.Tab.Loading;
        shell.Tab.OpenPlayer(Grim, new PlayerOverviewScope("vs Team X", ["sha1"]));
        await shell.Tab.Loading;

        shell.Tab.OpenPlayer(Mate);
        await shell.Tab.Loading;

        using (Assert.Multiple())
        {
            await Assert.That(shell.Tab.Scope).IsNull();
            await Assert.That(shell.Tab.Filter.HasScope).IsFalse();
            await Assert.That(shell.History.Rows.Count).IsEqualTo(4);
        }
    }
}
