using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;

namespace CraftMeThis;

/// <summary>
/// Grand Company seal farming through AutoDuty: run a dungeon ("/autoduty run Support|Trust &lt;territory&gt; 1"),
/// wait for it to end, "/autoduty gcturnin" (AutoDuty walks to the GC and AutoRetainer does the expert delivery;
/// needs GC rank 6+), wait for that helper to finish, then again until the seals cover the GC purchases, the run limit
/// is reached, or turn-ins stop giving seals. No auto-equip: this is a farm, not leveling.
/// </summary>
public sealed class GcFarm
{
    private enum Step { Equip, Run, WaitEnter, InDuty, TurnIn, Check }

    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly Plugin p;
    private Step step;
    private DateTime stepAt;
    private bool helperSeenRunning;
    private long sealsBeforeTurnIn;
    private int emptyTurnIns;

    public int Runs { get; private set; }
    public string Now { get; private set; } = "";
    public bool Finished { get; private set; }
    public string LastDuty { get; private set; } = "";

    public GcFarm(Plugin plugin) => p = plugin;

    public void Begin()
    {
        Runs = 0;
        emptyTurnIns = 0;
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

    // ---------------------------------------------------------------- AutoDuty data (reflection)

    private static Assembly? AutoDutyAssembly() => PluginFinder.FindPluginInstance("AutoDuty")?.GetType().Assembly;

    /// <summary>
    /// Dungeons of AutoDuty's leveling list that its own CanRun check allows at this level, highest first
    /// (AutoDuty.Helpers.LevelingHelper.LevelingDuties + ContentHelper.CanRun). Its leveling mode itself refuses level 100.
    /// </summary>
    public static List<(uint Territory, string Name)> RunnableDuties(int level, bool trust, out string why)
    {
        why = "";
        var list = new List<(uint, string)>();
        try
        {
            var asm = AutoDutyAssembly();
            if (asm == null) { why = "AutoDuty не загружен"; return list; }
            var duties = asm.GetType("AutoDuty.Helpers.LevelingHelper")?.GetProperty("LevelingDuties", AnyStatic)?.GetValue(null) as Array;
            var canRun = asm.GetType("AutoDuty.Helpers.ContentHelper")?.GetMethods(AnyStatic).FirstOrDefault(m => m.Name == "CanRun");
            var modeType = asm.GetType("AutoDuty.Data.Enums+DutyMode");
            if (duties == null || canRun == null || modeType == null) { why = "у AutoDuty не нашёл список данжей — возможно, изменилась его версия"; return list; }
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
                if (tt != 0) list.Add((tt, name));
            }
            if (list.Count == 0) why = $"ни один данж из списка AutoDuty не проходит на уровне {level}";
        }
        catch (Exception ex)
        {
            why = (ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message;
        }
        return list;
    }

    /// <summary>
    /// All dungeons of AutoDuty's leveling list with their level and item level (for the settings combo; CanRun is not
    /// used here because it rejects everything while a crafter is equipped).
    /// </summary>
    public static List<(uint Territory, string Name, int Level, uint ItemLevel)> AllDuties()
    {
        var list = new List<(uint, string, int, uint)>();
        try
        {
            if (AutoDutyAssembly()?.GetType("AutoDuty.Helpers.LevelingHelper")?.GetProperty("LevelingDuties", AnyStatic)?.GetValue(null) is not Array duties)
                return list;
            foreach (var c in duties)
            {
                if (c == null) continue;
                var t = c.GetType();
                var pub = BindingFlags.Instance | BindingFlags.Public;
                var tt = t.GetProperty("TerritoryType", pub)?.GetValue(c) is uint u ? u : 0;
                var name = t.GetProperty("Name", pub)?.GetValue(c) as string ?? $"#{tt}";
                var lvl = t.GetProperty("ClassJobLevelRequired", pub)?.GetValue(c) is byte b ? b : 0;
                var ilvl = t.GetProperty("ItemLevelRequired", pub)?.GetValue(c) is uint il ? il : 0;
                if (tt != 0) list.Add((tt, name, lvl, ilvl));
            }
        }
        catch
        {
            // AutoDuty missing or changed: empty list, the run falls back to "highest"
        }
        return list.OrderByDescending(d => d.Item3).ThenByDescending(d => d.Item4).ToList();
    }

    /// <summary>
    /// Is AutoDuty's GC turn-in helper running? (ActiveHelperBase&lt;GCTurninHelper, …&gt;.State == Running).
    /// null when it cannot be read; then "AutoDuty stopped + character idle" is used instead.
    /// </summary>
    private static bool? TurnInRunning()
    {
        try
        {
            var baseType = AutoDutyAssembly()?.GetType("AutoDuty.Helpers.GCTurninHelper")?.BaseType;
            var state = baseType?.GetField("State", AnyStatic)?.GetValue(null);
            return state == null ? null : state.ToString() == "Running";
        }
        catch
        {
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

    /// <summary>Waits for the turn-in helper: first for it to start (it walks to the GC first), then for it to finish.</summary>
    private bool TurnInDone(DateTime now)
    {
        var running = TurnInRunning();
        if (running == true || (running == null && !AutoDutyStopped())) helperSeenRunning = true;
        if (!helperSeenRunning)
            return now - stepAt > TimeSpan.FromSeconds(30) && Plugin.IsIdleNow(); // never started (nothing to hand in?)
        var finished = running == null ? AutoDutyStopped() : running == false;
        if (finished && Plugin.IsIdleNow()) return true;
        if (now - stepAt <= TimeSpan.FromMinutes(15)) return false;
        p.RunCommand("/autoduty stop");
        return true;
    }

    // ---------------------------------------------------------------- loop

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
                var trust = p.Config.GcLevelingMode.StartsWith("Trust", StringComparison.OrdinalIgnoreCase);
                var duties = RunnableDuties(p.CurrentJobLevel(), trust, out var why);
                if (duties.Count == 0)
                {
                    Finish($"Не нашёл данж для AutoDuty ({why}). Фарм печатей остановлен — покупаю, на что хватает.");
                    return;
                }
                // The dungeon chosen in the settings if it can be run at this level, otherwise the highest one.
                var duty = duties.FirstOrDefault(d => d.Territory == p.Config.GcDutyTerritory);
                if (duty.Territory == 0)
                {
                    if (p.Config.GcDutyTerritory != 0) Plugin.Chat.Print("[CraftMeThis] Выбранный данж сейчас недоступен (уровень/режим) — беру самый высокий.");
                    duty = duties[0];
                }
                var mode = trust ? "Trust" : "Support";
                LastDuty = duty.Name;
                p.RunCommand($"/autoduty run {mode} {duty.Territory} 1");
                Runs++;
                Go(Step.WaitEnter, $"Пробег {Runs}: {duty.Name} ({mode}) — жду входа…");
                return;
            }

            case Step.WaitEnter:
                if (InDuty) { Go(Step.InDuty, $"Пробег {Runs}/{p.Config.GcMaxRuns}: {LastDuty}. Печати {have}/{need}."); return; }
                if (now - stepAt > TimeSpan.FromSeconds(30) && AutoDutyStopped() && Plugin.IsIdleNow())
                    Finish("AutoDuty не смог зайти в данж (уровень, режим или ilvl). Фарм печатей остановлен — покупаю, на что хватает.");
                else if (now - stepAt > TimeSpan.FromMinutes(15))
                {
                    p.RunCommand("/autoduty stop");
                    Finish("За 15 минут так и не вошёл в данж. Фарм печатей остановлен.");
                }
                return;

            case Step.InDuty:
                Now = $"Пробег {Runs}/{p.Config.GcMaxRuns}: {LastDuty}. Печати {have}/{need}.";
                if (InDuty || !AutoDutyStopped() || !Plugin.IsIdleNow())
                {
                    if (now - stepAt > TimeSpan.FromMinutes(60))
                    {
                        p.RunCommand("/autoduty stop");
                        Finish("Данж идёт больше часа — остановил AutoDuty и фарм печатей.");
                    }
                    return;
                }
                sealsBeforeTurnIn = have;
                p.RunCommand("/autoduty gcturnin");
                Go(Step.TurnIn, $"Пробег {Runs}: сдаю лут в Grand Company…");
                return;

            case Step.TurnIn:
                if (!TurnInDone(now)) return;
                Go(Step.Check, "");
                return;

            case Step.Check:
            {
                // Give the seal count a moment to update after the delivery window closes.
                if (now - stepAt < TimeSpan.FromSeconds(3)) return;
                var gained = have - sealsBeforeTurnIn;
                if (gained <= 0)
                {
                    emptyTurnIns++;
                    Plugin.Chat.Print($"[CraftMeThis] Сдача в GC не принесла печатей (пробег {Runs}). Проверь в AutoRetainer → Expert Delivery, что разрешена сдача этого шмота, и что ранг GC 6+.");
                    if (emptyTurnIns >= 2) { Finish("Две сдачи подряд без печатей — фарм печатей остановлен, покупаю, на что хватает."); return; }
                }
                else
                {
                    emptyTurnIns = 0;
                }
                if (have >= need) { Finish($"Печатей хватает ({have}/{need}) после {Runs} пробег(ов)."); return; }
                if (Runs >= p.Config.GcMaxRuns) { Finish($"Сделано {Runs} пробегов (лимит), печатей {have}/{need}. Покупаю, на что хватает."); return; }
                Go(Step.Run, $"+{Math.Max(0, gained)} печатей, всего {have}/{need} — ещё пробег.");
                return;
            }
        }
    }
}
