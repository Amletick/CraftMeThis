using System;
using System.Collections.Generic;
using System.Linq;

namespace CraftMeThis;

/// <summary>One line of the craft tree: a cart recipe, a precraft or a raw material.</summary>
public sealed class TreeNode
{
    public uint ItemId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>How many the parent needs (for a cart recipe: crafts × yield).</summary>
    public int Need { get; init; }
    public int Have { get; init; }
    /// <summary>Recipe that makes it, 0 for raw materials.</summary>
    public uint RecipeId { get; init; }
    /// <summary>Crafts still to do for this node (0 when the bags already cover it).</summary>
    public int Crafts { get; init; }
    public bool IsCartLine { get; init; }
    /// <summary>Route of a raw material.</summary>
    public Route Route { get; init; }
    public List<TreeNode> Children { get; } = new();
}

public enum NodeState
{
    Done,       // in the bags / crafted
    Crafting,   // Vulcan is crafting it right now
    Ready,      // all ingredients there, waiting for its turn
    Waiting,    // ingredients still missing
    Gather,     // raw: Vulcan's queue gathers it
    Bicolor,    // raw: gemstone purchase
    Vendor,     // raw: Vulcan vendor list
    Manual,     // raw: market / by hand
    Failed,     // the last craft run did not make it
}

/// <summary>Builds the cart's craft tree with bag counts, the way Vulcan works through it (precrafts it already has are skipped).</summary>
public static class CraftTree
{
    public static List<TreeNode> Build(Plugin p)
    {
        var roots = new List<TreeNode>();
        foreach (var c in p.Config.Cart)
        {
            var item = p.Planner.RecipeItem(c.RecipeId);
            if (item == 0) continue;
            roots.Add(Recipe(p, item, c.RecipeId, c.Crafts * p.Planner.RecipeYield(c.RecipeId), c.Crafts, true, 0));
        }
        return roots;
    }

    private static TreeNode Recipe(Plugin p, uint item, uint recipeId, int need, int crafts, bool cartLine, int depth)
    {
        var node = new TreeNode
        {
            ItemId = item, Name = p.Planner.ItemName(item), Need = need, Have = Plugin.BagCount(item),
            RecipeId = recipeId, Crafts = crafts, IsCartLine = cartLine,
        };
        if (depth > 10 || crafts <= 0) return node;
        foreach (var (ing, amount) in p.Planner.Ingredients(recipeId))
        {
            if (ing <= 19) continue; // crystals: Vulcan handles them, they would only clutter the tree
            var childNeed = amount * crafts;
            if (p.Planner.ResolveRecipe(ing, null) is { } sub)
            {
                var have = Plugin.BagCount(ing);
                var subCrafts = (Math.Max(0, childNeed - have) + sub.Yield - 1) / sub.Yield;
                node.Children.Add(Recipe(p, ing, sub.RecipeId, childNeed, subCrafts, false, depth + 1));
            }
            else
            {
                node.Children.Add(new TreeNode
                {
                    ItemId = ing, Name = p.Planner.ItemName(ing), Need = childNeed, Have = Plugin.BagCount(ing),
                    Route = p.RouteOf(ing),
                });
            }
        }
        return node;
    }

    /// <param name="crafted">For cart lines: items gained since the craft run started (null before any run).</param>
    public static NodeState StateOf(TreeNode n, uint craftingRecipe, Func<TreeNode, int?> crafted, ISet<uint> failed)
    {
        if (n.RecipeId == 0)
        {
            if (n.Have >= n.Need) return NodeState.Done;
            return n.Route switch
            {
                Route.Gather => NodeState.Gather,
                Route.Bicolor => NodeState.Bicolor,
                Route.Vendor => NodeState.Vendor,
                _ => NodeState.Manual,
            };
        }
        if (craftingRecipe != 0 && craftingRecipe == n.RecipeId) return NodeState.Crafting;
        if (n.IsCartLine)
        {
            if (crafted(n) is { } got && got >= n.Need) return NodeState.Done;
            if (failed.Contains(n.ItemId)) return NodeState.Failed;
        }
        else if (n.Crafts == 0 || n.Have >= n.Need)
        {
            return NodeState.Done;
        }
        return n.Children.All(c => c.Have >= c.Need) ? NodeState.Ready : NodeState.Waiting;
    }
}
