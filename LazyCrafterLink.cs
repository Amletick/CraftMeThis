using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Plugin;

namespace BicolorBridge;

/// <summary>
/// LazyCrafter has no IPC for its cart, so the cart is changed the same way its own Catalog does it:
/// Plugin.Catalog (CatalogService).ClearCart() / AddToCart(recipeId, crafts), reached through Dalamud's plugin
/// manager by reflection. Those methods persist the cart and refresh LazyCrafter's window themselves.
/// </summary>
public static class LazyCrafterLink
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <returns>null on success, otherwise what went wrong.</returns>
    public static string? SetCart(IReadOnlyList<CartLine> lines, bool replace)
    {
        object catalog;
        try
        {
            var plugin = FindPluginInstance("LazyCrafter");
            if (plugin == null) return "LazyCrafter не загружен.";
            var prop = plugin.GetType().GetProperty("Catalog", Any);
            catalog = prop?.GetValue(plugin)!;
            if (catalog == null) return "У LazyCrafter не нашёл Catalog — возможно, изменилась его версия.";
        }
        catch (Exception ex)
        {
            return $"Не удалось добраться до LazyCrafter: {ex.Message}";
        }

        var t = catalog.GetType();
        var add = t.GetMethod("AddToCart", Any, new[] { typeof(uint), typeof(int) });
        var clear = t.GetMethod("ClearCart", Any, Type.EmptyTypes);
        if (add == null || (replace && clear == null))
            return "У LazyCrafter нет AddToCart/ClearCart — возможно, изменилась его версия.";

        try
        {
            if (replace) clear!.Invoke(catalog, null);
            foreach (var l in lines)
                if (l.Crafts > 0) add.Invoke(catalog, new object[] { l.RecipeId, l.Crafts });
            return null;
        }
        catch (TargetInvocationException ex)
        {
            return $"LazyCrafter отказался принять корзину: {ex.InnerException?.Message ?? ex.Message}";
        }
    }

    private static object? FindPluginInstance(string internalName)
    {
        var asm = typeof(IDalamudPluginInterface).Assembly;
        var pmType = asm.GetType("Dalamud.Plugin.Internal.PluginManager", true)!;
        var service = asm.GetType("Dalamud.Service`1", true)!.MakeGenericType(pmType);
        var pm = service.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        var installed = (IEnumerable)pmType.GetProperty("InstalledPlugins", Any)!.GetValue(pm)!;
        foreach (var lp in installed)
        {
            var lpType = lp.GetType();
            var manifest = lpType.GetProperty("Manifest", Any)?.GetValue(lp);
            var name = manifest?.GetType().GetProperty("InternalName", Any)?.GetValue(manifest) as string;
            if (name != internalName) continue;
            if (lpType.GetProperty("IsLoaded", Any)?.GetValue(lp) is false) return null;
            for (var ty = lpType; ty != null; ty = ty.BaseType)
            {
                var f = ty.GetField("instance", Any);
                if (f != null) return f.GetValue(lp);
            }
        }
        return null;
    }
}
