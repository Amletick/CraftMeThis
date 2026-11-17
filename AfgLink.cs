using System;
using System.Collections.Generic;
using System.Reflection;

namespace CraftMeThis;

/// <summary>
/// Auto FATE Grind has no IPC. Its settings live in the static AutoFateGrind.Plugin.Cfg (Configuration), so the farm
/// zone is set there through reflection before "/afg start", and the user's own zones are put back afterwards.
/// Its own class queue is switched off for the run because this plugin picks the job itself.
/// </summary>
public sealed class AfgLink
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private List<uint>? savedZones;
    private bool? savedApplyClass;

    public bool HasSavedState => savedZones != null || savedApplyClass != null;

    /// <returns>null on success, otherwise what went wrong.</returns>
    public string? Apply(uint territory)
    {
        var cfg = Config(out var err);
        if (cfg == null) return err;
        try
        {
            var t = cfg.GetType();
            var zonesProp = t.GetProperty("SelectedZones", Any);
            var applyProp = t.GetProperty("ApplyClassOnStart", Any);
            if (zonesProp?.GetValue(cfg) is not List<uint> zones) return "У Auto FATE Grind нет SelectedZones — возможно, изменилась его версия.";

            savedZones ??= new List<uint>(zones);
            zones.Clear();
            zones.Add(territory);

            if (applyProp != null && applyProp.GetValue(cfg) is bool apply)
            {
                savedApplyClass ??= apply;
                applyProp.SetValue(cfg, false);
            }
            Save(cfg);
            return null;
        }
        catch (Exception ex)
        {
            return $"Не удалось настроить Auto FATE Grind: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";
        }
    }

    /// <summary>Puts back the zones and class-queue switch the user had before the run.</summary>
    public void Restore()
    {
        if (!HasSavedState) return;
        var cfg = Config(out _);
        if (cfg == null) return;
        try
        {
            var t = cfg.GetType();
            if (savedZones != null && t.GetProperty("SelectedZones", Any)?.GetValue(cfg) is List<uint> zones)
            {
                zones.Clear();
                zones.AddRange(savedZones);
            }
            if (savedApplyClass is { } apply) t.GetProperty("ApplyClassOnStart", Any)?.SetValue(cfg, apply);
            Save(cfg);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Auto FATE Grind settings restore failed: {ex.Message}");
        }
        savedZones = null;
        savedApplyClass = null;
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
