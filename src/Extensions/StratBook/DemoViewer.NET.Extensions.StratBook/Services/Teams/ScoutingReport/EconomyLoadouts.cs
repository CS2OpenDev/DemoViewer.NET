#region

using DemoViewer.NET.Extensions.StratBook.Services.RoundIndex;

#endregion

namespace DemoViewer.NET.Extensions.StratBook.Services.Teams.ScoutingReport;

/// <summary>A team round with the captures of its demo's round.</summary>
/// <param name="Round">The team's round.</param>
/// <param name="View">The same round's captures.</param>
public sealed record CapturedTeamRound(TeamRound Round, RoundCaptureView View);

/// <summary>A name and how many times it was bought.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Count">The buys.</param>
public sealed record BoughtCount(string Name, int Count);

/// <summary>Players who did something in a pistol round, with the rounds it happened in.</summary>
/// <param name="Players">The players.</param>
/// <param name="Rounds">The rounds, each once.</param>
public sealed record PistolShare(int Players, IReadOnlyList<TeamRound> Rounds);

/// <summary>What one side's players buy in the pistol round, across the team's pistol rounds.</summary>
/// <param name="Side">2 T, 3 CT.</param>
/// <param name="Players">Player loadouts counted.</param>
/// <param name="Rounds">Pistol rounds counted.</param>
/// <param name="Armor">Players with kevlar.</param>
/// <param name="Helmet">Players with a helmet.</param>
/// <param name="Defuser">Players with a kit (CT).</param>
/// <param name="Utility">Players who bought a grenade.</param>
/// <param name="UtilityItems">The grenades bought, most first.</param>
/// <param name="Upgrade">Players who bought a pistol.</param>
/// <param name="UpgradeNames">The pistols bought, most first.</param>
public sealed record PistolKit(
    int Side,
    int Players,
    IReadOnlyList<TeamRound> Rounds,
    PistolShare Armor,
    PistolShare Helmet,
    PistolShare Defuser,
    PistolShare Utility,
    IReadOnlyList<BoughtCount> UtilityItems,
    PistolShare Upgrade,
    IReadOnlyList<BoughtCount> UpgradeNames);

/// <summary>The weapons the team bought on rounds of one low buy type.</summary>
/// <param name="Buy">Eco, Semi or Force.</param>
/// <param name="Rounds">The team's rounds of that buy with captures.</param>
/// <param name="Weapons">Every weapon bought, most first; pickups are not here.</param>
public sealed record LowBuyWeapons(BuyType Buy, IReadOnlyList<TeamRound> Rounds, IReadOnlyList<BoughtCount> Weapons);

/// <summary>
///     The Economy inspector's two capture-fed answers: the pistol-round loadout per side and the weapons bought
///     on eco, semi and force rounds. Both read purchases (a pickup is not a buy) and only the team's own slots, so
///     they are pure functions of <see cref="CapturedTeamRound" /> lists and are tested on synthetic captures.
///     About 1.5 percent of buys are drops bought for a teammate and counted for the buyer.
/// </summary>
public static class EconomyLoadouts
{
    /// <summary>How the inspector explains what a buy count is.</summary>
    public const string BuyCaveat =
        "bought weapons only, pickups left out; about 1.5% of buys are drops bought for a teammate and counted for the buyer";

    // Not weapons: kit, armour, grenades, the bomb and the knife.
    private static readonly HashSet<string> _notWeapons = new(StringComparer.Ordinal)
    {
        "vest", "vesthelm", "defuser", "taser", "smokegrenade", "flashbang", "hegrenade", "molotov", "incgrenade",
        "decoy", "c4", "knife", "knife_t", "healthshot"
    };

    private static readonly Dictionary<string, string> _names = new(StringComparer.Ordinal)
    {
        ["p250"] = "P250", ["fiveseven"] = "Five-SeveN", ["tec9"] = "Tec-9", ["cz75a"] = "CZ75", ["deagle"] = "Deagle",
        ["elite"] = "Dualies", ["revolver"] = "R8", ["glock"] = "Glock", ["hkp2000"] = "P2000",
        ["usp_silencer"] = "USP-S", ["mp9"] = "MP9", ["mac10"] = "MAC-10", ["mp7"] = "MP7", ["mp5sd"] = "MP5-SD",
        ["ump45"] = "UMP-45", ["p90"] = "P90", ["bizon"] = "PP-Bizon", ["galilar"] = "Galil", ["famas"] = "FAMAS",
        ["ak47"] = "AK-47", ["m4a1"] = "M4A4", ["m4a1_silencer"] = "M4A1-S", ["sg556"] = "SG 553", ["aug"] = "AUG",
        ["awp"] = "AWP", ["ssg08"] = "Scout", ["g3sg1"] = "G3SG1", ["scar20"] = "SCAR-20", ["nova"] = "Nova",
        ["xm1014"] = "XM1014", ["mag7"] = "MAG-7", ["sawedoff"] = "Sawed-Off", ["negev"] = "Negev",
        ["m249"] = "M249", ["smokegrenade"] = "smoke", ["flashbang"] = "flash", ["hegrenade"] = "HE",
        ["molotov"] = "molotov", ["incgrenade"] = "incendiary", ["decoy"] = "decoy"
    };

    /// <summary>"AK-47", "Five-SeveN": an item's display name, the item itself when unnamed.</summary>
    /// <param name="item">The purchase item.</param>
    public static string NameOf(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _names.GetValueOrDefault(item, item);
    }

    /// <summary>The pistol-round loadout of the team's players on a side; null when no pistol round has one.</summary>
    /// <param name="rounds">The team's rounds on a map with captures.</param>
    /// <param name="side">2 T, 3 CT.</param>
    public static PistolKit? PistolKitOf(IEnumerable<CapturedTeamRound> rounds, int side)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<TeamRound> counted = [];
        List<TeamRound> armor = [], helmet = [], defuser = [], utility = [], upgrade = [];
        int players = 0, armorN = 0, helmetN = 0, defuserN = 0, utilityN = 0, upgradeN = 0;
        Dictionary<string, int> grenades = new(StringComparer.Ordinal);
        Dictionary<string, int> pistols = new(StringComparer.Ordinal);
        foreach (CapturedTeamRound pair in rounds.Where(r => r.Round.Side == side && r.View.IsPistol))
        {
            List<PistolLoadout> kits =
                [.. pair.View.PistolLoadouts(side).Where(l => pair.Round.Own.Slots.Contains(l.Slot))];
            if (kits.Count == 0)
            {
                continue;
            }

            counted.Add(pair.Round);
            players += kits.Count;
            foreach (PistolLoadout kit in kits)
            {
                Count(kit.Armor == true, pair.Round, armor, ref armorN);
                Count(kit.Helmet == true, pair.Round, helmet, ref helmetN);
                Count(side == 3 && kit.Defuser == true, pair.Round, defuser, ref defuserN);
                Count(kit.Utility.Count > 0, pair.Round, utility, ref utilityN);
                Count(kit.Pistol is not null, pair.Round, upgrade, ref upgradeN);
                foreach (string item in kit.Utility)
                {
                    grenades[NameOf(item)] = grenades.GetValueOrDefault(NameOf(item)) + 1;
                }

                if (kit.Pistol is { } pistol)
                {
                    pistols[NameOf(pistol)] = pistols.GetValueOrDefault(NameOf(pistol)) + 1;
                }
            }
        }

        return counted.Count == 0
            ? null
            : new PistolKit(side, players, counted, Share(armorN, armor), Share(helmetN, helmet),
                Share(defuserN, defuser), Share(utilityN, utility), Ranked(grenades), Share(upgradeN, upgrade),
                Ranked(pistols));
    }

    /// <summary>The weapons bought on the team's eco, semi and force rounds, one entry per buy type that has a round.</summary>
    /// <param name="rounds">The team's rounds on a map with captures.</param>
    public static IReadOnlyList<LowBuyWeapons> LowBuys(IEnumerable<CapturedTeamRound> rounds)
    {
        ArgumentNullException.ThrowIfNull(rounds);
        List<CapturedTeamRound> all = [.. rounds];
        List<LowBuyWeapons> result = [];
        foreach (BuyType buy in (BuyType[])[BuyType.Eco, BuyType.Semi, BuyType.Force])
        {
            List<CapturedTeamRound> here = [.. all.Where(r => r.Round.Buy == buy && r.View.Collected)];
            if (here.Count == 0)
            {
                continue;
            }

            Dictionary<string, int> bought = new(StringComparer.Ordinal);
            foreach (CapturedTeamRound pair in here)
            {
                foreach (CapturedPurchase p in pair.View.Purchases.Where(p =>
                             pair.Round.Own.Slots.Contains(p.Slot) && !_notWeapons.Contains(p.Item)))
                {
                    string name = NameOf(p.Item);
                    bought[name] = bought.GetValueOrDefault(name) + 1;
                }
            }

            result.Add(new LowBuyWeapons(buy, [.. here.Select(r => r.Round)], Ranked(bought)));
        }

        return result;
    }

    private static void Count(bool yes, TeamRound round, List<TeamRound> rounds, ref int players)
    {
        if (!yes)
        {
            return;
        }

        players++;
        if (!rounds.Contains(round))
        {
            rounds.Add(round);
        }
    }

    private static PistolShare Share(int players, List<TeamRound> rounds) => new(players, rounds);

    private static List<BoughtCount> Ranked(Dictionary<string, int> counts) =>
    [
        .. counts.Select(kv => new BoughtCount(kv.Key, kv.Value)).OrderByDescending(c => c.Count)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
    ];
}
