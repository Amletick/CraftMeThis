using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

/// <summary>One cart line: a Recipe row and how many times to craft it.</summary>
public readonly record struct CartLine(uint RecipeId, int Crafts);

/// <summary>A material that the cart needs and that a Gemstone Trader sells for Bicolor Gemstones.</summary>
public sealed record GemPurchase(uint ItemId, string Name, int Units, int Trades, uint CostPerTrade, uint ReceivePerTrade)
{
    public long Gems => (long)Trades * CostPerTrade;
}

/// <param name="Leaves">Raw materials still missing that are neither crafted nor bought for gemstones (crystals excluded).</param>
public sealed record GemPlan(IReadOnlyList<GemPurchase> Purchases, IReadOnlyList<string> Recipes, IReadOnlyDictionary<uint, int> Leaves)
{
    public long Gems => Purchases.Sum(p => p.Gems);
    public static readonly GemPlan Empty = new(Array.Empty<GemPurchase>(), Array.Empty<string>(), new Dictionary<uint, int>());
}

/// <summary>
/// Walks the recipe tree of a cart the same way Vulcan plans it: what is already in the bags is used first,
/// a material that has a recipe is crafted, and a material without a recipe that a Gemstone Trader sells is bought
/// for Bicolor Gemstones. Everything else (gathering, vendors, crystals) is collected in Leaves for the vendor/manual split.
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

    /// <summary>
    /// Whether the character can craft a recipe at all (master recipe book learned, crafter level). A missing item
    /// whose recipe is not craftable becomes a raw material (market / by hand) instead of a precraft.
    /// </summary>
    public Func<uint, bool>? CanCraft { get; set; }

    /// <summary>Recipes the last <see cref="Build"/> needed but the character cannot craft (see <see cref="CanCraft"/>).</summary>
    public HashSet<uint> LastBlocked { get; private set; } = new();

    private readonly IEnumerable<ExcelSheet<Item>> nameSheets = Array.Empty<ExcelSheet<Item>>();
    private Dictionary<string, uint>? itemByName;

    /// <param name="nameSheets">Item sheets in the languages item names may be typed in (client language, English...).</param>
    public Planner(ExcelSheet<Recipe> recipes, ExcelSheet<SpecialShop> shops, ExcelSheet<Item> items, IEnumerable<ExcelSheet<Item>> nameSheets)
        : this(recipes, shops, items)
    {
        this.nameSheets = nameSheets;
    }

    /// <summary>Recipe row and yield to make an item. A recipe id from an export wins when it really makes that item.</summary>
    public (uint RecipeId, int Yield)? ResolveRecipe(uint itemId, uint? recipeId)
    {
        if (recipeId is { } rid && recipes.TryGetRow(rid, out var given) && given.ItemResult.RowId == itemId && given.AmountResult > 0)
            return (rid, given.AmountResult);
        return recipeByItem.TryGetValue(itemId, out var r) ? (r.RowId, r.AmountResult) : null;
    }

    public int RecipeYield(uint recipeId) => recipes.TryGetRow(recipeId, out var r) ? Math.Max(1, (int)r.AmountResult) : 1;

    public bool IsRecipe(uint recipeId) => recipes.TryGetRow(recipeId, out var r) && r.ItemResult.RowId != 0;

    /// <summary>Item id by exact (case-insensitive) name; craftable items win over others with the same name.</summary>
    public uint FindItem(string name)
    {
        if (itemByName == null)
        {
            var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            foreach (var sheet in nameSheets.Prepend(items))
            {
                foreach (var it in sheet)
                {
                    var n = it.Name.ExtractText().Trim();
                    if (n.Length == 0) continue;
                    if (!map.TryGetValue(n, out var existing) || (!recipeByItem.ContainsKey(existing) && recipeByItem.ContainsKey(it.RowId)))
                        map[n] = it.RowId;
                }
            }
            itemByName = map;
        }
        return itemByName.TryGetValue(name.Trim(), out var id) ? id : 0;
    }

    public uint ItemIcon(uint id) => items.TryGetRow(id, out var it) ? it.Icon : 0u;

    public uint RecipeItem(uint recipeId) => recipes.TryGetRow(recipeId, out var r) ? r.ItemResult.RowId : 0u;

    public string ItemName(uint id) => items.TryGetRow(id, out var it) ? it.Name.ExtractText() : $"#{id}";

    public string RecipeName(uint recipeId) =>
        recipes.TryGetRow(recipeId, out var r) ? ItemName(r.ItemResult.RowId) : $"recipe #{recipeId}";

    public bool HasGemOffer(uint itemId) => gemOffers.ContainsKey(itemId);

    public bool HasRecipe(uint itemId) => recipeByItem.ContainsKey(itemId);

    /// <summary>Direct ingredients of a recipe (crystals included).</summary>
    public List<(uint ItemId, int Amount)> Ingredients(uint recipeId)
    {
        var list = new List<(uint, int)>();
        if (!recipes.TryGetRow(recipeId, out var r)) return list;
        for (var i = 0; i < r.Ingredient.Count; i++)
            if (r.Ingredient[i].RowId != 0 && r.AmountIngredient[i] > 0) list.Add((r.Ingredient[i].RowId, r.AmountIngredient[i]));
        return list;
    }

    /// <param name="bagCount">How many of an item are in the bags right now (NQ + HQ).</param>
    /// <param name="buyForGems">Whether a raw material that a Gemstone Trader sells should be bought for gemstones
    /// (default: always). When false it goes to <see cref="GemPlan.Leaves"/> like any other raw material.</param>
    public GemPlan Build(IEnumerable<CartLine> cart, Func<uint, int> bagCount, Func<uint, bool>? buyForGems = null)
    {
        var available = new Dictionary<uint, int>();
        int Avail(uint id)
        {
            if (!available.TryGetValue(id, out var n)) available[id] = n = Math.Max(0, bagCount(id));
            return n;
        }

        var needUnits = new Dictionary<uint, int>();
        var leaves = new Dictionary<uint, int>();
        var names = new List<string>();
        var blocked = new HashSet<uint>();

        void Require(uint id, int qty, int depth)
        {
            if (qty <= 0 || id == 0) return;
            var take = Math.Min(Avail(id), qty);
            available[id] -= take;
            qty -= take;
            if (qty == 0) return;

            Recipe r = default;
            var hasRecipe = depth < 12 && recipeByItem.TryGetValue(id, out r);
            if (hasRecipe && CanCraft?.Invoke(r.RowId) is false)
            {
                blocked.Add(r.RowId); // not craftable by this character: buy it like a raw material
                hasRecipe = false;
            }
            if (hasRecipe)
            {
                var crafts = (qty + r.AmountResult - 1) / r.AmountResult;
                Craft(r, crafts, depth + 1);
                return;
            }
            if (gemOffers.ContainsKey(id) && (buyForGems?.Invoke(id) ?? true))
                needUnits[id] = needUnits.GetValueOrDefault(id) + qty;
            else if (id > 19)
                leaves[id] = leaves.GetValueOrDefault(id) + qty;
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
            if (CanCraft?.Invoke(r.RowId) is false) blocked.Add(r.RowId);
            Craft(r, line.Crafts, 0);
        }
        LastBlocked = blocked;

        var purchases = needUnits
            .Select(kv =>
            {
                var (cost, receive) = gemOffers[kv.Key];
                var trades = (int)((kv.Value + receive - 1) / receive);
                return new GemPurchase(kv.Key, ItemName(kv.Key), kv.Value, trades, cost, receive);
            })
            .OrderByDescending(p => p.Gems)
            .ToList();
        return new GemPlan(purchases, names, leaves);
    }
}
