using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

/// <summary>A field zone with a Gemstone Trader (FateShop NPC).</summary>
public sealed record FateZone(uint Territory, string Name, uint Expansion, string Trader)
{
    /// <summary>Lowest level a job needs to do FATEs here (ShB 70, EW 80, DT 90).</summary>
    public int MinLevel => 40 + 10 * (int)Expansion;
}

public sealed record ZoneChoice(FateZone Zone, string Reason, IReadOnlyList<(string Item, int Rank)> Sold);

/// <summary>
/// Finds where the gemstone materials are sold. Every field zone of ShB/EW/DT has a Gemstone Trader whose stock
/// opens with the zone's shared FATE rank (FateShop.SpecialShop[rank-1]); city traders sell a mix.
/// The farm zone is the field zone whose trader sells the most of what the cart needs (gemstone-weighted).
/// When only city traders sell it, the highest-level field zone of that expansion is used.
/// </summary>
public sealed class Zones
{
    private readonly List<FateZone> fieldZones = new();
    /// <summary>item → (zone territory, rank needed) for field traders, and item → expansion for city traders.</summary>
    private readonly Dictionary<uint, List<(uint Territory, int Rank)>> fieldStock = new();
    private readonly Dictionary<uint, uint> cityExpansion = new();

    public IReadOnlyList<FateZone> All => fieldZones;

    public Zones(ExcelSheet<FateShop> fateShops, ExcelSheet<SpecialShop> shops, ExcelSheet<Level> levels,
                 ExcelSheet<ENpcResident> residents)
    {
        var npcLevel = new Dictionary<uint, Level>();
        foreach (var l in levels)
            if (l.Type == 8 && l.Object.RowId != 0) npcLevel.TryAdd(l.Object.RowId, l);

        foreach (var fs in fateShops)
        {
            if (fs.RowId == 0 || !npcLevel.TryGetValue(fs.RowId, out var lvl) || !lvl.Territory.IsValid) continue;
            var terr = lvl.Territory.Value;
            var isField = terr.TerritoryIntendedUse.RowId == 1;
            var trader = residents.TryGetRow(fs.RowId, out var r) ? r.Singular.ExtractText() : $"NPC {fs.RowId}";
            if (isField)
                fieldZones.Add(new FateZone(lvl.Territory.RowId, terr.PlaceName.Value.Name.ExtractText(), terr.ExVersion.RowId, trader));

            var seen = new HashSet<uint>();
            for (var tier = 0; tier < fs.SpecialShop.Count; tier++)
            {
                var sid = fs.SpecialShop[tier].RowId;
                if (sid == 0 || !seen.Add(sid) || !shops.TryGetRow(sid, out var shop)) continue;
                foreach (var e in shop.Item)
                {
                    if (!e.ItemCosts.Any(c => c.ItemCost.RowId == Planner.BicolorGemstone && c.CurrencyCost > 0)) continue;
                    var item = e.ReceiveItems[0].Item.RowId;
                    if (item == 0) continue;
                    if (isField)
                    {
                        var list = fieldStock.TryGetValue(item, out var l) ? l : fieldStock[item] = new();
                        if (!list.Any(x => x.Territory == lvl.Territory.RowId)) list.Add((lvl.Territory.RowId, tier + 1));
                    }
                    else if (!cityExpansion.ContainsKey(item))
                    {
                        cityExpansion[item] = terr.ExVersion.RowId;
                    }
                }
            }
        }
        fieldZones.Sort((a, b) => a.Expansion != b.Expansion ? a.Expansion.CompareTo(b.Expansion) : a.Territory.CompareTo(b.Territory));
    }

    public FateZone? Get(uint territory) => fieldZones.FirstOrDefault(z => z.Territory == territory);

    /// <summary>Highest-level field zone per expansion; territory ids are not in level order (Elpis 961 is below Ultima Thule 960).</summary>
    private static readonly Dictionary<uint, uint> HighestByExpansion = new() { [4] = 960, [5] = 1192 };

    /// <summary>Highest-level field zone of an expansion.</summary>
    public FateZone? Highest(uint expansion) =>
        (HighestByExpansion.TryGetValue(expansion, out var t) ? Get(t) : null)
        ?? fieldZones.Where(z => z.Expansion == expansion).LastOrDefault();

    public ZoneChoice? Choose(GemPlan plan)
    {
        if (plan.Purchases.Count == 0) return null;
        var score = new Dictionary<uint, long>();
        foreach (var p in plan.Purchases)
            if (fieldStock.TryGetValue(p.ItemId, out var where))
                foreach (var (t, _) in where) score[t] = score.GetValueOrDefault(t) + p.Gems;

        if (score.Count > 0)
        {
            // Most gemstones covered; on a tie the higher-level zone.
            var best = score.OrderByDescending(kv => kv.Value).ThenByDescending(kv => Get(kv.Key)?.Expansion ?? 0)
                            .ThenByDescending(kv => kv.Key).First().Key;
            var zone = Get(best)!;
            var sold = plan.Purchases
                .Select(p => (p.Name, Rank: fieldStock.TryGetValue(p.ItemId, out var w) ? w.Where(x => x.Territory == best).Select(x => x.Rank).DefaultIfEmpty(0).Min() : 0))
                .Where(x => x.Rank > 0).ToList();
            return new ZoneChoice(zone, $"торговец {zone.Trader} продаёт материалы корзины", sold);
        }

        var ex = plan.Purchases.Select(p => cityExpansion.TryGetValue(p.ItemId, out var e) ? e : 0u).DefaultIfEmpty(0u).Max();
        var top = ex > 0 ? Highest(ex) : fieldZones.LastOrDefault();
        return top == null ? null : new ZoneChoice(top, "материалы продают только в городе — беру самую высокоуровневую зону", Array.Empty<(string, int)>());
    }
}
