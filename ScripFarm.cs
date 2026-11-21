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
                                 uint RecipeId, uint CraftJob, bool Fish, int LowCol = 0, int MidCol = 0, int HighCol = 0)
{
    /// <summary>Scrips the exchange pays for one piece with this collectability.</summary>
    public int RewardFor(int collectability) =>
        HighCol > 0 && collectability >= HighCol ? High : MidCol > 0 && collectability >= MidCol ? Mid : collectability >= LowCol ? Low : 0;
}

/// <summary>
/// Scrip farming on top of GatherBuddy Reborn's collectables automation:
///  * crafter scrips — an ephemeral Vulcan crafting list with the best collectable recipe the job can make;
///  * gatherer scrips — GatherBuddy's temporary gather list (CraftingGatherBridge.CreateGatherListForMissingIngredients)
///    with the best collectable the gatherer level allows, auto-gather on;
///  * turn-ins — after each batch, once the character stands up: GatherBuddy's "Run Turn-Ins Now"
///    (CollectableManager.Start()); CraftMeThis does not touch its auto turn-in setting. Repeat until the target.
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
    private int batches, idleBatches;
    private int before;
    private readonly HashSet<uint> tried = new();
    private bool allowFish;
    private uint preferred;
    // progress watchdog for one batch: scrips + collectables in the bags
    private (int Scrips, int Items, int Queue, int Done, bool Waiting) lastProgress;
    private DateTime lastProgressAt;
    private static readonly TimeSpan BatchStall = TimeSpan.FromMinutes(12);
    // several timed nodes: travel between zones plus a possible gap between windows
    private static readonly TimeSpan GatherSetStall = TimeSpan.FromMinutes(20);
    private readonly Dictionary<uint, (DateTime At, List<uint> Missing)> missingCache = new();

    public ScripKind Kind { get; private set; }
    public int Target { get; private set; }
    public Collectable? Item { get; private set; }

    /// <summary>
    /// What the current batch gathers: for gatherer scrips every timed collectable whose node windows do not overlap
    /// (orange lvl-100 nodes open 2 Eorzea hours at 0/2/4/…/10 (+12), so together they cover the whole day and
    /// GatherBuddy always has one to go to); otherwise just <see cref="Item"/>.
    /// </summary>
    public List<Collectable> Set { get; private set; } = new();
    public string SetName => Set.Count > 1 ? string.Join(", ", Set.Select(c => c.Name)) : Item?.Name ?? "";
    // node windows of gathered items, Eorzea minutes of the day [start, end)
    private readonly Dictionary<uint, List<(int Start, int End)>> windows = new();
    public bool Running { get; private set; }
    public bool Finished { get; private set; }
    public string Now { get; private set; } = "";

    public ScripFarm(Plugin plugin)
    {
        p = plugin;
        var items = Plugin.Data.GetExcelSheet<Item>();
        var recipeByItem = Plugin.Data.GetExcelSheet<Recipe>().Where(r => r.ItemResult.RowId != 0)
                                 .GroupBy(r => r.ItemResult.RowId).ToDictionary(g => g.Key, g => g.First());
        var itemOfGi = new Dictionary<uint, uint>();
        foreach (var g in Plugin.Data.GetExcelSheet<GatheringItem>())
        {
            if (g.Item.RowId != 0 && g.GatheringItemLevel.IsValid) gatherLevel.TryAdd(g.Item.RowId, g.GatheringItemLevel.Value.GatheringItemLevel);
            if (g.Item.RowId != 0) itemOfGi[g.RowId] = g.Item.RowId;
        }
        LoadWindows(itemOfGi);
        var fish = new HashSet<uint>(Plugin.Data.GetExcelSheet<FishParameter>().Select(f => f.Item.RowId));
        foreach (var j in Plugin.Data.GetExcelSheet<ClassJob>())
            if (j.ExpArrayIndex >= 0) craftJobExp[j.RowId] = (uint)j.ExpArrayIndex;

        foreach (var sub in Plugin.Data.GetSubrowExcelSheet<CollectablesShopItem>())
        foreach (var c in sub)
        {
            if (c.Item.RowId == 0 || !c.CollectablesShopRewardScrip.IsValid) continue;
            var rw = c.CollectablesShopRewardScrip.Value;
            var rf = c.CollectablesShopRefine.IsValid ? c.CollectablesShopRefine.Value : default;
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
                                        r.RowId, 8 + r.CraftType.RowId, false, rf.LowCollectability, rf.MidCollectability, rf.HighCollectability));
            }
            else
            {
                var isFish = fish.Contains(id);
                if (!isFish && !gatherLevel.ContainsKey(id)) continue;
                all.Add(new Collectable(id, name, kind.Kind, isFish ? c.LevelMax : gatherLevel[id], rw.LowReward, rw.MidReward, rw.HighReward,
                                        0, 0, isFish, rf.LowCollectability, rf.MidCollectability, rf.HighCollectability));
            }
        }
    }

    // ---------------------------------------------------------------- data

    /// <summary>Timed node windows (GatheringRarePopTimeTable: start hhmm, duration h/mm) of every gathered item.</summary>
    private void LoadWindows(Dictionary<uint, uint> itemOfGi)
    {
        try
        {
            var transient = Plugin.Data.GetExcelSheet<GatheringPointTransient>();
            foreach (var pt in Plugin.Data.GetExcelSheet<GatheringPoint>())
            {
                if (!pt.GatheringPointBase.IsValid || !transient.TryGetRow(pt.RowId, out var t)) continue;
                var pop = t.GatheringRarePopTimeTable;
                if (pop.RowId == 0 || !pop.IsValid) continue;
                var spans = new List<(int, int)>();
                for (var i = 0; i < pop.Value.StartTime.Count; i++)
                {
                    int st = pop.Value.StartTime[i], du = pop.Value.Duration[i];
                    if (du == 0) continue;
                    var start = st / 100 * 60 + st % 100;
                    spans.Add((start, start + du / 100 * 60 + du % 100));
                }
                if (spans.Count == 0) continue;
                foreach (var gi in pt.GatheringPointBase.Value.Item)
                    if (itemOfGi.TryGetValue(gi.RowId, out var item))
                    {
                        if (!windows.TryGetValue(item, out var list)) windows[item] = list = new();
                        foreach (var sp in spans) if (!list.Contains(sp)) list.Add(sp);
                    }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"node windows: {ex.Message}");
        }
    }

    private static bool Overlap(List<(int Start, int End)> a, List<(int Start, int End)> b)
    {
        foreach (var x in a)
        foreach (var y in b)
            for (var shift = -1440; shift <= 1440; shift += 1440) // windows may wrap past midnight
                if (x.Start < y.End + shift && y.Start + shift < x.End) return true;
        return false;
    }

    /// <summary>
    /// The batch set for a gathered collectable: it plus every other timed collectable of the kind (best payout first)
    /// whose node windows overlap none already taken. Untimed items and crafts stay alone.
    /// </summary>
    private List<Collectable> BuildSet(Collectable first)
    {
        var set = new List<Collectable> { first };
        if (first.RecipeId != 0 || !windows.TryGetValue(first.ItemId, out var w0)) return set;
        var taken = new List<(int Start, int End)>(w0);
        foreach (var o in Options(Kind, allowFish))
        {
            if (o.ItemId == first.ItemId || o.RecipeId != 0 || tried.Contains(o.ItemId) || !windows.TryGetValue(o.ItemId, out var w)) continue;
            if (Overlap(taken, w)) continue;
            set.Add(o);
            taken.AddRange(w);
        }
        foreach (var c in set) tried.Add(c.ItemId);
        return set;
    }

    private int SetBags() => Set.Sum(c => Space.CountInBags(c.ItemId));

    /// <summary>Scrips the collectables of the set already in the bags will give at turn-in (by their collectability).</summary>
    private unsafe int BagValue()
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var byId = Set.ToDictionary(c => c.ItemId);
        var sum = 0;
        foreach (var t in Space.Bags)
        {
            var cont = im->GetInventoryContainer(t);
            if (cont == null) continue;
            for (var i = 0; i < cont->Size; i++)
            {
                var slot = cont->GetInventorySlot(i);
                if (slot != null && byId.TryGetValue(slot->ItemId, out var c))
                    sum += c.RewardFor(slot->SpiritbondOrCollectability) * Math.Max(1, (int)slot->Quantity);
            }
        }
        return sum;
    }

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
        if (Item != null) Set = BuildSet(Item);
        if (Item == null)
            return $"Все коллектаблы этих скриптов требуют того, что плагин не добудет: {string.Join("; ", options.Take(3).Select(o => $"{o.Name} — {MissingText(o)}"))}.";
        batches = idleBatches = 0;
        Finished = false;
        Running = true;
        Go(Step.Start, "");
        return null;
    }

    public void Stop(string? why = null)
    {
        if (!Running) return;
        p.Vulcan.StopCrafting();
        SetAutoGather(false);
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
        Set = BuildSet(next);
        idleBatches = 0;
        Plugin.Chat.Print($"[CraftMeThis] {why} Переключаюсь с {old?.Name} на {SetName}.");
        Go(Step.Start, $"Переключаюсь на {SetName}.");
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
                var reward = Set.Max(c => Math.Max(c.High, c.Mid));
                if (have + reward > Plugin.ScripCap)
                {
                    Stop($"{label} скрипы: {have} — следующий {SetName} даст до {reward}, в лимит {Plugin.ScripCap} уже не влезет. Сначала трачу.");
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
                    // Turn-in happens only after the batch: keep its total payout under the cap.
                    var mid = Set.Min(c => Math.Max(1, c.Mid));
                    units = Math.Min((int)Math.Ceiling((Target - have) / (double)mid), Math.Max(1, (Plugin.ScripCap - have) / Math.Max(1, reward)));
                    // No per-item quota: each item may cover the whole need, GatherBuddy goes round whichever node is up,
                    // and Working ends the batch once the pieces in the bags are worth the missing scrips.
                    var err = p.Vulcan.StartGather(Set.ToDictionary(c => c.ItemId, c => Math.Max(1, units) + Space.CountInBags(c.ItemId)));
                    if (err != null) { Stop(err); return; }
                    Go(Step.Working, Set.Count > 1
                        ? $"Собираю коллектаблы по кругу с {Set.Count} таймовых нодок ({SetName}), пока не наберётся на {Target - have} скриптов: {have}/{Target}."
                        : $"Собираю коллектабл {Item.Name} ×{units} ради скриптов: {have}/{Target}.");
                }
                return;
            }

            case Step.Working:
            {
                if (now - stepAt < TimeSpan.FromSeconds(8)) return;
                var busy = Item.RecipeId != 0 ? p.Vulcan.CraftingRunning : AutoGatherOn();
                Now = (Item.RecipeId != 0 ? $"Vulcan крафтит {Item.Name}" : $"Авто-сбор: {SetName} — {VulcanLink.AutoGatherStatus()}")
                      + $" · скрипы {have}/{Target}";
                if (Item.RecipeId == 0 && busy)
                {
                    // Enough in the bags for the target (or for the cap): end the round and hand in.
                    var value = BagValue();
                    var top = Set.Max(c => Math.Max(c.High, c.Mid));
                    Now += $" · в сумках на {value}";
                    if (have + value >= Target || have + value + top > Plugin.ScripCap)
                    {
                        Plugin.Log.Information($"[ScripFarm] bags worth {value}, have {have}, target {Target}: ending gather round");
                        SetAutoGather(false);
                        busy = false;
                    }
                }
                // Watchdog: neither scrips nor collectables in the bags moved for a long time: auto-gather refused the
                // item, or Vulcan is stuck on a craft. End the batch; Check then switches to another collectable.
                var q = Item.RecipeId != 0 ? p.Vulcan.QueueProgress() : null;
                var vs = Item.RecipeId != 0 ? p.Vulcan.CraftingState : "";
                var waiting = vs.Contains("Gather") || vs.Contains("Retainer");
                var progress = (have, SetBags(), q?.Index ?? 0, q?.Done ?? 0, waiting);
                if (progress != lastProgress || waiting || TurnInRunning()) { lastProgress = progress; lastProgressAt = now; }
                else if (busy && have < Target && now - lastProgressAt > (Set.Count > 1 ? GatherSetStall : BatchStall))
                {
                    Plugin.Chat.Print($"[CraftMeThis] {SetName}: {(Set.Count > 1 ? GatherSetStall : BatchStall).TotalMinutes:0} мин без прибавки — прерываю партию.");
                    if (Item.RecipeId != 0) p.Vulcan.StopCrafting(); else SetAutoGather(false);
                    busy = false;
                }
                if (busy && have < Target) return;
                Go(Step.TurnIn, "Жду, пока персонаж выйдет из крафта, потом сдача в GatherBuddy…");
                turnInPressed = false;
                standTried = false;
                standTries = 0;
                lastStandTry = default;
                return;
            }

            case Step.TurnIn:
            {
                // GatherBuddy may already be handing in (its own auto turn-in, if the user has it on): wait.
                if (TurnInRunning()) { turnInPressed = true; stepAt = now; Now = "GatherBuddy сдаёт коллектаблы и покупает по своему списку…"; return; }
                if (!turnInPressed)
                {
                    // Vulcan says "Queue complete" while the character may still kneel at the crafting log; a turn-in
                    // started then gets stuck on its way. Wait until it stands (close the log once if it does not).
                    if (InCraftingStance())
                    {
                        // Close the crafting log like Escape (every 10 s after the first 15 s); give up after 3 min.
                        if (now - stepAt > TimeSpan.FromSeconds(15) && now - lastStandTry > TimeSpan.FromSeconds(10))
                        {
                            lastStandTry = now;
                            standTries++;
                            LeaveCraftingLog();
                        }
                        if (now - stepAt > TimeSpan.FromMinutes(3))
                            Stop("Персонаж 3 мин не выходит из крафта (стойка у журнала рецептов) — встань сам и запусти фарм скриптов снова.");
                        return;
                    }
                    if (!Plugin.IsIdleNow() || now - stepAt < TimeSpan.FromSeconds(3)) return;
                    if (SetBags() == 0) { Go(Step.Check, ""); return; }
                    // The same as GatherBuddy's "Run Turn-Ins Now" button (CollectableManager.Start()).
                    PressTurnIns();
                    turnInPressed = true;
                    stepAt = now;
                    return;
                }
                if (now - stepAt < TimeSpan.FromSeconds(10) || !Plugin.IsIdleNow()) return;
                Go(Step.Check, "");
                return;
            }

            case Step.Check:
                if (now - stepAt < TimeSpan.FromSeconds(3)) return;
                if (have >= Target) { Stop($"{label} скрипы: {have}/{Target} — готово."); return; }
                idleBatches = have > before ? 0 : idleBatches + 1;
                if (idleBatches >= 1 && SetBags() == 0)
                {
                    // Nothing was made at all: this collectable does not work (materials, fishing, node), try another.
                    SwitchOrStop($"{SetName} не получился — скриптов не прибавилось.");
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






    /// <summary>GatherBuddy's own explanation (CollectableManager.StatusText), e.g. "still loading", "no collectables".</summary>
    private static string TurnInStatus() =>
        CollectableManager() is { } m && m.GetType().GetProperty("StatusText", Any)?.GetValue(m) is string s && s.Length > 0 ? s : "без объяснения";

    private bool turnInPressed, standTried;
    private int standTries;
    private DateTime lastStandTry;

    /// <summary>
    /// Stand up from the crafting stance: hide the crafting log agent (what Escape does). If the log window is not
    /// open, open it so the next try can close it. "/craftinglog" is a game command and Dalamud does not run it.
    /// </summary>
    private static unsafe void LeaveCraftingLog()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance()->GetAgentByInternalId(
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.RecipeNote);
            if (agent == null) return;
            var open = agent->IsAgentActive();
            if (open) agent->Hide(); else agent->Show();
            Plugin.Log.Information($"[ScripFarm] leave crafting log: {(open ? "closed" : "opened")} the log");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"leave crafting log: {ex.Message}");
        }
    }

    private static bool InCraftingStance() =>
        Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting]
        || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.PreparingToCraft];

    /// <summary>GatherBuddy's "Run Turn-Ins Now" button: CollectableManager.Start() with its defaults.</summary>
    private static void PressTurnIns()
    {
        try
        {
            if (CollectableManager() is not { } m || m.GetType().GetMethod("Start", Any) is not { } start) return;
            var args = start.GetParameters().Select(x => x.HasDefaultValue ? x.DefaultValue : (x.ParameterType.IsValueType ? Activator.CreateInstance(x.ParameterType) : null)).ToArray();
            if (start.Invoke(m, args) is false) Plugin.Chat.Print($"[CraftMeThis] GatherBuddy не начал сдачу: {TurnInStatus()}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"GatherBuddy turn-ins: {ex.Message}");
        }
    }

    private static bool TurnInRunning() => CollectableManager() is { } m && m.GetType().GetProperty("IsRunning", Any)?.GetValue(m) is true;


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
