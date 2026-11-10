#region

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;
using DemoViewer.NET.Extensions.Sdk;

#endregion

namespace DemoViewer.NET.Services.DemoProcessing;

/// <summary>
///     The host's own providers (<see cref="HostProviders" />) as the engine takes them: specs in its provider
///     registries for every build, and rows in its catalog so a ruleset can name them.
///     <para>
///         The rules language resolves <c>player.*</c> and <c>match.*</c> through the engine's embedded catalog,
///         which takes no extension point, so <see cref="Install" /> appends the host's rows to the parsed copy the
///         engine caches. Resolution, validation, the fingerprint and the Rule Workbench's vocabulary all read that
///         copy. The provider names start with <c>entity.</c> because the engine lowers a <c>player.*</c> read to
///         <c>player.&lt;provider name&gt;</c> and accepts only <c>player.entity.*</c> there.
///     </para>
///     <para>
///         The <c>match</c> reads are singletons for the rules, and per-player broadcasts under the same name for a
///         collector, which reads per-player providers only. A rules read of one turns both on; the broadcast
///         writes one value per controller slot and costs little.
///     </para>
/// </summary>
internal static class HostProviderSet
{
    private const string GameRules = "CCSGameRulesProxy";

    private static readonly PlayerSpec[] _players =
    [
        new(HostProviders.HasHelmet, "CCSPlayerPawn", "m_pItemServices.m_bHasHelmet", typeof(bool), false,
            "True while the pawn has a helmet. Read before the event's frame, like every player column."),
        new(HostProviders.HasDefuser, "CCSPlayerPawn", "m_pItemServices.m_bHasDefuser", typeof(bool), false,
            "True while the pawn has a defuse kit."),
        new(HostProviders.CashSpentRound, "CCSPlayerController", "m_pInGameMoneyServices.m_iCashSpentThisRound",
            typeof(int), true,
            "What the player has spent this round, off the controller. A refund or a sale lowers it."),
        new(HostProviders.StartAccount, "CCSPlayerController", "m_pInGameMoneyServices.m_iStartAccount",
            typeof(int), true, "The player's money at the start of the round, off the controller.")
    ];

    private static readonly MatchSpec[] _match =
    [
        new(HostProviders.CtTimeoutActive, "m_pGameRules.m_bCTTimeOutActive", typeof(bool), typeof(CtTimeoutActiveMarker),
            false, "True while a CT tactical timeout runs."),
        new(HostProviders.TTimeoutActive, "m_pGameRules.m_bTerroristTimeOutActive", typeof(bool),
            typeof(TTimeoutActiveMarker), false, "True while a T tactical timeout runs."),
        new(HostProviders.CtTimeoutsLeft, "m_pGameRules.m_nCTTimeOuts", typeof(int), typeof(CtTimeoutsLeftMarker), 0,
            "Tactical timeouts the CT side has left. It follows the side, so the two counts swap at the half."),
        new(HostProviders.TTimeoutsLeft, "m_pGameRules.m_nTerroristTimeOuts", typeof(int),
            typeof(TTimeoutsLeftMarker), 0,
            "Tactical timeouts the T side has left. It follows the side, so the two counts swap at the half."),
        new(HostProviders.TechnicalTimeout, "m_pGameRules.m_bTechnicalTimeOut", typeof(bool),
            typeof(TechnicalTimeoutMarker), false, "True while a technical timeout runs.")
    ];

    // A read of any host path in a ruleset's text: player.dv_x, match.dv_x, or a role's (victim.dv_x).
    private static readonly Regex _hostRead = new(@"\b[a-z_]+\.dv_[a-z0-9_]+", RegexOptions.CultureInvariant);

    private static readonly ConditionalWeakTable<RulesetDoc, StrongBox<bool>> _reads = new();

    /// <summary>Why the catalog rows are not in, or null when they are.</summary>
    public static string? CatalogRefusal { get; private set; }

    /// <summary>
    ///     Appends the host's rows to the engine's cached catalog, once, before any ruleset is resolved. A catalog
    ///     the engine no longer holds as a list leaves them out: a ruleset naming a host path is then excluded
    ///     with the engine's own diagnostic, and collectors still read the providers.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255",
        Justification = "Application code that is a class library only because several heads reference it; "
            + "the rows must be in before the first ruleset resolves, whichever path loads rules first.")]
    internal static void Install()
    {
        try
        {
            if (CatalogResource.Load().Providers is not List<CatalogProvider> providers)
            {
                CatalogRefusal = "the engine's catalog is not a list";
                return;
            }

            lock (providers)
            {
                HashSet<string> known = new(providers.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                foreach (CatalogProvider row in CatalogRows())
                {
                    if (known.Add(row.Name))
                    {
                        providers.Add(row);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CatalogRefusal = ex.Message;
        }
    }

    /// <summary>The engine's default per-player providers plus the host's.</summary>
    public static PerPlayerEntityValueProviderRegistry PerPlayer() =>
        AddPerPlayer(PerPlayerEntityValueProviderRegistry.CreateDefault());

    /// <summary>
    ///     <paramref name="options" /> with the host's providers in both registries. A registry the caller set is
    ///     added to, not replaced; a provider already under a host name is kept.
    /// </summary>
    public static AnalysisOptions Wire(AnalysisOptions options) => options with
    {
        PerPlayerEntityProviders = AddPerPlayer(options.PerPlayerEntityProviders
            ?? PerPlayerEntityValueProviderRegistry.CreateDefault()),
        EntityProviders = AddSingletons(options.EntityProviders ?? EntityValueProviderRegistry.CreateDefault())
    };

    /// <summary>True when any ruleset in <paramref name="docs" /> reads a host path.</summary>
    public static bool ReadAny(IEnumerable<RulesetDoc> docs) => docs.Any(Reads);

    /// <summary>
    ///     What an output stamp folds in for <paramref name="docs" />: the host set's version when one of them reads
    ///     a host path, else empty, so no other stamp moves.
    /// </summary>
    public static string StampFor(IEnumerable<RulesetDoc> docs) =>
        ReadAny(docs) ? $"dv={HostProviders.Version}" : "";

    private static bool Reads(RulesetDoc doc) => _reads.GetValue(doc, d => new StrongBox<bool>(ReadsCore(d))).Value;

    private static bool ReadsCore(RulesetDoc doc)
    {
        try
        {
            return _hostRead.IsMatch(JsonSerializer.Serialize(doc));
        }
        catch (Exception)
        {
            // An unserializable doc is stamped as a reader: a stamp that moves needlessly costs one re-run.
            return true;
        }
    }

    private static PerPlayerEntityValueProviderRegistry AddPerPlayer(PerPlayerEntityValueProviderRegistry registry)
    {
        foreach (PlayerSpec spec in _players)
        {
            if (registry.Get(spec.Name) is null)
            {
                ProviderSpec read = new(spec.Name, spec.EntityClass, spec.Path, spec.Type);
                registry.Register(spec.OnController
                    ? new ControllerFieldProvider(read)
                    : new GenericPerPlayerFieldProvider(read));
            }
        }

        foreach (MatchSpec spec in _match)
        {
            if (registry.Get(spec.Name) is null)
            {
                registry.Register(new MatchBroadcastProvider(spec));
            }
        }

        return registry;
    }

    private static EntityValueProviderRegistry AddSingletons(EntityValueProviderRegistry registry)
    {
        foreach (MatchSpec spec in _match)
        {
            if (registry.Get(spec.Name) is null)
            {
                registry.Register(new GenericSingletonFieldProvider(
                    new ProviderSpec(spec.Name, GameRules, spec.Path, spec.Type), ChangeDirection.RisingOnly,
                    spec.Marker, spec.Default));
            }
        }

        return registry;
    }

    private static IEnumerable<CatalogProvider> CatalogRows()
    {
        foreach (PlayerSpec spec in _players)
        {
            yield return Row(spec.Name, "perPlayer", spec.Type, spec.Note);
        }

        foreach (MatchSpec spec in _match)
        {
            yield return Row(spec.Name, "singleton", spec.Type, spec.Note);
        }

        static CatalogProvider Row(string name, string scope, Type type, string note) =>
            type == typeof(bool)
                ? new CatalogProvider(name, scope, "bool", HostProviders.RulesPath(name), "Bool", Note: note)
                : new CatalogProvider(name, scope, "int", HostProviders.RulesPath(name), "Int", Note: note);
    }

    private sealed record PlayerSpec(string Name, string EntityClass, string Path, Type Type, bool OnController,
        string Note);

    private sealed record MatchSpec(string Name, string Path, Type Type, Type Marker, object Default, string Note);

    /// <summary>
    ///     A game-rules field read per player, for a collector: every slot with a controller reads the one value.
    /// </summary>
    private sealed class MatchBroadcastProvider(MatchSpec spec)
        : IPerPlayerEntityValueProvider, IWorkerCloneable<IPerPlayerEntityValueProvider>
    {
        private const int MaxSlots = 64;
        private int _cachedIndex = -1;

        public string EntityClass => GameRules;
        public string FieldName => spec.Path;
        public string Name => spec.Name;
        public Type ValueType => spec.Type;

        public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit)
        {
            if (Value(layer.Tracker) is not { } value)
            {
                return;
            }

            for (int slot = 0; slot < MaxSlots; slot++)
            {
                if (ControllerFieldProvider.ResolveController(layer.Tracker, slot) is not null)
                {
                    emit(slot, value);
                }
            }
        }

        public object? Read(EntityStateLayer layer, int playerSlot) => Value(layer.Tracker);

        public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) => Value(tracker);

        public IPerPlayerEntityValueProvider CloneForWorker() => new MatchBroadcastProvider(spec);

        // One shared box per bool, as the engine's own singleton reads hand out.
        private static readonly object _true = true;
        private static readonly object _false = false;

        private object? Value(EntityTracker tracker)
        {
            if (Raw(tracker) is not { } value)
            {
                return null;
            }

            if (spec.Type == typeof(bool))
            {
                return value != 0 ? _true : _false;
            }

            return value;
        }

        private int? Raw(EntityTracker tracker) =>
            GameRulesEntity(tracker)?[spec.Path] switch
            {
                null => null,
                bool b => b ? 1 : 0,
                IConvertible c => Convert.ToInt32(c, CultureInfo.InvariantCulture),
                _ => null
            };

        private EntityState? GameRulesEntity(EntityTracker tracker)
        {
            if (_cachedIndex >= 0)
            {
                if (tracker.CurrentEntities[_cachedIndex] is { ClassName: GameRules } cached)
                {
                    return cached;
                }

                _cachedIndex = -1;
            }

            foreach ((int index, EntityState entity) in tracker.CurrentEntities.AllIndexed())
            {
                if (entity.ClassName == GameRules)
                {
                    _cachedIndex = index;
                    return entity;
                }
            }

            return null;
        }
    }

    internal sealed class CtTimeoutActiveMarker;

    internal sealed class TTimeoutActiveMarker;

    internal sealed class CtTimeoutsLeftMarker;

    internal sealed class TTimeoutsLeftMarker;

    internal sealed class TechnicalTimeoutMarker;
}
