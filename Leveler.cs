using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace CraftMeThis;

/// <summary>
/// Auto-leveling (beta), a port of the user's SND script job_leveler.lua v0.8:
///  * levels 1–14: Auto FATE Grind until the next quest threshold, then "/afg stop soft";
///  * 15+: one AutoDuty leveling dungeon at a time ("/autoduty cfg leveling Support|Trust_*" + "run 1"),
///    Trust first from 71, Support as fallback; if no dungeon lets the job in, autoequip and try once more;
///    after each dungeon "/autoduty autoequip", and "/autoduty gcturnin" when the bags run low on free slots;
///  * whenever the level crosses a class/job quest level: Questionable ("/qst start") until the character has been idle
///    for a while, then "/qst stop". If Questionable starts AutoDuty itself (MSQ leveling), everything is stopped.
///  * a stall watchdog: no movement, combat or casting for N minutes while AutoDuty "runs" → stop and retry.
/// Job quests are the fragile part (Questionable sometimes leaves them half done) — hence beta.
/// </summary>
public sealed class Leveler
{
    private static readonly int[] QuestLevels = { 1, 5, 10, 15, 20, 25, 30, 35, 40, 45, 50, 52, 54, 56, 58, 60, 63, 65, 68, 70,
                                                  72, 74, 76, 78, 80, 83, 85, 88, 90, 92, 94, 96, 98, 100 };

    private enum Step
    {
        Decide, QuestStart, Quests, QuestStop, Fates, FateStop, DungeonCfg, DungeonRun, DungeonEnter, DungeonIn,
        DungeonLeave, AfterEquip, AfterTurnIn, AfterEquip2, RetryEquip, Pause,
    }

    private readonly Plugin p;
    private Step step = Step.Decide;
    private DateTime stepAt, t0;
    private DateTime? idleSince;
    private bool helperSeen;
    private uint jobId;
    private int lastQuestLevel;
    private int fateGoal;
    private List<string> modes = new();
    private int modeIndex, attempt;
    private Vector3? stallPos;
    private DateTime stallAt;
    private Step afterPause;

    public bool Running { get; private set; }
    public string Now { get; private set; } = "Не запущен.";
    public List<string> Log { get; } = new();

    public Leveler(Plugin plugin) => p = plugin;

    private Configuration C => p.Config;

    // ---------------------------------------------------------------- helpers

    private static int Level()
    {
        var job = Jobs.CurrentJob();
        return PlayerLevel(job);
    }

    private static int PlayerLevel(uint jobId)
    {
        var info = Plugin.Instance?.Jobs.Get(jobId);
        return info == null ? 0 : Jobs.Level(info);
    }

    private static bool InDuty => Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56];

    private static bool Busy =>
        Plugin.Condition[ConditionFlag.BetweenAreas] || Plugin.Condition[ConditionFlag.BetweenAreas51]
        || Plugin.Condition[ConditionFlag.Occupied] || Plugin.Condition[ConditionFlag.OccupiedInQuestEvent]
        || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Plugin.Condition[ConditionFlag.Casting]
        || Plugin.Condition[ConditionFlag.Unconscious];

    private static bool InCombatOrCasting => Plugin.Condition[ConditionFlag.InCombat] || Plugin.Condition[ConditionFlag.Casting];

    private bool Idle => !Busy && !InDuty && !InCombatOrCasting && !moving;

    // Movement is sampled once per tick (one second apart).
    private Vector3 lastPos;
    private bool moving;
    private void SampleMovement()
    {
        var pos = Plugin.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        moving = Vector3.DistanceSquared(pos, lastPos) > 0.01f;
        lastPos = pos;
    }

    private static unsafe bool InFate()
    {
        var fm = FateManager.Instance();
        return fm != null && fm->CurrentFate != null;
    }

    private static unsafe int FreeSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 99 : (int)im->GetEmptySlotsInBag();
    }

    private void Go(Step s, string now)
    {
        step = s;
        stepAt = DateTime.UtcNow;
        idleSince = null;
        helperSeen = false;
        if (now.Length > 0) Say(now);
    }

    private void Say(string text)
    {
        Now = text;
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        if (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        Plugin.Chat.Print($"[CraftMeThis · автоуровень] {text}");
    }

    private void Cmd(string c) => p.RunCommand(c);

    private void Stop(string why)
    {
        Running = false;
        Cmd("/qst stop");
        if (!GcFarm.AutoDutyStopped()) Cmd("/autoduty stop");
        Say(why);
        p.LevelerFinished(why);
    }

    /// <summary>
    /// AutoDuty helper (autoequip, gcturnin): wait for it to start, then for it to stop and the character to idle 15 s.
    /// Watches the helper's own state (GcFarm.HelperRunning): AutoDuty.IsStopped stays true while the GC turn-in is still
    /// walking to the NPC, and the next command (autoequip) then broke the delivery (AutoRetainer TaskTimeoutException).
    /// </summary>
    private bool HelperDone(DateTime now, int maxMinutes, string helper)
    {
        var running = GcFarm.HelperRunning(helper);
        if (running == true || (running == null && !GcFarm.AutoDutyStopped())) helperSeen = true;
        var startWait = helper == "GCTurninHelper" ? 30 : 15;
        if (!helperSeen && now - stepAt < TimeSpan.FromSeconds(startWait)) return false;
        var stopped = running == null ? GcFarm.AutoDutyStopped() : running == false;
        if (stopped && Idle)
        {
            idleSince ??= now;
            if (now - idleSince >= TimeSpan.FromSeconds(15)) return true;
        }
        else idleSince = null;
        if (now - stepAt <= TimeSpan.FromMinutes(maxMinutes)) return false;
        Say("Служебное действие AutoDuty зависло — останавливаю его.");
        Cmd("/autoduty stop");
        return true;
    }

    private bool Stalled(DateTime now)
    {
        var pos = Plugin.Objects.LocalPlayer?.Position;
        if (pos == null) return false;
        if (stallPos == null || Vector3.DistanceSquared(pos.Value, stallPos.Value) > 4 || InCombatOrCasting)
        {
            stallPos = pos;
            stallAt = now;
            return false;
        }
        return now - stallAt > TimeSpan.FromMinutes(Math.Max(1, C.LevelStallMinutes));
    }

    private static bool QuestCrossed(int from, int to) => QuestLevels.Any(l => l > from && l <= to);

    private static int NextQuestLevel(int lvl)
    {
        foreach (var l in QuestLevels) if (l > lvl) return Math.Min(l, 15);
        return 15;
    }

    // ---------------------------------------------------------------- control

    public void Start()
    {
        if (Running) return;
        jobId = Jobs.CurrentJob();
        var job = p.Jobs.Get(jobId);
        if (job == null)
        {
            Say("Нужна боевая профа (не лимитированная). Стоп.");
            return;
        }
        Running = true;
        lastQuestLevel = 0; // the first pass always checks the available quests
        Log.Clear();
        Say($"Старт: {job.Abbr} {Level()} → {C.LevelTarget}.");
        Cmd("/afg stop");
        Cmd("/qst stop");
        if (!GcFarm.AutoDutyStopped() && !InDuty) Cmd("/autoduty stop");
        Go(Step.Pause, "");
        afterPause = Step.Decide;
    }

    public void Stop() { if (Running) Stop("Остановлено вручную."); }

    // ---------------------------------------------------------------- loop

    public void Tick(DateTime now)
    {
        if (!Running) return;
        SampleMovement();
        if (Jobs.CurrentJob() != jobId && step is Step.Decide or Step.Pause)
        {
            Stop("Профа сменилась — стоп.");
            return;
        }

        switch (step)
        {
            case Step.Pause:
                if (now - stepAt < TimeSpan.FromSeconds(3) || Busy) return;
                Go(afterPause, "");
                return;

            case Step.Decide:
            {
                var lvl = Level();
                if (QuestCrossed(lastQuestLevel, lvl))
                {
                    Cmd("/qst start");
                    Go(Step.Quests, $"Уровень {lvl} — Questionable делает класс/джоб-квесты.");
                }
                else if (lvl >= C.LevelTarget)
                {
                    Stop($"Готово: {p.Jobs.Get(jobId)?.Abbr} {lvl}.");
                }
                else if (lvl < 15)
                {
                    fateGoal = NextQuestLevel(lvl);
                    Cmd("/afg start");
                    t0 = now;
                    Go(Step.Fates, $"Уровень {lvl} — FATE через Auto FATE Grind до {fateGoal}.");
                }
                else
                {
                    modes = lvl >= 71 ? new() { C.LevelTrustMode, "Support" } : new() { "Support" };
                    modeIndex = 0;
                    attempt = 1;
                    Go(Step.DungeonCfg, "");
                }
                return;
            }

            case Step.Quests:
                if (now - stepAt < TimeSpan.FromSeconds(5)) return;
                if (!GcFarm.AutoDutyStopped())
                {
                    Say("Questionable сам запустил прокачку под MSQ — забираю управление. Выключи в нём «Run AutoDuty Leveling mode when underleveled for MSQ».");
                    Cmd("/qst stop");
                    if (!InDuty) Cmd("/autoduty stop");
                    lastQuestLevel = Level();
                    Go(Step.QuestStop, "");
                    return;
                }
                if (Idle)
                {
                    idleSince ??= now;
                    if (now - idleSince < TimeSpan.FromSeconds(Math.Max(10, C.LevelQuestIdleSeconds))) return;
                }
                else
                {
                    idleSince = null;
                    if (now - stepAt < TimeSpan.FromMinutes(Math.Max(5, C.LevelQuestMaxMinutes))) return;
                    Say("Квестовая фаза упёрлась в лимит времени, иду дальше.");
                }
                Cmd("/qst stop");
                lastQuestLevel = Level();
                Go(Step.QuestStop, "");
                return;

            case Step.QuestStop:
                if (InDuty) { Now = "Жду конца данжа, который запустил Questionable…"; return; }
                if (now - stepAt < TimeSpan.FromSeconds(3) || Busy) return;
                Go(Step.Decide, "");
                return;

            case Step.Fates:
                Now = $"FATE через Auto FATE Grind: уровень {Level()} → {fateGoal}.";
                if (Level() >= fateGoal)
                {
                    Cmd("/afg stop soft");
                    Go(Step.FateStop, $"Достигнут {Level()} — прошу AFG закончить текущий FATE.");
                    return;
                }
                if (now - t0 > TimeSpan.FromMinutes(Math.Max(10, C.LevelFateMaxMinutes)))
                {
                    Cmd("/afg stop");
                    Stop("FATE-фаза упёрлась в лимит времени — стоп.");
                }
                return;

            case Step.FateStop:
                if (!InFate() && Idle)
                {
                    idleSince ??= now;
                    if (now - idleSince >= TimeSpan.FromSeconds(10)) Go(Step.Decide, "");
                }
                else idleSince = null;
                if (step == Step.FateStop && now - stepAt > TimeSpan.FromMinutes(15))
                {
                    Cmd("/afg stop");
                    Go(Step.Decide, "");
                }
                return;

            case Step.DungeonCfg:
                Cmd($"/autoduty cfg leveling {modes[modeIndex]}");
                Go(Step.DungeonRun, $"Уровень {Level()} — данж через AutoDuty ({modes[modeIndex]}).");
                return;

            case Step.DungeonRun:
                if (now - stepAt < TimeSpan.FromSeconds(1)) return;
                Cmd("/autoduty run 1");
                t0 = now;
                Go(Step.DungeonEnter, "");
                return;

            case Step.DungeonEnter:
                if (now - stepAt < TimeSpan.FromSeconds(5)) return;
                if (InDuty)
                {
                    stallPos = null;
                    Go(Step.DungeonIn, "");
                    Now = $"AutoDuty проходит данж ({modes[modeIndex]}), уровень {Level()}.";
                    return;
                }
                if ((now - t0 > TimeSpan.FromSeconds(20) && GcFarm.AutoDutyStopped() && !Busy) || now - t0 > TimeSpan.FromMinutes(3))
                {
                    if (!GcFarm.AutoDutyStopped()) Cmd("/autoduty stop");
                    NoEntry();
                }
                return;

            case Step.DungeonIn:
                if (InDuty || !GcFarm.AutoDutyStopped() || Busy)
                {
                    Now = $"AutoDuty проходит данж ({modes[modeIndex]}), уровень {Level()}.";
                    if (now - t0 > TimeSpan.FromMinutes(Math.Max(30, C.LevelDungeonMaxMinutes)))
                    {
                        Cmd("/autoduty stop");
                        Stop("Данж длится слишком долго — остановил AutoDuty и автоуровень.");
                        return;
                    }
                    if (Stalled(now))
                    {
                        Cmd("/autoduty stop");
                        Go(Step.DungeonLeave, $"Персонаж стоит на месте {C.LevelStallMinutes} мин — останавливаю AutoDuty и пробую заново.");
                    }
                    return;
                }
                if (now - stepAt < TimeSpan.FromSeconds(5)) return;
                Cmd("/autoduty autoequip");
                Go(Step.AfterEquip, "Данж пройден — надеваю лучшее.");
                return;

            case Step.DungeonLeave:
                // AutoDuty stopped inside a stuck duty: give it time to leave, then retry from the top.
                if ((InDuty || Busy) && now - stepAt < TimeSpan.FromMinutes(2)) return;
                if (InDuty) { Stop("Застрял в данже и не смог выйти — выйди вручную и перезапусти."); return; }
                Go(Step.Pause, "");
                afterPause = Step.Decide;
                return;

            case Step.AfterEquip:
                if (!HelperDone(now, 2, "AutoEquipHelper")) return;
                if (C.LevelGcFreeSlots > 0 && FreeSlots() < C.LevelGcFreeSlots)
                {
                    Cmd("/autoduty gcturnin");
                    Go(Step.AfterTurnIn, $"Свободно {FreeSlots()} слотов — сдаю лут в Grand Company.");
                    return;
                }
                Go(Step.Decide, "");
                return;

            case Step.AfterTurnIn:
                if (!HelperDone(now, 15, "GCTurninHelper")) return;
                Cmd("/autoduty autoequip");
                Go(Step.AfterEquip2, "");
                return;

            case Step.AfterEquip2:
                if (!HelperDone(now, 2, "AutoEquipHelper")) return;
                Go(Step.Decide, "");
                return;

            case Step.RetryEquip:
                if (!HelperDone(now, 2, "AutoEquipHelper")) return;
                Go(Step.DungeonCfg, "");
                return;
        }
    }

    private void NoEntry()
    {
        Say($"AutoDuty не смог зайти в данж в режиме {modes[modeIndex]}.");
        modeIndex++;
        if (modeIndex < modes.Count) { Go(Step.DungeonCfg, ""); return; }
        if (attempt == 1)
        {
            attempt = 2;
            modeIndex = 0;
            Cmd("/autoduty autoequip");
            Go(Step.RetryEquip, "Никуда не пускает — надеваю рекомендуемый шмот и пробую снова.");
            return;
        }
        Stop($"Стоп: ни в один данж для {p.Jobs.Get(jobId)?.Abbr} {Level()} не пустило даже после смены шмота. Скорее всего, не хватает item level.");
    }
}
