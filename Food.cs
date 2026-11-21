using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

/// <summary>Food and medicine in the bags, their stat bonuses, and the Well Fed buff.</summary>
internal static class Food
{
    public const uint WellFed = 48, Medicated = 49;
    public const uint Craftsmanship = 70, Control = 71, CP = 11, Gathering = 72, Perception = 73, GP = 10;
    public static readonly uint[] CraftParams = { Craftsmanship, Control, CP };
    public static readonly uint[] GatherParams = { Gathering, Perception, GP };

    public sealed record Option(uint ItemId, bool Hq, string Name, int Count, string Stats);

    private static readonly Dictionary<uint, (uint Status, uint FoodRow)> actions = new();

    /// <summary>Status the item gives (48 Well Fed / 49 Medicated) and its ItemFood row, or (0, 0).</summary>
    public static (uint Status, uint FoodRow) Action(uint itemId)
    {
        if (actions.TryGetValue(itemId, out var a)) return a;
        a = (0, 0);
        if (Plugin.Data.GetExcelSheet<Item>().TryGetRow(itemId, out var it) && it.ItemAction.IsValid)
        {
            var ia = it.ItemAction.Value;
            if (ia.Data[0] is (ushort)WellFed or (ushort)Medicated && ia.Data[1] != 0) a = (ia.Data[0], ia.Data[1]);
        }
        return actions[itemId] = a;
    }

    /// <summary>Flat bonus of this food/medicine to a stat whose value without it is <paramref name="baseValue"/>.</summary>
    public static int Bonus(uint itemId, bool hq, uint baseParam, int baseValue)
    {
        var (_, row) = Action(itemId);
        if (row == 0 || !Plugin.Data.GetExcelSheet<ItemFood>().TryGetRow(row, out var f)) return 0;
        foreach (var p in f.Params)
        {
            if (p.BaseParam.RowId != baseParam) continue;
            int value = hq ? p.ValueHQ : p.Value, max = hq ? p.MaxHQ : p.Max;
            return p.IsRelative ? Math.Min(baseValue * value / 100, max) : value;
        }
        return 0;
    }

    private static string StatText(uint itemId, bool hq)
    {
        var (_, row) = Action(itemId);
        if (row == 0 || !Plugin.Data.GetExcelSheet<ItemFood>().TryGetRow(row, out var f)) return "";
        var parts = new List<string>();
        foreach (var p in f.Params)
        {
            if (!p.BaseParam.IsValid || p.BaseParam.RowId == 0) continue;
            int value = hq ? p.ValueHQ : p.Value, max = hq ? p.MaxHQ : p.Max;
            var name = p.BaseParam.Value.Name.ExtractText();
            parts.Add(p.IsRelative ? $"{name} +{value}% (≤{max})" : $"{name} +{value}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>Food (status 48) or medicine (49) in the bags that raises one of these stats.</summary>
    public static unsafe List<Option> InBags(uint status, uint[] stats)
    {
        var counts = new Dictionary<(uint, bool), int>();
        var im = InventoryManager.Instance();
        if (im != null)
            foreach (var t in Space.Bags)
            {
                var cont = im->GetInventoryContainer(t);
                if (cont == null) continue;
                for (var i = 0; i < cont->Size; i++)
                {
                    var slot = cont->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    var key = (slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                    counts[key] = counts.GetValueOrDefault(key) + (int)slot->Quantity;
                }
            }
        var res = new List<Option>();
        foreach (var ((id, hq), n) in counts)
        {
            var (st, row) = Action(id);
            if (st != status || !Plugin.Data.GetExcelSheet<ItemFood>().TryGetRow(row, out var f)) continue;
            if (!f.Params.Any(p => stats.Contains(p.BaseParam.RowId))) continue;
            res.Add(new Option(id, hq, Name(id), n, StatText(id, hq)));
        }
        return res.OrderBy(o => o.Name).ThenByDescending(o => o.Hq).ToList();
    }

    public static unsafe int CountInBags(uint itemId, bool hq)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var n = 0;
        foreach (var t in Space.Bags)
        {
            var cont = im->GetInventoryContainer(t);
            if (cont == null) continue;
            for (var i = 0; i < cont->Size; i++)
            {
                var slot = cont->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId && slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == hq)
                    n += (int)slot->Quantity;
            }
        }
        return n;
    }

    public static bool HasStatus(uint status) =>
        Plugin.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == status) == true;

    private static string Name(uint id) => Plugin.Data.GetExcelSheet<Item>().TryGetRow(id, out var it) ? it.Name.ExtractText() : id.ToString();

    public static string Label(uint itemId, bool hq) => itemId == 0 ? "не есть" : $"{Name(itemId)}{(hq ? " HQ" : "")}";
}
