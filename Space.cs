using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

/// <summary>
/// Bag space: how many of the 140 main-inventory slots are free, and roughly how many a set of cart lines will take
/// (raw materials still to get, precrafts, finished items — one slot per stack, crystals excluded since they have their
/// own bag). Used to split a big cart into batches that each fit: buy → craft one batch, then the next.
/// </summary>
public static class Space
{
    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    public static unsafe int FreeSlots()
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var free = 0;
        foreach (var t in Bags)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == 0) free++;
            }
        }
        return free;
    }

    /// <summary>
    /// Copies of an item in the four bags, whatever their flags. InventoryManager.GetInventoryItemCount skips
    /// collectables, so a bag full of crafted collectables reads as 0 there.
    /// </summary>
    public static unsafe int CountInBags(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var n = 0;
        foreach (var t in Bags)
        {
            var c = im->GetInventoryContainer(t);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId) n += (int)slot->Quantity;
            }
        }
        return n;
    }

    private static int Stacks(uint itemId, long qty)
    {
        if (qty <= 0 || itemId <= 19) return 0;
        var stack = Plugin.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var it) ? Math.Max(1u, it.StackSize) : 999u;
        return (int)Math.Ceiling(qty / (double)stack);
    }

    /// <summary>Slots these lines will need on top of what the bags already hold.</summary>
    public static int Estimate(Plugin p, IReadOnlyList<CartLine> lines)
    {
        if (lines.Count == 0) return 0;
        var plan = p.Planner.Build(lines, Plugin.BagCount, id => p.RouteOf(id) == Route.Bicolor);
        var slots = 0;
        // Raw materials still to buy, gather, farm (a new stack only if none is in the bags yet; otherwise extra stacks).
        foreach (var (id, qty) in plan.Leaves) slots += NewStacks(id, qty);
        foreach (var g in plan.Purchases) slots += NewStacks(g.ItemId, g.Units);
        // Precrafts and finished items.
        var seen = new HashSet<uint>();
        foreach (var l in lines)
        {
            var item = p.Planner.RecipeItem(l.RecipeId);
            slots += NewStacks(item, (long)l.Crafts * p.Planner.RecipeYield(l.RecipeId));
            foreach (var (ing, amount) in p.Planner.Ingredients(l.RecipeId))
                if (ing > 19 && p.Planner.HasRecipe(ing) && seen.Add(ing)) slots += NewStacks(ing, amount * l.Crafts);
        }
        return slots;
    }

    private static int NewStacks(uint itemId, long qty)
    {
        var have = Plugin.BagCount(itemId);
        return Stacks(itemId, have + qty) - Stacks(itemId, have);
    }

    /// <summary>
    /// Takes cart lines off the front of <paramref name="remaining"/> while they fit (bag slots and currency caps).
    /// Always takes at least something: a line that is too big on its own is cut down to the crafts that fit (at least 1).
    /// </summary>
    public static List<CartLine> NextBatch(Plugin p, List<CartLine> remaining, Func<IReadOnlyList<CartLine>, bool> fits)
    {
        var batch = new List<CartLine>();
        while (remaining.Count > 0)
        {
            var line = remaining[0];
            var trial = batch.Append(line).ToList();
            if (fits(trial)) { batch.Add(line); remaining.RemoveAt(0); continue; }
            if (batch.Count > 0) break;
            // A single line that does not fit: as many crafts of it as fit, the rest stays for the next batch.
            int lo = 1, hi = line.Crafts - 1, best = 1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (fits(new[] { line with { Crafts = mid } })) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            batch.Add(line with { Crafts = best });
            if (best >= line.Crafts) remaining.RemoveAt(0); else remaining[0] = line with { Crafts = line.Crafts - best };
            break;
        }
        return batch;
    }
}
