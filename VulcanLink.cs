using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CraftMeThis;

/// <summary>
/// GatherBuddy Reborn's Vulcan, driven through reflection (GatherBuddy only exposes auto-gather over IPC):
///  * vendor buy list — <c>GatherBuddy.VendorBuyListManager</c>: CreateList / TrySetTargets / Start / IsBusy / Stop.
///    It knows gil vendors, Grand Company exchange, scrips and special currencies including Gemstone Traders (FateShop).
///  * crafting list — <c>GatherBuddy.CraftingListManager</c>: CreateNewList(ephemeral) / AddRecipe / SaveList, started the
///    way the Vulcan window does it: CraftingExecutionPlan.Create(list) → CraftingGatherBridge.StartQueueCraftAndGather,
///    which gathers what is missing and crafts with Vulcan's own solver.
///  * Artisan on/off — Vulcan refuses to craft while Artisan is loaded; GatherBuddy's own DalamudPluginToggleHelper
///    (the "Artisan on/off" chip in the Vulcan window) switches it.
/// </summary>
public sealed class VulcanLink
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public const string ListName = "CraftMeThis";

    private Assembly? asm;
    private Type? gb;
    private readonly Dictionary<uint, bool> buyable = new();

    private bool Ready(out string? error)
    {
        error = null;
        var instance = PluginFinder.FindPluginInstance("GatherBuddyReborn");
        if (instance == null) { error = "GatherBuddy Reborn не загружен."; gb = null; return false; }
        if (gb != null && asm == instance.GetType().Assembly) return true;
        // First use, or GatherBuddy was reloaded/updated: drop everything cached from the old assembly.
        buyable.Clear();
        asm = instance.GetType().Assembly;
        gb = asm.GetType("GatherBuddy.GatherBuddy");
        if (gb == null) { error = "Не нашёл GatherBuddy.GatherBuddy — возможно, изменилась его версия."; return false; }
        return true;
    }

    /// <summary>Forget cached types (GatherBuddy reloaded).</summary>
    public void Reset()
    {
        asm = null;
        gb = null;
        buyable.Clear();
    }

    private object? Static(string name) => gb!.GetProperty(name, Any)?.GetValue(null);
    private Type T(string name) => asm!.GetType(name, true)!;

    // ---------------------------------------------------------------- vendors

    /// <summary>Whether Vulcan can buy the item from some vendor (gil, GC, scrips, gemstones…). Cached.</summary>
    public bool CanBuy(uint itemId)
    {
        if (buyable.TryGetValue(itemId, out var known)) return known;
        if (!Ready(out _)) return false;
        try
        {
            var m = T("GatherBuddy.Vulcan.Vendors.VendorBuyListManager").GetMethod("CanAddSupportedItem", Any, new[] { typeof(uint) });
            // Static in newer GatherBuddy builds, an instance method in 7.5.6.x.
            var target = m == null || m.IsStatic ? null : Static("VendorBuyListManager");
            if (m != null && !m.IsStatic && target == null) return false;
            var ok = m?.Invoke(target, new object[] { itemId }) is true;
            buyable[itemId] = ok;
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Replaces the "CraftMeThis" vendor list with targets (item → total count to have) and returns the items it accepted.</summary>
    public HashSet<uint> SetVendorList(IReadOnlyList<(uint ItemId, int Target)> targets, out string? error)
    {
        var accepted = new HashSet<uint>();
        if (!Ready(out error)) return accepted;
        try
        {
            var mgr = Static("VendorBuyListManager") ?? throw new InvalidOperationException("VendorBuyListManager ещё не создан");
            var mt = mgr.GetType();

            // Drop old lists of ours, then create a fresh one.
            var lists = mt.GetProperty("Lists", Any)?.GetValue(mgr) as IEnumerable
                        ?? mt.GetMethod("GetListsByCreation", Any, new[] { typeof(bool) })?.Invoke(null, new object[] { true }) as IEnumerable;
            if (lists != null)
            {
                foreach (var l in lists.Cast<object>().ToList())
                {
                    if (l.GetType().GetProperty("Name", Any)?.GetValue(l) as string != ListName) continue;
                    if (l.GetType().GetProperty("Id", Any)?.GetValue(l) is Guid id) mt.GetMethod("DeleteList", Any, new[] { typeof(Guid) })?.Invoke(mgr, new object[] { id });
                }
            }
            var list = mt.GetMethod("CreateList", Any)!.Invoke(mgr, new object[] { ListName, false })!;
            var listId = (Guid)list.GetType().GetProperty("Id", Any)!.GetValue(list)!;

            var reqType = mt.GetNestedType("VendorTargetRequest", Any)!;
            var reqs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(reqType))!;
            foreach (var (item, target) in targets)
                if (target > 0) reqs.Add(Activator.CreateInstance(reqType, item, (uint)target)!);
            mt.GetMethod("TrySetTargets", Any, new[] { typeof(Guid), typeof(IReadOnlyList<>).MakeGenericType(reqType), typeof(bool), typeof(bool), typeof(bool) })!
              .Invoke(mgr, new object[] { listId, reqs, false, false, false });

            if (list.GetType().GetProperty("Entries", Any)?.GetValue(list) is IEnumerable entries)
                foreach (var e in entries)
                    if (e.GetType().GetProperty("ItemId", Any)?.GetValue(e) is uint id) accepted.Add(id);
            vendorListId = listId;
            return accepted;
        }
        catch (Exception ex)
        {
            error = $"Не удалось собрать список покупок Vulcan: {Inner(ex)}";
            return accepted;
        }
    }

    private Guid? vendorListId;

    /// <summary>Starts the vendor list. Returns the StartResult name ("Started", "VendorDataLoading", …) or an error.</summary>
    public string StartVendorList()
    {
        if (!Ready(out var err)) return err!;
        if (vendorListId == null) return "NoList";
        try
        {
            var mgr = Static("VendorBuyListManager")!;
            var start = mgr.GetType().GetMethod("Start", Any)!;
            var res = start.Invoke(mgr, new object?[] { vendorListId, null });
            return res?.ToString() ?? "Unknown";
        }
        catch (Exception ex)
        {
            return $"Error: {Inner(ex)}";
        }
    }

    public bool VendorBusy => Ready(out _) && Static("VendorBuyListManager") is { } m && m.GetType().GetProperty("IsBusy", Any)?.GetValue(m) is true;

    public string VendorStatus =>
        Ready(out _) && Static("VendorBuyListManager") is { } m ? m.GetType().GetProperty("StatusText", Any)?.GetValue(m) as string ?? "" : "";

    public void StopVendor()
    {
        if (Ready(out _) && Static("VendorBuyListManager") is { } m) m.GetType().GetMethod("Stop", Any, Type.EmptyTypes)?.Invoke(m, null);
    }

    // ---------------------------------------------------------------- crafting

    /// <summary>
    /// Makes Vulcan's persistent crafting list "CraftMeThis" equal to the cart (created if missing), so Vulcan's own
    /// Materials window shows the cart. Returns the list object, or null with an error.
    /// </summary>
    public object? SyncList(IReadOnlyList<CartLine> cart, bool retainerRestock, out string? error)
    {
        if (!Ready(out error)) return null;
        try
        {
            var mgr = Static("CraftingListManager") ?? throw new InvalidOperationException("CraftingListManager ещё не создан");
            var mt = mgr.GetType();
            var list = mt.GetMethod("GetListByName", Any)?.Invoke(mgr, new object[] { ListName });
            // 0.8.0 made it ephemeral (Vulcan deletes those after a run): replace it with a normal list.
            if (list != null && list.GetType().GetProperty("Ephemeral", Any)?.GetValue(list) is true
                && list.GetType().GetProperty("ID", Any)?.GetValue(list) is int oldId)
            {
                mt.GetMethod("DeleteList", Any, new[] { typeof(int) })?.Invoke(mgr, new object[] { oldId });
                list = null;
            }
            list ??= mt.GetMethod("CreateNewList", Any)!.Invoke(mgr, new object?[] { ListName, false, null })!;

            var lt = list.GetType();
            if (lt.GetProperty("Recipes", Any)?.GetValue(list) is IList recipes) recipes.Clear();
            var add = lt.GetMethod("AddRecipe", Any, new[] { typeof(uint), typeof(int) })!;
            foreach (var l in cart) if (l.Crafts > 0) add.Invoke(list, new object[] { l.RecipeId, l.Crafts });
            lt.GetProperty("RetainerRestock", Any)?.SetValue(list, retainerRestock);
            lt.GetMethod("BuildExpandedList", Any, Type.EmptyTypes)?.Invoke(list, null);
            mt.GetMethod("SaveList", Any)?.Invoke(mgr, new[] { list });
            return list;
        }
        catch (Exception ex)
        {
            error = $"Не удалось обновить список «{ListName}» в Vulcan: {Inner(ex)}";
            return null;
        }
    }

    /// <summary>Syncs the "CraftMeThis" list and starts Vulcan's gather + craft queue on it.</summary>
    public string? StartCrafting(IReadOnlyList<CartLine> cart, bool retainerRestock)
    {
        var list = SyncList(cart, retainerRestock, out var err);
        if (list == null) return err;
        try
        {
            var lt = list.GetType();
            var plan = T("GatherBuddy.Crafting.CraftingExecutionPlan").GetMethod("Create", Any, new[] { lt })!.Invoke(null, new[] { list });
            var startQ = T("GatherBuddy.Crafting.CraftingGatherBridge").GetMethod("StartQueueCraftAndGather", Any)!;
            var consumables = lt.GetProperty("Consumables", Any)?.GetValue(list);
            startQ.Invoke(null, new[] { plan, consumables, null });
            return null;
        }
        catch (Exception ex)
        {
            return $"Не удалось запустить крафт в Vulcan: {Inner(ex)}";
        }
    }

    /// <summary>
    /// After "/vulcan CraftMeThis" has opened the list in the Vulcan window: hand its editor to Vulcan's Materials
    /// window and show it, like the editor's own "Materials" button. Returns false while the editor is not there yet.
    /// </summary>
    public bool ShowMaterialsWindow()
    {
        if (!Ready(out _)) return false;
        try
        {
            var plugin = PluginFinder.FindPluginInstance("GatherBuddyReborn");
            var vulcan = plugin?.GetType().GetField("_vulcanWindow", Any)?.GetValue(plugin);
            var editor = vulcan?.GetType().GetField("_listEditor", Any)?.GetValue(vulcan);
            if (editor == null) return false;
            var mats = Static("CraftingMaterialsWindow");
            if (mats == null) return false;
            mats.GetType().GetMethod("SetEditor", Any)?.Invoke(mats, new[] { editor });
            var restore = mats.GetType().GetMethod("OpenAndRestore", Any, Type.EmptyTypes);
            if (restore != null) restore.Invoke(mats, null);
            else mats.GetType().GetProperty("IsOpen", Any)?.SetValue(mats, true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Vulcan materials window: {Inner(ex)}");
            return true; // give up quietly; the list itself is open in Vulcan
        }
    }

    private object? QueueProcessor() =>
        Ready(out _) ? T("GatherBuddy.Crafting.CraftingGatherBridge").GetField("_queueProcessor", Any)?.GetValue(null) : null;

    public bool CraftingRunning => QueueProcessor() != null;

    /// <summary>Queue state name ("WaitingForGather", "Crafting", …), "Paused: …" or "".</summary>
    public string CraftingState
    {
        get
        {
            var q = QueueProcessor();
            if (q == null) return "";
            var t = q.GetType();
            var state = t.GetProperty("CurrentState", Any)?.GetValue(q)?.ToString() ?? "";
            return t.GetProperty("Paused", Any)?.GetValue(q) is true ? $"пауза ({state})" : state;
        }
    }

    public void StopCrafting()
    {
        if (Ready(out _)) T("GatherBuddy.Crafting.CraftingGatherBridge").GetMethod("StopQueue", Any, Type.EmptyTypes)?.Invoke(null, null);
    }

    // ---------------------------------------------------------------- Artisan

    /// <summary>Artisan loaded? null when unknown.</summary>
    public static bool ArtisanLoaded => Plugin.IsLoaded("Artisan");

    /// <returns>null when the switch was requested, otherwise why not.</returns>
    public string? SetArtisan(bool enable)
    {
        if (!Ready(out var err)) return err;
        try
        {
            var helper = T("GatherBuddy.Utility.DalamudPluginToggleHelper");
            var args = new object?[] { "Artisan", enable, null, null };
            var ok = helper.GetMethod("TrySetPluginEnabled", Any)!.Invoke(null, args) is true;
            return ok ? null : args[3] as string ?? "GatherBuddy не смог переключить Artisan.";
        }
        catch (Exception ex)
        {
            return $"Не удалось переключить Artisan: {Inner(ex)}";
        }
    }

    private static string Inner(Exception ex) => (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
}
