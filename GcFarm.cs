using System;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;

namespace CraftMeThis;

/// <summary>
/// Grand Company seal farming through AutoDuty, the way the user's job-leveling SND script drives it:
/// "/autoduty cfg leveling &lt;mode&gt;" + "/autoduty run 1" (leveling mode picks the highest dungeon the job can do),
/// wait for the duty to end, "/autoduty gcturnin" (expert delivery of the looted gear; needs GC rank 6+ and AutoRetainer),
/// "/autoduty autoequip", then again until the seals cover the GC purchases or the run limit is reached.
/// </summary>
public sealed class GcFarm
{
    private enum Step { Equip, Run, WaitEnter, InDuty, TurnIn, AutoEquip, Check }

    private readonly Plugin p;
    private Step step;
    private DateTime stepAt;
    private bool helperSeenRunning;

    public int Runs { get; private set; }
    public string Now { get; private set; } = "";
    public bool Finished { get; private set; }

    public GcFarm(Plugin plugin) => p = plugin;

    public void Begin()
    {
        Runs = 0;
        Finished = false;
        Go(Step.Equip, "Готовлю класс для данжей…");
    }

    private void Go(Step s, string now)
    {
        step = s;
        stepAt = DateTime.UtcNow;
        helperSeenRunning = false;
        Now = now;
    }

    private void Finish(string why)
    {
        Finished = true;
        Now = why;
        Plugin.Chat.Print($"[CraftMeThis] {why}");
    }

    /// <summary>
    /// The highest dungeon of AutoDuty's own leveling list that its CanRun check allows at this level
    /// (AutoDuty.Helpers.LevelingHelper.LevelingDuties + ContentHelper.CanRun), read through reflection.
    /// </summary>
    public static (uint Territory, string Name)? HighestDuty(int level, bool trust, out string why)
    {
        why = "";
        try
        {
            var inst = PluginFinder.FindPluginInstance("AutoDuty");
            if (inst == null) { why = "AutoDuty не загружен"; return null; }
            var asm = inst.GetType().Assembly;
            const BindingFlags any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var duties = asm.GetType("AutoDuty.Helpers.LevelingHelper")?.GetProperty("LevelingDuties", any)?.GetValue(null) as Array;
            var canRun = asm.GetType("AutoDuty.Helpers.ContentHelper")?.GetMethods(any).FirstOrDefault(m => m.Name == "CanRun");
            var modeType = asm.GetType("AutoDuty.Data.Enums+DutyMode");
            if (duties == null || canRun == null || modeType == null) { why = "у AutoDuty не нашёл список данжей — возможно, изменилась его версия"; return null; }
            var mode = Enum.Parse(modeType, trust ? "Trust" : "None");
            var ps = canRun.GetParameters();
            for (var i = duties.Length - 1; i >= 0; i--)
            {
                var c = duties.GetValue(i);
                if (c == null) continue;
                var args = new object?[ps.Length];
                args[0] = c;
                if (ps.Length > 1) args[1] = Convert.ChangeType(level, ps[1].ParameterType);
                if (ps.Length > 2) args[2] = mode;
                for (var k = 3; k < ps.Length; k++) args[k] = ps[k].HasDefaultValue ? ps[k].DefaultValue : null;
                if (canRun.Invoke(null, args) is not true) continue;
                var t = c.GetType();
                var tt = t.GetProperty("TerritoryType", BindingFlags.Instance | BindingFlags.Public)?.GetValue(c) is uint u ? u : 0;
                var name = t.GetProperty("Name", BindingFlags.Instance | BindingFlags.Public)?.GetValue(c) as string ?? $"#{tt}";
                if (tt != 0) return (tt, name);
            }
            why = $"ни один данж из списка прокачки AutoDuty не проходит на уровне {level}";
            return null;
        }
        catch (Exception ex)
        {
            why = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
            return null;
        }
    }

    public static bool AutoDutyStopped()
    {
        try { return Plugin.Pi.GetIpcSubscriber<bool>("AutoDuty.IsStopped").InvokeFunc(); }
        catch { return true; }
    }

    private static bool InDuty => Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56]
                                  || Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>Waits for an AutoDuty helper (gcturnin, autoequip): first for it to start, then for it to finish.</summary>
    private bool HelperDone(TimeSpan timeout, DateTime now)
    {
        if (!AutoDutyStopped()) helperSeenRunning = true;
        if (!helperSeenRunning && now - stepAt < TimeSpan.FromSeconds(15)) return false;
        if (AutoDutyStopped() && Plugin.IsIdleNow()) return true;
        if (now - stepAt <= timeout) return false;
        p.RunCommand("/autoduty stop");
        return true;
    }

    public void Tick(DateTime now)
    {
        if (Finished) return;
        var (have, need) = p.GcSeals();
        switch (step)
        {
            case Step.Equip:
                if (now - stepAt < TimeSpan.FromSeconds(1)) return;
                p.EquipFarmJob(forDungeons: true);
                Go(Step.Run, "Запускаю данж через AutoDuty…");
                return;

            case Step.Run:
            {
                if (now - stepAt < TimeSpan.FromSeconds(3)) return;
                // AutoDuty's leveling mode refuses level 100 ("lvl >= 100 → no leveling duty") and then
                // "/autoduty run 1" fails, so the duty is picked here the same way and started explicitly.
                var trust = p.Config.GcLevelingMode.StartsWith("Trust", StringComparison.OrdinalIgnoreCase);
                var duty = HighestDuty(p.CurrentJobLevel(), trust, out var why);
                if (duty == null)
                {
                    Finish($"Не нашёл данж для AutoDuty ({why}). Фарм печатей остановлен — покупаю, на что хватает.");
                    return;
                }
                var mode = trust ? "Trust" : "Support";
                p.RunCommand($"/autoduty run {mode} {duty.Value.Territory} 1");
                Runs++;
                Go(Step.WaitEnter, $"Пробег {Runs}: {duty.Value.Name} ({mode}) — жду входа…");
                return;
            }

            case Step.WaitEnter:
                if (InDuty) { Go(Step.InDuty, $"Пробег {Runs}/{p.Config.GcMaxRuns}: AutoDuty проходит данж. Печати {have}/{need}."); return; }
                if (now - stepAt > TimeSpan.FromSeconds(30) && AutoDutyStopped() && Plugin.IsIdleNow())
                    Finish("AutoDuty не смог зайти в данж (нет подходящего для уровня, режим или ilvl). Фарм печатей остановлен — покупаю, на что хватает.");
                else if (now - stepAt > TimeSpan.FromMinutes(15))
                {
                    p.RunCommand("/autoduty stop");
                    Finish("За 15 минут так и не вошёл в данж. Фарм печатей остановлен.");
                }
                return;

            case Step.InDuty:
                Now = $"Пробег {Runs}/{p.Config.GcMaxRuns}: AutoDuty проходит данж. Печати {have}/{need}.";
                if (InDuty || !AutoDutyStopped() || !Plugin.IsIdleNow())
                {
                    if (now - stepAt > TimeSpan.FromMinutes(60))
                    {
                        p.RunCommand("/autoduty stop");
                        Finish("Данж идёт больше часа — остановил AutoDuty и фарм печатей.");
                    }
                    return;
                }
                p.RunCommand("/autoduty gcturnin");
                Go(Step.TurnIn, $"Пробег {Runs}: сдаю лут в Grand Company…");
                return;

            case Step.TurnIn:
                if (!HelperDone(TimeSpan.FromMinutes(15), now)) return;
                p.RunCommand("/autoduty autoequip");
                Go(Step.AutoEquip, $"Пробег {Runs}: надеваю лучшее после сдачи…");
                return;

            case Step.AutoEquip:
                if (!HelperDone(TimeSpan.FromMinutes(2), now)) return;
                Go(Step.Check, "");
                return;

            case Step.Check:
                if (have >= need) { Finish($"Печатей хватает ({have}/{need}) после {Runs} пробег(ов)."); return; }
                if (Runs >= p.Config.GcMaxRuns) { Finish($"Сделано {Runs} пробегов (лимит), печатей {have}/{need}. Покупаю, на что хватает."); return; }
                Go(Step.Run, $"Печатей {have}/{need} — ещё пробег.");
                return;
        }
    }
}
