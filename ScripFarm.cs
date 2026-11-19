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

    /// <summary>Collectables of a kind that the character can make now, best payout first.</summary>
    public List<Collectable> Options(ScripKind kind, bool allowFish) =>
        all.Where(c => c.Kind == kind && (c.RecipeId != 0 ? JobLevel(c.CraftJob) >= c.Level : (allowFish || !c.Fish) && GatherLevel(c.Fish) >= c.Level))
           .OrderByDescending(c => c.Mid).ThenByDescending(c => c.Level).ToList();

    public string JobName(uint classJobId) =>
        Plugin.Data.GetExcelSheet<ClassJob>().TryGetRow(classJobId, out var j) ? j.Abbreviation.ExtractText() : "?";

    // ---------------------------------------------------------------- run

    public string? Begin(ScripKind kind, int target, uint preferredItem, bool allowFish)
    {
        var options = Options(kind, allowFish);
        if (options.Count == 0)
            return Info(kind).Crafter
                ? "Нет крафтера нужного уровня для коллектаблов этих скриптов."
                : "Нет собирателя нужного уровня для коллектаблов этих скриптов (рыбу можно разрешить в настройках вкладки).";
        Kind = kind;
        Target = target;
        Item = options.FirstOrDefault(o => o.ItemId == preferredItem) ?? options[0];
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
                before = have;
                batches++;
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
                if (busy && have < Target) return;
                if (TurnInRunning()) return; // GatherBuddy is already handing in
                Go(Step.TurnIn, "Сдаю оставшиеся коллектаблы…");
                StartTurnIn();
                return;
            }

            case Step.TurnIn:
                if (now - stepAt < TimeSpan.FromSeconds(5) || TurnInRunning() || !Plugin.IsIdleNow()) return;
                Go(Step.Check, "");
                return;

            case Step.Check:
                if (now - stepAt < TimeSpan.FromSeconds(3)) return;
                if (have >= Target) { Stop($"{label} скрипы: {have}/{Target} — готово."); return; }
                idleBatches = have > before ? 0 : idleBatches + 1;
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
