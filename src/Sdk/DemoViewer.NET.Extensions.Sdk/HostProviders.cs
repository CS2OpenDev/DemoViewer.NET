namespace DemoViewer.NET.Extensions.Sdk;

/// <summary>
///     The per-player and match-wide reads the host adds to every rules run, on top of the engine's catalog. They
///     cover fields the engine networks but its catalog does not name yet: armour, kit, economy and the tactical
///     timeouts. Each one has two spellings:
///     <list type="bullet">
///         <item>
///             the provider name, which a <see cref="CollectorContribution" /> lists in its
///             <see cref="CollectorContribution.Providers" /> (<c>entity.dv.player.has_helmet</c>);
///         </item>
///         <item>
///             the rules path, which a ruleset reads (<c>player.dv_has_helmet</c>, <c>match.dv_t_timeouts_left</c>),
///             from <see cref="RulesPath" />.
///         </item>
///     </list>
///     <para>
///         A collector reads the <c>match</c> ones per player: every slot with a controller reads the same value.
///         A ruleset reads them as <c>match.*</c>. Extensions read this set; they cannot add providers of their
///         own.
///     </para>
/// </summary>
[PublicAPI]
public static class HostProviders
{
    /// <summary>
    ///     Bumped when what one of these reads changes. A collector that declares one folds it into its
    ///     <see cref="CollectorContribution.Stamp" />, and a ruleset that reads one into its stored outputs' stamp.
    /// </summary>
    public const int Version = 1;

    /// <summary>What every host provider name starts with.</summary>
    public const string Prefix = "entity.dv.";

    /// <summary>bool: the pawn has a helmet (<c>m_pItemServices.m_bHasHelmet</c>).</summary>
    public const string HasHelmet = "entity.dv.player.has_helmet";

    /// <summary>bool: the pawn has a defuse kit (<c>m_pItemServices.m_bHasDefuser</c>).</summary>
    public const string HasDefuser = "entity.dv.player.has_defuser";

    /// <summary>int: what the player has spent this round (<c>m_pInGameMoneyServices.m_iCashSpentThisRound</c>).</summary>
    public const string CashSpentRound = "entity.dv.player.cash_spent_round";

    /// <summary>int: the player's money at the start of the round (<c>m_pInGameMoneyServices.m_iStartAccount</c>).</summary>
    public const string StartAccount = "entity.dv.player.start_account";

    /// <summary>bool: a CT tactical timeout is running (<c>m_pGameRules.m_bCTTimeOutActive</c>).</summary>
    public const string CtTimeoutActive = "entity.dv.match.ct_timeout_active";

    /// <summary>bool: a T tactical timeout is running (<c>m_pGameRules.m_bTerroristTimeOutActive</c>).</summary>
    public const string TTimeoutActive = "entity.dv.match.t_timeout_active";

    /// <summary>
    ///     int: tactical timeouts the side playing CT has left (<c>m_pGameRules.m_nCTTimeOuts</c>). The counts follow
    ///     the side, not the team: they swap at the half.
    /// </summary>
    public const string CtTimeoutsLeft = "entity.dv.match.ct_timeouts_left";

    /// <summary>int: tactical timeouts the side playing T has left (<c>m_pGameRules.m_nTerroristTimeOuts</c>).</summary>
    public const string TTimeoutsLeft = "entity.dv.match.t_timeouts_left";

    /// <summary>bool: a technical timeout is running (<c>m_pGameRules.m_bTechnicalTimeOut</c>).</summary>
    public const string TechnicalTimeout = "entity.dv.match.technical_timeout";

    /// <summary>Every host provider name, the per-player ones first.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        HasHelmet, HasDefuser, CashSpentRound, StartAccount,
        CtTimeoutActive, TTimeoutActive, CtTimeoutsLeft, TTimeoutsLeft, TechnicalTimeout
    ];

    /// <summary>True when <paramref name="providerName" /> is one of <see cref="All" />.</summary>
    public static bool IsHostProvider(string? providerName) =>
        providerName is not null && All.Contains(providerName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The path a ruleset reads a host provider by: <c>entity.dv.player.has_helmet</c> is
    ///     <c>player.dv_has_helmet</c>, <c>entity.dv.match.t_timeouts_left</c> is <c>match.dv_t_timeouts_left</c>.
    /// </summary>
    /// <param name="providerName">One of <see cref="All" />.</param>
    public static string RulesPath(string providerName)
    {
        if (!IsHostProvider(providerName))
        {
            throw new ArgumentException($"'{providerName}' is not a host provider", nameof(providerName));
        }

        string rest = providerName[Prefix.Length..];
        int dot = rest.IndexOf('.', StringComparison.Ordinal);
        return $"{rest[..dot]}.dv_{rest[(dot + 1)..]}";
    }
}
