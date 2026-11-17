using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CraftMeThis;

/// <summary>
/// Auto FATE Grind has no IPC. Its settings live in the static AutoFateGrind.Plugin.Cfg (Configuration), so this sets
/// them through reflection before "/afg start" and puts the user's values back afterwards:
///  * SelectedZones — the farm zone;
///  * ApplyClassOnStart — off, this plugin picks the job;
///  * LevelRangeFilterEnabled / LevelWindowEnabled — off, so FATEs below the job's level are taken (level sync);
///  * TradeList / TradeOnCap / TradeThreshold / SpendMode / KeepGemstonesReserve / AfterTrade — AFG's own
///    "buy from the Gemstone Trader" hand-off: once the wallet reaches the threshold it travels to the trader,
///    buys each listed item up to its StopAtCount and, with AfterTrade = Stop, ends the run.
///    LazyCrafter cannot do this purchase itself: it does not know Gemstone Traders (FateShop NPCs) at all.
/// </summary>
public sealed class AfgLink
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Original values of the properties changed for this run (lists are saved as copies).</summary>
    private readonly Dictionary<string, object?> saved = new();

    public bool HasSavedState => saved.Count > 0;

    /// <param name="territory">Farm zone, or 0 to keep the user's zones.</param>
    /// <param name="purchases">Items to buy from the Gemstone Trader and the bag count to stop at.</param>
    /// <param name="threshold">Gemstones at which AFG goes trading.</param>
    /// <returns>null on success, otherwise what went wrong.</returns>
    public string? Apply(uint territory, IReadOnlyList<(uint ItemId, int StopAt)> purchases, int threshold)
    {
        var cfg = Config(out var err);
        if (cfg == null) return err;
        try
        {
            var t = cfg.GetType();

            if (territory != 0)
            {
                if (Get(cfg, "SelectedZones") is not List<uint> zones) return "У Auto FATE Grind нет SelectedZones — возможно, изменилась его версия.";
                Remember(cfg, "SelectedZones");
                zones.Clear();
                zones.Add(territory);
            }
            Set(cfg, "ApplyClassOnStart", false);
            // FATEs of the chosen zone may be far below the job's level (Urqopacha 90-92 on a level 100 job):
            // AFG's "player level -5..+2" filter would then find nothing and wait forever. Level sync handles it.
            Set(cfg, "LevelRangeFilterEnabled", false);
            Set(cfg, "LevelWindowEnabled", false);

            if (purchases.Count > 0)
            {
                if (Get(cfg, "TradeList") is not IList list) return "У Auto FATE Grind нет TradeList — возможно, изменилась его версия.";
                var entryType = list.GetType().GetGenericArguments()[0];
                Remember(cfg, "TradeList");
                list.Clear();
                foreach (var (item, stopAt) in purchases)
                {
                    var e = Activator.CreateInstance(entryType)!;
                    entryType.GetProperty("ItemId", Any)!.SetValue(e, item);
                    entryType.GetProperty("StopAtCount", Any)!.SetValue(e, stopAt);
                    list.Add(e);
                }
                Set(cfg, "TradeOnCap", true);
                Set(cfg, "TradeThreshold", Math.Clamp(threshold, 1, Planner.GemCap));
                Set(cfg, "KeepGemstonesReserve", 0);
                SetEnum(cfg, "SpendMode", "SpendAll");
                SetEnum(cfg, "AfterTrade", "Stop");
            }
            Save(cfg);
            return null;
        }
        catch (Exception ex)
        {
            return $"Не удалось настроить Auto FATE Grind: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>Puts back everything <see cref="Apply"/> changed.</summary>
    public void Restore()
    {
        if (!HasSavedState) return;
        var cfg = Config(out _);
        if (cfg == null) return;
        try
        {
            var t = cfg.GetType();
            foreach (var (name, value) in saved)
            {
                var prop = t.GetProperty(name, Any);
                if (prop == null) continue;
                if (value is IList copy && prop.GetValue(cfg) is IList live)
                {
                    live.Clear();
                    foreach (var x in copy) live.Add(x);
                }
                else
                {
                    prop.SetValue(cfg, value);
                }
            }
            Save(cfg);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Auto FATE Grind settings restore failed: {ex.Message}");
        }
        saved.Clear();
    }

    private static object? Get(object cfg, string name) => cfg.GetType().GetProperty(name, Any)?.GetValue(cfg);

    private void Remember(object cfg, string name)
    {
        if (saved.ContainsKey(name)) return;
        var value = Get(cfg, name);
        if (value is IList list)
        {
            var copy = (IList)Activator.CreateInstance(list.GetType())!;
            foreach (var x in list) copy.Add(x);
            saved[name] = copy;
        }
        else
        {
            saved[name] = value;
        }
    }

    private void Set(object cfg, string name, object value)
    {
        var prop = cfg.GetType().GetProperty(name, Any);
        if (prop == null) return;
        Remember(cfg, name);
        prop.SetValue(cfg, Convert.ChangeType(value, prop.PropertyType));
    }

    private void SetEnum(object cfg, string name, string member)
    {
        var prop = cfg.GetType().GetProperty(name, Any);
        if (prop == null || !prop.PropertyType.IsEnum) return;
        Remember(cfg, name);
        prop.SetValue(cfg, Enum.Parse(prop.PropertyType, member));
    }

    private static object? Config(out string? error)
    {
        error = null;
        try
        {
            var instance = LazyCrafterLink.FindPluginInstance("AutoFateGrind");
            if (instance == null) { error = "Auto FATE Grind не загружен."; return null; }
            var cfg = instance.GetType().GetProperty("Cfg", Any)?.GetValue(null);
            if (cfg == null) error = "У Auto FATE Grind не нашёл настроек (Plugin.Cfg) — возможно, изменилась его версия.";
            return cfg;
        }
        catch (Exception ex)
        {
            error = $"Не удалось добраться до Auto FATE Grind: {ex.Message}";
            return null;
        }
    }

    private static void Save(object cfg) => cfg.GetType().GetMethod("Save", Any, Type.EmptyTypes)?.Invoke(cfg, null);
}
