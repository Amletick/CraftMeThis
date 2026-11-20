using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

/// <summary>The four scrips that collectables pay; CollectablesShopRewardScrip.Currency is the index.</summary>
public enum ScripKind { OrangeCrafter, PurpleCrafter, OrangeGatherer, PurpleGatherer }

/// <summary>A collectable that pays scrips: crafted (recipe) or gathered/fished.</summary>
public sealed record Collectable(uint ItemId, string Name, ScripKind Kind, int Level, int Low, int Mid, int High,
                                 uint RecipeId, uint CraftJob, bool Fish);

/// <summary>
/// Scrip farming on top of GatherBuddy Reborn's collectables automation:
///  * crafter scrips — an ephemeral Vulcan crafting list with the best collectable recipe the job can make;
///  * gatherer scrips — GatherBuddy's temporary gather list (CraftingGatherBridge.CreateGatherListForMissingIngredients)
///    with the best collectable the gatherer level allows, auto-gather on;
///  * turn-ins — GatherBuddy's own (CollectableConfig.AutoTurnInCollectables during the run, then a final
///    CollectableManager.Start for what is left), until the scrip count reaches the target.
/// Purchases are not done here: the cart's vendor list (Vulcan) buys with the scrips afterwards.
/// </summary>
public sealed class ScripFarm
{
    public static readonly (ScripKind Kind, ushort Currency, uint ItemId, string Name, bool Crafter)[] Kinds =
    {
        (ScripKind.OrangeCrafter, 6, 41784, "Оранжевые крафтерские", true),
        (ScripKind.PurpleCrafter, 2, 33913, "Фиолетовые крафтерские", true),
        (ScripKind.OrangeGatherer, 7, 41785, "Оранжевые собирательские", false),
        (ScripKind.PurpleGatherer, 4, 33914, "Фиолетовые собирательские", false),
    };

    public static ScripKind? KindOfItem(uint scripItemId) =>
        Kinds.FirstOrDefault(k => k.ItemId == scripItemId) is { ItemId: not 0 } k ? k.Kind : null;

    public static (ScripKind Kind, ushort Currency, uint ItemId, string Name, bool Crafter) Info(ScripKind kind) => Kinds.First(k => k.Kind == kind);

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const int MaxCraftsPerBatch = 40;

    private readonly Plugin p;
    private readonly List<Collectable> all = new();
    private readonly Dictionary<uint, int> gatherLevel = new();
    private readonly Dictionary<uint, uint> craftJobExp = new(); // ClassJob id → ExpArrayIndex

    // run state
    private enum Step { Start, Working, TurnIn, Check }
    private Step step;
    private DateTime stepAt;
    private bool? savedAutoTurnIn;
    private int batches, idleBatches;
    private int before;
    private readonly HashSet<uint> tried = new();
    private bool allowFish;
    private uint preferred;
    // progress watchdog for one batch: scrips + collectables in the bags
    private (int Scrips, int Items, int Queue, int Done, bool Waiting) lastProgress;
    private DateTime lastProgressAt;
    private static readonly TimeSpan BatchStall = TimeSpan.FromMinutes(12);
    private readonly Dictionary<uint, (DateTime At, List<uint> Missing)> missingCache = new();

    public ScripKind Kind { get; private set; }
    public int Target { get; private set; }
    public Collectable? Item { get; private set; }
    public bool Running { get; private set; }
    public bool Finished { get; private set; }
    public string Now { get; private set; } = "";

    public ScripFarm(Plugin plugin)
    {
        p = plugin;
        var items = Plugin.Data.GetExcelSheet<Item>();
        var recipeByItem = Plugin.Data.GetExcelSheet<Recipe>().Where(r => r.ItemResult.RowId != 0)
                                 .GroupBy(r => r.ItemResult.RowId).ToDictionary(g => g.Key, g => g.First());
        foreach (var g in Plugin.Data.GetExcelSheet<GatheringItem>())
            if (g.Item.RowId != 0 && g.GatheringItemLevel.IsValid) gatherLevel.TryAdd(g.Item.RowId, g.GatheringItemLevel.Value.GatheringItemLevel);
        var fish = new HashSet<uint>(Plugin.Data.GetExcelSheet<FishParameter>().Select(f => f.Item.RowId));
        foreach (var j in Plugin.Data.GetExcelSheet<ClassJob>())
            if (j.ExpArrayIndex >= 0) craftJobExp[j.RowId] = (uint)j.ExpArrayIndex;

        foreach (var sub in Plugin.Data.GetSubrowExcelSheet<CollectablesShopItem>())
        foreach (var c in sub)
        {
            if (c.Item.RowId == 0 || !c.CollectablesShopRewardScrip.IsValid) continue;
            var rw = c.CollectablesShopRewardScrip.Value;
            var kind = Kinds.FirstOrDefault(k => k.Currency == rw.Currency);
            if (kind.ItemId == 0 || all.Any(a => a.ItemId == c.Item.RowId && a.Kind == kind.Kind)) continue;
            var id = c.Item.RowId;
            var name = items.TryGetRow(id, out var it) ? it.Name.ExtractText() : $"#{id}";
            if (kind.Crafter)
            {
                if (!recipeByItem.TryGetValue(id, out var r)) continue;
                // Vulcan crafts a collectable only when the item is always collectable (the "Rarefied …" line); it never
                // turns on Collectable Synthesis, so e.g. Bluespirit Glaives come out as plain items nobody takes.
                if (!items.TryGetRow(id, out var ac) || !ac.AlwaysCollectable) continue;
                all.Add(new Collectable(id, name, kind.Kind, r.RecipeLevelTable.Value.ClassJobLevel, rw.LowReward, rw.MidReward, rw.HighReward,
                                        r.RowId, 8 + r.CraftType.RowId, false));
            }
            else
            {
                var isFish = fish.Contains(id);
                if (!isFish && !gatherLevel.ContainsKey(id)) continue;
                all.Add(new Collectable(id, name, kind.Kind, isFish ? c.LevelMax : gatherLevel[id], rw.LowReward, rw.MidReward, rw.HighReward,
                                        0, 0, isFish));
            }
        }
    }

    // ---------------------------------------------------------------- data

    public static int Have(ScripKind kind) => Plugin.CurrencyCount(Info(kind).ItemId);

    private unsafe int JobLevel(uint classJobId)
    {
        var ps = PlayerState.Instance();
        if (ps == null || !craftJobExp.TryGetValue(classJobId, out var idx) || idx >= ps->ClassJobLevels.Length) return 0;
        return ps->ClassJobLevels[(int)idx];
    }

    /// <summary>Gatherer level used for gathered collectables: the best of Miner/Botanist (Fisher for fish).</summary>
    private int GatherLevel(bool fish) => fish ? JobLevel(18) : Math.Max(JobLevel(16), JobLevel(17));

    /// <summary>
    /// Collectables of a kind that the character can make now: those whose materials can all be obtained first
    /// (bags, gathering, gil vendors), then by payout. Fish only when allowed and GatherBuddy may auto-fish
    /// (it refuses fish in auto-gather lists while its fishing data collection opt-in is off).
    /// </summary>
    public List<Collectable> Options(ScripKind kind, bool allowFish)
    {
        var fishOk = allowFish && FishingAllowedInGbr();
        return all.Where(c => c.Kind == kind && (c.RecipeId != 0 ? JobLevel(c.CraftJob) >= c.Level : (fishOk || !c.Fish) && GatherLevel(c.Fish) >= c.Level))
                  .OrderBy(c => Missing(c).Count > 0).ThenByDescending(c => c.Mid).ThenByDescending(c => c.Level).ToList();
    }

    /// <summary>
    /// Raw materials of one craft of a collectable that nothing in the pipeline will bring: not in the bags, not
    /// gatherable at the character's level, not sold for gil (mob drops, tribe items...). Vulcan skips such recipes.
    /// </summary>
    public List<uint> Missing(Collectable c)
    {
        if (c.RecipeId == 0) return new();
        var now = DateTime.UtcNow;
        if (missingCache.TryGetValue(c.RecipeId, out var cached) && now - cached.At < TimeSpan.FromSeconds(30)) return cached.Missing;
        var missing = new List<uint>();
        try
        {
            var plan = p.Planner.Build(new[] { new CartLine(c.RecipeId, 1) }, Plugin.BagCount, _ => false);
            var gl = GatherLevel(false);
            foreach (var (id, qty) in plan.Leaves)
            {
                if (qty <= 0 || id <= 19) continue;
                if (gatherLevel.TryGetValue(id, out var lvl) && gl >= lvl) continue;
                if (p.Vulcan.PriceOf(id) is { Group: "Gil" }) continue;
                missing.Add(id);
            }
        }
        catch
        {
            // planner not ready: treat as obtainable
        }
        missingCache[c.RecipeId] = (now, missing);
        return missing;
    }

    /// <summary>The largest single turn-in among the collectables the farm may use for this kind (0 when none).</summary>
    public int MaxReward(ScripKind kind, bool allowFish) =>
        Options(kind, allowFish).Where(o => Missing(o).Count == 0).Select(o => Math.Max(o.High, o.Mid)).DefaultIfEmpty(0).Max();

    public string MissingText(Collectable c) => string.Join(", ", Missing(c).Select(p.Planner.ItemName));

    /// <summary>GatherBuddy's AutoGatherConfig.FishDataCollection: without it auto-gather drops fish from its lists.</summary>
    public static bool FishingAllowedInGbr()
    {
        try
        {
            var cfg = GbrConfig();
            var ag = cfg?.GetType().GetProperty("AutoGatherConfig", Any)?.GetValue(cfg);
            return ag?.GetType().GetProperty("FishDataCollection", Any)?.GetValue(ag) is true;
        }
        catch
        {
            return false;
        }
    }

    public string JobName(uint classJobId) =>
        Plugin.Data.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var j) ? j.Abbreviation.ExtractText() : "?";

    // ---------------------------------------------------------------- run

    /// <summary>GatherBuddy's CollectableTurnInRequirements.IsAvailable: turn-ins need Allagan Tools or Allagan Item Search.</summary>
    public static bool TurnInAvailable()
    {
        try
        {
            var inst = PluginFinder.FindPluginInstance("GatherBuddyReborn");
            var t = inst?.GetType().Assembly.GetType("GatherBuddy.AutoGather.Collectables.CollectableTurnInRequirements");
            return t?.GetProperty("IsAvailable", Any)?.GetValue(null) is not false;
        }
        catch
        {
            return true; // unknown: do not block
        }
    }

    public const string TurnInMissing = "GatherBuddy не сдаёт коллектаблы без Allagan Tools или Allagan Item Search — поставь/включи один из них (вкладка «Зависимости»).";

    public string? Begin(ScripKind kind, int target, uint preferredItem, bool allowFish)
    {
        if (!TurnInAvailable()) return TurnInMissing;
        var options = Options(kind, allowFish);
        if (options.Count == 0)
            return Info(kind).Crafter
                ? "Нет крафтера нужного уровня для коллектаблов этих скриптов."
                : "Нет собирателя нужного уровня для коллектаблов этих скриптов"
                  + (allowFish && !FishingAllowedInGbr() ? " (рыбу GatherBuddy не ловит без «Fish data collection» в настройках авто-сбора)." : " (рыбу можно разрешить в настройках вкладки).");
        Kind = kind;
        Target = target;
        this.allowFish = allowFish;
        preferred = preferredItem;
        tried.Clear();
        Item = PickNext();
        if (Item == null)
            return $"Все коллектаблы этих скриптов требуют того, что плагин не добудет: {string.Join("; ", options.Take(3).Select(o => $"{o.Name} — {MissingText(o)}"))}.";
        batches = idleBatches = 0;
        Finished = false;
        Running = true;
        var err = SetAutoTurnIn(true);
        if (err != null) { Running = false; return err; }
        Go(Step.Start, "");
        return null;
    }

    public void Stop(string? why = null)
    {
        if (!Running) return;
        p.Vulcan.StopCrafting();
        SetAutoGather(false);
        RestoreAutoTurnIn();
        Running = false;
        Finished = true;
        if (why != null) { Now = why; Plugin.Chat.Print($"[CraftMeThis] {why}"); }
    }

    private void Go(Step s, string now) { step = s; stepAt = DateTime.UtcNow; if (now.Length > 0) Now = now; }

    /// <summary>Next collectable to try: the preferred one if it is obtainable, otherwise the best obtainable not tried yet.</summary>
    private Collectable? PickNext()
    {
        var options = Options(Kind, allowFish).Where(o => !tried.Contains(o.ItemId) && Missing(o).Count == 0).ToList();
        var pick = options.FirstOrDefault(o => o.ItemId == preferred) ?? options.FirstOrDefault();
        if (pick != null) tried.Add(pick.ItemId);
        return pick;
    }

    /// <summary>The current collectable gave nothing: switch to the next one, or stop when none is left.</summary>
    private void SwitchOrStop(string why)
    {
        var old = Item;
        var next = PickNext();
        if (next == null) { Stop($"{why} Других подходящих коллектаблов нет — останавливаю фарм скриптов ({Have(Kind)}/{Target})."); return; }
        Item = next;
        idleBatches = 0;
        Plugin.Chat.Print($"[CraftMeThis] {why} Переключаюсь с {old?.Name} на {next.Name}.");
        Go(Step.Start, $"Переключаюсь на {next.Name}.");
    }

    public void Tick(DateTime now)
    {
        if (!Running || Item == null) return;
        var have = Have(Kind);
        var label = Info(Kind).Name;
        switch (step)
        {
            case Step.Start:
            {
                if (have >= Target) { Stop($"{label} скрипы: {have}/{Target} — хватает."); return; }
                // The exchange refuses a turn-in that would go over the cap: with 3600 and a 600-scrip collectable
                // nothing more can be handed in, so farming further only fills the bags.
                var reward = Math.Max(Item.High, Item.Mid);
                if (have + reward > Plugin.ScripCap)
                {
                    Stop($"{label} скрипы: {have} — следующий {Item.Name} даст до {reward}, в лимит {Plugin.ScripCap} уже не влезет. Сначала трачу.");
                    return;
                }
                before = have;
                batches++;
                lastProgress = default;
                lastProgressAt = now;
                var units = (int)Math.Ceiling((Target - have) / (double)Math.Max(1, Item.Mid));
                if (Item.RecipeId != 0)
                {
                    if (VulcanLink.ArtisanLoaded) { p.Vulcan.SetArtisan(false); if (now - stepAt < TimeSpan.FromSeconds(10)) return; }
                    var crafts = Math.Min(MaxCraftsPerBatch, Math.Max(1, units));
                    var err = p.Vulcan.StartCraftingNamed("CraftMeThis Scrips", new[] { new CartLine(Item.RecipeId, crafts) });
                    if (err != null) { Stop(err); return; }
                    Go(Step.Working, $"Крафчу коллектабл {Item.Name} ×{crafts} ({JobName(Item.CraftJob)}) ради скриптов: {have}/{Target}.");
                }
                else
                {
                    var err = p.Vulcan.StartGather(new Dictionary<uint, int> { [Item.ItemId] = Math.Max(1, units) + Plugin.BagCount(Item.ItemId) });
                    if (err != null) { Stop(err); return; }
                    Go(Step.Working, $"Собираю коллектабл {Item.Name} ×{units} ради скриптов: {have}/{Target}.");
                }
                return;
            }

            case Step.Working:
            {
                if (now - stepAt < TimeSpan.FromSeconds(8)) return;
                var busy = Item.RecipeId != 0 ? p.Vulcan.CraftingRunning : AutoGatherOn();
                Now = (Item.RecipeId != 0 ? $"Vulcan крафтит {Item.Name}" : $"Авто-сбор: {Item.Name} — {VulcanLink.AutoGatherStatus()}")
                      + $" · скрипы {have}/{Target}";
                if (have >= Target && Item.RecipeId == 0) SetAutoGather(false);
                // Watchdog: neither scrips nor collectables in the bags moved for a long time: auto-gather refused the
                // item, or Vulcan is stuck on a craft. End the batch; Check then switches to another collectable.
                var q = Item.RecipeId != 0 ? p.Vulcan.QueueProgress() : null;
                var vs = Item.RecipeId != 0 ? p.Vulcan.CraftingState : "";
                var waiting = vs.Contains("Gather") || vs.Contains("Retainer") || vs.Contains("Raphael") || vs.Contains("Repair");
                var progress = (have, Plugin.BagCount(Item.ItemId), q?.Index ?? 0, q?.Done ?? 0, waiting);
                if (progress != lastProgress || waiting || TurnInRunning()) { lastProgress = progress; lastProgressAt = now; }
                else if (busy && have < Target && now - lastProgressAt > BatchStall)
                {
                    Plugin.Chat.Print($"[CraftMeThis] {Item.Name}: {BatchStall.TotalMinutes:0} мин без прибавки — прерываю партию.");
                    if (Item.RecipeId != 0) p.Vulcan.StopCrafting(); else SetAutoGather(false);
                    busy = false;
                }
                if (busy && have < Target) return;
                if (TurnInRunning()) return; // GatherBuddy is already handing in
                Go(Step.TurnIn, "Сдаю оставшиеся коллектаблы…");
                turnInStarted = false;
                lastTurnInTry = now;
                StartTurnIn();
                return;
            }

            case Step.TurnIn:
                if (now - stepAt < TimeSpan.FromSeconds(5) || !Plugin.IsIdleNow()) return;
                if (TurnInRunning()) { turnInStarted = true; return; }
                // GatherBuddy's first Start often only kicks off loading its collectables data ("still loading") and
                // returns false: retry for a minute while collectables are still in the bags.
                if (!turnInStarted && Plugin.BagCount(Item.ItemId) > 0 && now - stepAt < TimeSpan.FromSeconds(60))
                {
                    if (now - lastTurnInTry > TimeSpan.FromSeconds(5)) { lastTurnInTry = now; StartTurnIn(); }
                    Now = $"Сдаю коллектаблы… {TurnInStatus()}";
                    return;
                }
                if (!turnInStarted && Plugin.BagCount(Item.ItemId) > 0)
                {
                    Stop($"GatherBuddy не начал сдачу коллектаблов: {TurnInStatus()}. Сдай вручную (окно Collectables в GatherBuddy) и запусти снова.");
                    return;
                }
                Go(Step.Check, "");
                return;

            case Step.Check:
                if (now - stepAt < TimeSpan.FromSeconds(3)) return;
                if (have >= Target) { Stop($"{label} скрипы: {have}/{Target} — готово."); return; }
                idleBatches = have > before ? 0 : idleBatches + 1;
                if (idleBatches >= 1 && Plugin.BagCount(Item.ItemId) == 0)
                {
                    // Nothing was made at all: this collectable does not work (materials, fishing, node), try another.
                    SwitchOrStop($"{Item.Name} не получился — скриптов не прибавилось.");
                    return;
                }
                if (idleBatches >= 2) { Stop($"Две партии без прибавки скриптов ({have}/{Target}). Проверь сдачу коллектаблов в GatherBuddy (Collectables)."); return; }
                if (batches >= 10) { Stop($"10 партий, скрипы {have}/{Target} — останавливаюсь."); return; }
                Go(Step.Start, $"Скрипы {have}/{Target} — ещё партия.");
                return;
        }
    }

    // ---------------------------------------------------------------- GatherBuddy plumbing

    private static object? GbrConfig()
    {
        var inst = PluginFinder.FindPluginInstance("GatherBuddyReborn");
        var gb = inst?.GetType().Assembly.GetType("GatherBuddy.GatherBuddy");
        return gb?.GetProperty("Config", Any)?.GetValue(null);
    }

    private static object? CollectableManager()
    {
        var inst = PluginFinder.FindPluginInstance("GatherBuddyReborn");
        var gb = inst?.GetType().Assembly.GetType("GatherBuddy.GatherBuddy");
        return gb?.GetProperty("CollectableManager", Any)?.GetValue(null);
    }

    private string? SetAutoTurnIn(bool on)
    {
        try
        {
            var cfg = GbrConfig() ?? throw new InvalidOperationException("нет GatherBuddy.Config");
            var cc = cfg.GetType().GetProperty("CollectableConfig", Any)?.GetValue(cfg) ?? throw new InvalidOperationException("нет CollectableConfig");
            var prop = cc.GetType().GetProperty("AutoTurnInCollectables", Any) ?? throw new InvalidOperationException("нет AutoTurnInCollectables");
            savedAutoTurnIn ??= prop.GetValue(cc) as bool?;
            prop.SetValue(cc, on);
            cfg.GetType().GetMethod("Save", Any, Type.EmptyTypes)?.Invoke(cfg, null);
            return null;
        }
        catch (Exception ex)
        {
            return $"Не удалось включить сдачу коллектаблов в GatherBuddy: {(ex as TargetInvocationException)?.InnerException?.Message ?? ex.Message}";
        }
    }

    private void RestoreAutoTurnIn()
    {
        if (savedAutoTurnIn is not { } old) return;
        SetAutoTurnIn(old);
        savedAutoTurnIn = null;
    }

    private bool turnInStarted;
    private DateTime lastTurnInTry;

    /// <summary>GatherBuddy's own explanation (CollectableManager.StatusText), e.g. "still loading", "no collectables".</summary>
    private static string TurnInStatus() =>
        CollectableManager() is { } m && m.GetType().GetProperty("StatusText", Any)?.GetValue(m) is string s && s.Length > 0 ? s : "без объяснения";

    private static bool TurnInRunning() => CollectableManager() is { } m && m.GetType().GetProperty("IsRunning", Any)?.GetValue(m) is true;

    private static void StartTurnIn()
    {
        try
        {
            if (CollectableManager() is not { } m) return;
            var start = m.GetType().GetMethod("Start", Any);
            if (start == null) return;
            var ps = start.GetParameters();
            var args = ps.Select(x => x.HasDefaultValue ? x.DefaultValue : (x.ParameterType.IsValueType ? Activator.CreateInstance(x.ParameterType) : null)).ToArray();
            start.Invoke(m, args);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"GatherBuddy turn-in: {ex.Message}");
        }
    }

    private static bool AutoGatherOn()
    {
        try { return Plugin.Pi.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled").InvokeFunc(); }
        catch { return false; }
    }

    private static void SetAutoGather(bool on)
    {
        try { Plugin.Pi.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled").InvokeAction(on); }
        catch { /* not running */ }
    }
}
