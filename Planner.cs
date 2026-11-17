using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace BicolorBridge;

/// <summary>One cart line as LazyCrafter stores it: a Recipe row and how many times to craft it.</summary>
public readonly record struct CartLine(uint RecipeId, int Crafts);

/// <summary>A material that the cart needs and that a Gemstone Trader sells for Bicolor Gemstones.</summary>
public sealed record GemPurchase(uint ItemId, string Name, int Units, int Trades, uint CostPerTrade, uint ReceivePerTrade)
{
    public long Gems => (long)Trades * CostPerTrade;
}

public sealed record GemPlan(IReadOnlyList<GemPurchase> Purchases, IReadOnlyList<string> Recipes)
{
    public long Gems => Purchases.Sum(p => p.Gems);
    public static readonly GemPlan Empty = new(Array.Empty<GemPurchase>(), Array.Empty<string>());
}

/// <summary>
/// Walks the recipe tree of a cart the same way LazyCrafter does: what is already in the bags is used first,
/// a material that has a recipe is crafted, and a material without a recipe that a Gemstone Trader sells is bought
/// for Bicolor Gemstones. Everything else (gathering, vendors, crystals) is LazyCrafter's business and is ignored.
/// </summary>
public sealed class Planner
{
    public const uint BicolorGemstone = 26807;
    public const int GemCap = 1500;

    private readonly ExcelSheet<Recipe> recipes;
    private readonly ExcelSheet<Item> items;
    private readonly Dictionary<uint, Recipe> recipeByItem = new();
    private readonly Dictionary<uint, (uint Cost, uint Receive)> gemOffers = new();

    public Planner(ExcelSheet<Recipe> recipes, ExcelSheet<SpecialShop> shops, ExcelSheet<Item> items)
    {
        this.recipes = recipes;
        this.items = items;

        foreach (var r in recipes)
        {
            var result = r.ItemResult.RowId;
            if (result == 0 || r.AmountResult == 0) continue;
            recipeByItem.TryAdd(result, r);
        }

        foreach (var shop in shops)
        {
            foreach (var entry in shop.Item)
            {
                // Only trades paid in Bicolor Gemstones alone.
                uint cost = 0;
                var other = false;
                foreach (var c in entry.ItemCosts)
                {
                    if (c.CurrencyCost == 0) continue;
                    if (c.ItemCost.RowId == BicolorGemstone && cost == 0) cost = c.CurrencyCost;
                    else other = true;
                }
                if (cost == 0 || other) continue;

                var received = entry.ReceiveItems.Where(x => x.Item.RowId != 0 && x.ReceiveCount > 0).ToList();
                if (received.Count != 1) continue;
                var id = received[0].Item.RowId;
                var receive = received[0].ReceiveCount;
                if (!gemOffers.TryGetValue(id, out var best) || (double)cost / receive < (double)best.Cost / best.Receive)
                    gemOffers[id] = (cost, receive);
            }
        }
    }

    public int GemOfferCount => gemOffers.Count;

    public string ItemName(uint id) => items.TryGetRow(id, out var it) ? it.Name.ExtractText() : $"#{id}";

    public string RecipeName(uint recipeId) =>
        recipes.TryGetRow(recipeId, out var r) ? ItemName(r.ItemResult.RowId) : $"recipe #{recipeId}";

    /// <param name="bagCount">How many of an item are in the bags right now (NQ + HQ).</param>
    public GemPlan Build(IEnumerable<CartLine> cart, Func<uint, int> bagCount)
    {
        var available = new Dictionary<uint, int>();
        int Avail(uint id)
        {
            if (!available.TryGetValue(id, out var n)) available[id] = n = Math.Max(0, bagCount(id));
            return n;
        }

        var needUnits = new Dictionary<uint, int>();
        var names = new List<string>();

        void Require(uint id, int qty, int depth)
        {
            if (qty <= 0 || id == 0) return;
            var take = Math.Min(Avail(id), qty);
            available[id] -= take;
            qty -= take;
            if (qty == 0) return;

            if (depth < 12 && recipeByItem.TryGetValue(id, out var r))
            {
                var crafts = (qty + r.AmountResult - 1) / r.AmountResult;
                Craft(r, crafts, depth + 1);
                return;
            }
            if (gemOffers.ContainsKey(id))
                needUnits[id] = needUnits.GetValueOrDefault(id) + qty;
        }

        void Craft(Recipe r, int crafts, int depth)
        {
            for (var i = 0; i < r.Ingredient.Count; i++)
            {
                var ing = r.Ingredient[i].RowId;
                var amt = r.AmountIngredient[i];
                if (ing == 0 || amt == 0) continue;
                Require(ing, amt * crafts, depth);
            }
        }

        foreach (var line in cart)
        {
            if (line.Crafts <= 0 || !recipes.TryGetRow(line.RecipeId, out var r)) continue;
            names.Add($"{ItemName(r.ItemResult.RowId)} x{line.Crafts}");
            Craft(r, line.Crafts, 0);
        }

        var purchases = needUnits
            .Select(kv =>
            {
                var (cost, receive) = gemOffers[kv.Key];
                var trades = (int)((kv.Value + receive - 1) / receive);
                return new GemPurchase(kv.Key, ItemName(kv.Key), kv.Value, trades, cost, receive);
            })
            .OrderByDescending(p => p.Gems)
            .ToList();
        return new GemPlan(purchases, names);
    }
}
