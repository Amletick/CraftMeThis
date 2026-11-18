using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace CraftMeThis;

/// <summary>
/// Hunter (HunterV2, T3chnicalD3ath) farms mob drops: it teleports, flies to spawns, kills (Rotation Solver Reborn),
/// tracks the bag count of each hunt-list item and, when every enabled item is done, sends "/li thespot" and turns
/// Auto-Hunt off. It has no IPC; its slash commands only toggle the window. So this works on its live objects:
///  * Plugin.MobRepository.Mobs (MonsterInfo: Name, TerritoryId, LootItems = English item names) — who drops what;
///  * Plugin.Configuration.HuntLists (HuntList: Name, Enabled, Items of HuntItem: ItemId, Name, TargetCount, Enabled);
///  * Plugin.Configuration.AutoHuntEnabled + Configuration.Save() — what its own "Start Auto-Hunt" button does.
/// For the run, a "CraftMeThis" list is the only enabled list; the user's lists get their Enabled flags back afterwards.
/// </summary>
public sealed class HunterLink
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public const string InternalName = "HunterV2";
    public const string ListName = "CraftMeThis";

    private Dictionary<string, List<string>>? lootIndex;
    private DateTime lootIndexAt = DateTime.MinValue;
    private Dictionary<string, bool>? savedEnabled;

    private static object? Instance() => PluginFinder.FindPluginInstance(InternalName);

    private static object? Cfg(object instance) => instance.GetType().GetProperty("Configuration", Any)?.GetValue(instance);

    /// <summary>Mobs (with zone) that drop an item, by its English name; empty when Hunter does not know it.</summary>
    public IReadOnlyList<string> DroppedBy(string englishName)
    {
        if (lootIndex == null || DateTime.UtcNow - lootIndexAt > TimeSpan.FromMinutes(2)) BuildIndex();
        return lootIndex!.TryGetValue(englishName, out var mobs) ? mobs : (IReadOnlyList<string>)Array.Empty<string>();
    }

    private void BuildIndex()
    {
        lootIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        lootIndexAt = DateTime.UtcNow;
        try
        {
            var inst = Instance();
            var repo = inst?.GetType().GetProperty("MobRepository", Any)?.GetValue(inst);
            if (repo?.GetType().GetProperty("Mobs", Any)?.GetValue(repo) is not IEnumerable mobs) return;
            foreach (var m in mobs)
            {
                var t = m.GetType();
                var name = (t.GetProperty("Name", Any)?.GetValue(m) as string ?? "?").Trim();
                if (t.GetProperty("LootItems", Any)?.GetValue(m) is not IEnumerable loot) continue;
                foreach (var it in loot)
                {
                    if (it is not string item || item.Length == 0) continue;
                    var list = lootIndex.TryGetValue(item.Trim(), out var l) ? l : lootIndex[item.Trim()] = new();
                    if (!list.Contains(name)) list.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Hunter mob database: {ex.Message}");
        }
    }

    /// <summary>Makes "CraftMeThis" the only enabled hunt list, with these items and target counts.</summary>
    public string? SetList(IReadOnlyList<(uint ItemId, string EnglishName, int Target)> items)
    {
        var inst = Instance();
        if (inst == null) return "Hunter не загружен.";
        try
        {
            var cfg = Cfg(inst) ?? throw new InvalidOperationException("нет Configuration");
            if (cfg.GetType().GetProperty("HuntLists", Any)?.GetValue(cfg) is not IList lists)
                return "У Hunter нет HuntLists — возможно, изменилась его версия.";
            var listType = lists.GetType().GetGenericArguments()[0];

            object? ours = null;
            savedEnabled ??= new();
            foreach (var l in lists)
            {
                var name = listType.GetProperty("Name", Any)?.GetValue(l) as string ?? "";
                if (name == ListName) { ours = l; continue; }
                if (listType.GetProperty("Enabled", Any)?.GetValue(l) is bool en)
                {
                    savedEnabled.TryAdd(name, en);
                    listType.GetProperty("Enabled", Any)!.SetValue(l, false);
                }
            }
            if (ours == null)
            {
                ours = Activator.CreateInstance(listType)!;
                listType.GetProperty("Name", Any)!.SetValue(ours, ListName);
                lists.Add(ours);
            }
            listType.GetProperty("Enabled", Any)!.SetValue(ours, true);
            listType.GetProperty("Fallback", Any)?.SetValue(ours, false);
            var itemList = (IList)listType.GetProperty("Items", Any)!.GetValue(ours)!;
            itemList.Clear();
            var itemType = itemList.GetType().GetGenericArguments()[0];
            foreach (var (id, name, target) in items)
            {
                var hi = Activator.CreateInstance(itemType)!;
                itemType.GetProperty("ItemId", Any)!.SetValue(hi, id);
                itemType.GetProperty("Name", Any)?.SetValue(hi, name);
                itemType.GetProperty("TargetCount", Any)!.SetValue(hi, target);
                itemType.GetProperty("Enabled", Any)?.SetValue(hi, true);
                itemList.Add(hi);
            }
            Save(cfg);
            return null;
        }
        catch (Exception ex)
        {
            return $"Не удалось настроить Hunter: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";
        }
    }

    public bool AutoHuntOn
    {
        get
        {
            var inst = Instance();
            return inst != null && Cfg(inst) is { } cfg && cfg.GetType().GetProperty("AutoHuntEnabled", Any)?.GetValue(cfg) is true;
        }
    }

    public string? SetAutoHunt(bool on)
    {
        var inst = Instance();
        if (inst == null) return "Hunter не загружен.";
        var cfg = Cfg(inst);
        var prop = cfg?.GetType().GetProperty("AutoHuntEnabled", Any);
        if (cfg == null || prop == null) return "У Hunter нет AutoHuntEnabled — возможно, изменилась его версия.";
        prop.SetValue(cfg, on);
        Save(cfg);
        return null;
    }

    /// <summary>Hunter's own status line (StateManager), if it has one.</summary>
    public string Status()
    {
        try
        {
            var inst = Instance();
            var state = inst?.GetType().GetProperty("State", Any)?.GetValue(inst);
            if (state == null) return "";
            var t = state.GetType();
            var st = t.GetProperty("CurrentState", Any)?.GetValue(state)?.ToString() ?? "";
            var item = t.GetProperty("CurrentHuntItem", Any)?.GetValue(state);
            var mob = t.GetProperty("CurrentMonster", Any)?.GetValue(state);
            var itemName = item?.GetType().GetProperty("Name", Any)?.GetValue(item) as string;
            var mobName = mob?.GetType().GetProperty("Name", Any)?.GetValue(mob) as string;
            return st + (itemName != null ? $": {itemName}" : "") + (mobName != null ? $" ← {mobName.Trim()}" : "");
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Gives the user's hunt lists their Enabled flags back and switches ours off.</summary>
    public void Restore()
    {
        var inst = Instance();
        if (inst == null || Cfg(inst) is not { } cfg) { savedEnabled = null; return; }
        try
        {
            if (cfg.GetType().GetProperty("HuntLists", Any)?.GetValue(cfg) is IList lists)
            {
                var listType = lists.GetType().GetGenericArguments()[0];
                foreach (var l in lists)
                {
                    var name = listType.GetProperty("Name", Any)?.GetValue(l) as string ?? "";
                    if (name == ListName) listType.GetProperty("Enabled", Any)?.SetValue(l, false);
                    else if (savedEnabled != null && savedEnabled.TryGetValue(name, out var en)) listType.GetProperty("Enabled", Any)?.SetValue(l, en);
                }
            }
            Save(cfg);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Hunter restore: {ex.Message}");
        }
        savedEnabled = null;
    }

    private static void Save(object cfg) => cfg.GetType().GetMethod("Save", Any, Type.EmptyTypes)?.Invoke(cfg, null);
}
