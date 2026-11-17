using System.Collections.Generic;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

public enum Source { Gather, Fish, Vendor, Other }

/// <summary>
/// Where a raw material comes from, as far as LazyCrafter can get it on its own: gathering (GatherBuddy), fishing,
/// or a gil vendor (Knightshopper). Anything else (drops, market-only items) ends up on the market-board list,
/// which LazyCrafter leaves to the player.
/// </summary>
public sealed class Sources
{
    private readonly HashSet<uint> gather = new();
    private readonly HashSet<uint> fish = new();
    private readonly HashSet<uint> vendor = new();

    public Sources(ExcelSheet<GatheringItem> gathering, ExcelSheet<FishParameter> fishing, ExcelSheet<SpearfishingItem> spear,
                   SubrowExcelSheet<GilShopItem> gilShops)
    {
        foreach (var g in gathering) if (g.Item.RowId != 0) gather.Add(g.Item.RowId);
        foreach (var f in fishing) if (f.Item.RowId != 0) fish.Add(f.Item.RowId);
        foreach (var s in spear) if (s.Item.RowId != 0) fish.Add(s.Item.RowId);
        foreach (var shop in gilShops)
            foreach (var item in shop)
                if (item.Item.RowId != 0) vendor.Add(item.Item.RowId);
    }

    public Source Of(uint itemId) =>
        gather.Contains(itemId) ? Source.Gather
        : fish.Contains(itemId) ? Source.Fish
        : vendor.Contains(itemId) ? Source.Vendor
        : Source.Other;
}
