using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Lumina.Excel.Sheets;

namespace CraftMeThis;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    /// <summary>Extra gemstones to farm on top of what the cart costs.</summary>
    public int Reserve { get; set; }
    /// <summary>Seconds the character must stay idle after "/afg stop soft" before the vendor run starts.</summary>
    public int IdleSeconds { get; set; } = 6;
    /// <summary>Minutes to wait for the soft stop before forcing "/afg stop".</summary>
    public int SoftStopTimeoutMinutes { get; set; } = 8;
    /// <summary>The cart: recipes and craft counts (imported from Teamcraft or edited here).</summary>
    public List<CartEntry> Cart { get; set; } = new();
    /// <summary>Farm zone (territory id) chosen by hand; 0 = pick automatically from the cart.</summary>
    public uint ZoneOverride { get; set; }
    /// <summary>Set the farm zone in Auto FATE Grind before the run (its own zones are put back afterwards).</summary>
    public bool SetAfgZone { get; set; } = true;
    /// <summary>Switch to the first suitable job from <see cref="Jobs"/> before farming.</summary>
    public bool SwitchJob { get; set; } = true;
    /// <summary>Job priority, top first.</summary>
    public List<JobPref> Jobs { get; set; } = new();
    /// <summary>Teamcraft list the cart was last imported from ("" = none).</summary>
    public string TeamcraftListUrl { get; set; } = string.Empty;
    /// <summary>Things the plugin will not do for the imported list (FC workshop phases, workshop raw materials).</summary>
    public List<string> ImportManual { get; set; } = new();
    /// <summary>Buy vendor materials (gil, GC, gemstones…) through Vulcan before crafting.</summary>
    public bool BuyFromVendors { get; set; } = true;
    /// <summary>Start Vulcan's gather + craft queue after the purchases.</summary>
    public bool CraftAfterBuying { get; set; } = true;
    /// <summary>Let Vulcan take materials from retainers (its "Retainer restock", needs Allagan Tools).</summary>
    public bool UseRetainers { get; set; } = true;
    /// <summary>Unload Artisan for Vulcan's crafting (Vulcan refuses to craft while it is loaded)…</summary>
    public bool ToggleArtisan { get; set; } = true;
    /// <summary>…and load it again afterwards.</summary>
    public bool ReenableArtisan { get; set; } = true;
    /// <summary>
    /// Resume mode ("Include Final Crafts" in Vulcan): the cart counts as a target amount, so final crafts are reduced by
    /// what is already in the bags. Turned on after a run that left cart lines unfinished, off after a clean run or a new cart.
    /// </summary>
    public bool ResumeFinals { get; set; }
    /// <summary>How each raw material is obtained, chosen on the Materials tab (missing = automatic).</summary>
    public Dictionary<uint, Route> Routes { get; set; } = new();
    /// <summary>AutoDuty leveling mode for GC seal farming: Support, Trust_Solo or Trust_Group.</summary>
    public string GcLevelingMode { get; set; } = "Support";
    /// <summary>At most this many dungeon runs per GC seal farm.</summary>
    public int GcMaxRuns { get; set; } = 10;
    /// <summary>Dungeon (territory id) to farm GC seals in; 0 = the highest one AutoDuty allows at the job's level.</summary>
    public uint GcDutyTerritory { get; set; }
    /// <summary>Collectable to farm per scrip kind (item id; 0/missing = best one available).</summary>
    public Dictionary<ScripKind, uint> ScripItem { get; set; } = new();
    /// <summary>Allow fish as gatherer-scrip collectables (orange gatherer scrips at 100 are mostly fish).</summary>
    public bool ScripAllowFish { get; set; } = true;
    /// <summary>Split a cart that will not fit into the bags into batches (buy → craft one batch at a time).</summary>
    public bool SplitBySpace { get; set; } = true;
    /// <summary>Bag slots to keep free when sizing batches (loot, retainers, safety margin).</summary>
    public int SpaceReserve { get; set; } = 5;
    /// <summary>Scrips tab: kind and target for a standalone farm.</summary>
    public ScripKind ScripTabKind { get; set; } = ScripKind.OrangeCrafter;
    public int ScripTabTarget { get; set; } = 1000;
    /// <summary>Give Hunter at most this many minutes for the mob drops.</summary>
    public int MobFarmMaxMinutes { get; set; } = 60;
    /// <summary>During the mob farm, set RSR's Engage to "all targets in range" for the farm job, so it starts fights itself.</summary>
    public bool RsrEngageAll { get; set; } = true;
    // Auto-leveling (beta) — see Leveler.
    public int LevelTarget { get; set; } = 100;
    public string LevelTrustMode { get; set; } = "Trust_Group";
    public int LevelGcFreeSlots { get; set; } = 25;
    public int LevelQuestIdleSeconds { get; set; } = 45;
    public int LevelQuestMaxMinutes { get; set; } = 300;
    public int LevelDungeonMaxMinutes { get; set; } = 450;
    public int LevelFateMaxMinutes { get; set; } = 120;
    public int LevelStallMinutes { get; set; } = 10;
    /// <summary>Past runs, newest first (History tab).</summary>
    public List<RunRecord> History { get; set; } = new();
    /// <summary>Carts saved under a name (Cart tab).</summary>
    public List<SavedCart> SavedCarts { get; set; } = new();
}

/// <summary>How a raw material is obtained.</summary>
public enum Route
{
    /// <summary>Vulcan's crafting queue gathers or fishes it (its auto-gather).</summary>
    Gather,
    /// <summary>Gemstone Trader: Auto FATE Grind farms the gemstones, Vulcan buys.</summary>
    Bicolor,
    /// <summary>Vulcan's vendor buy list (gil, Grand Company, scrips…).</summary>
    Vendor,
    /// <summary>Market board, drops or anything else: left to the player.</summary>
    Market,
    /// <summary>Grand Company seals farmed with AutoDuty (dungeons + expert delivery), then Vulcan buys.</summary>
    GcFarm,
    /// <summary>Mob drop farmed by Hunter (HunterV2).</summary>
    MobFarm,
    /// <summary>Sold for crafter/gatherer scrips: scrips farmed with GatherBuddy collectables first, then Vulcan buys.</summary>
    ScripFarm,
    /// <summary>Sold for Allagan tomestones: Vulcan buys if there are enough, tomestones themselves are not farmed.</summary>
    Tomes,
}

/// <summary>A raw material of the cart with its possible and chosen route.</summary>
/// <param name="Price">Vulcan's vendor offer ("7 Ixali Oaknot · Ixali vendor"), null when no vendor.</param>
/// <param name="Affordable">Whether the currency for <see cref="Missing"/> units is there (beast tribe, GC, scrips…).</param>
public sealed record Material(uint ItemId, string Name, int Have, int Missing, IReadOnlyList<Route> Options, Route Route, bool Chosen,
                              string? Price = null, bool Affordable = true, string? Shortage = null, string? Note = null);

/// <summary>A master recipe book the cart needs and the character has not learned: bought (scrips…) and learned in the run.</summary>
public sealed record BookNeed(uint BookId, uint ItemId, string Name, IReadOnlyList<uint> Recipes);

[Serializable]
public sealed class CartEntry
{
    public uint RecipeId { get; set; }
    public int Crafts { get; set; }
}

public enum Phase { Idle, Preparing, Farming, Stopping, GcFarming, ScripFarming, Buying, AfgBuying, MobFarming, Crafting, Done, Failed }

/// <summary>A material Vulcan will buy from a vendor: target is the bag count to reach.</summary>
public sealed record VendorBuy(uint ItemId, string Name, int Units, int Target, bool ForGemstones);

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface Pi { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;

    internal static Plugin? Instance { get; private set; }
    internal Leveler Leveler { get; private set; } = null!;
    internal readonly Dependencies Deps = new();

    private const string Cmd = "/craftme";
    private const string CmdShort = "/cmt";
    private const string GatherBuddy = "GatherBuddyReborn";
    private const string AutoFateGrind = "AutoFateGrind";

    internal readonly Configuration Config;
    internal readonly Planner Planner;
    internal readonly Zones Zones;
    internal readonly Jobs Jobs;
    internal readonly Sources Sources;
    internal readonly VulcanLink Vulcan = new();
    private readonly AfgLink afg = new();
    private readonly WindowSystem windows = new("CraftMeThis");
    private readonly MainWindow window;

    internal GemPlan Plan { get; private set; } = GemPlan.Empty;
    /// <summary>What Vulcan should buy: gemstone materials and other vendor-only materials of the cart.</summary>
    internal List<VendorBuy> VendorBuys { get; private set; } = new();
    /// <summary>All raw materials still missing for the cart, with their routes (Materials tab).</summary>
    internal List<Material> Materials { get; private set; } = new();
    internal Phase Phase { get; private set; } = Phase.Idle;
    internal string Status { get; private set; } = "Ожидание.";
    internal int Target { get; private set; }
    internal int Gems { get; private set; }
    internal List<string> Warnings { get; } = new();
    internal ZoneChoice? Zone { get; private set; }

    internal bool Running => Phase is Phase.Preparing or Phase.Farming or Phase.Stopping or Phase.GcFarming or Phase.ScripFarming or Phase.Buying or Phase.AfgBuying or Phase.MobFarming or Phase.Crafting;
    internal ScripFarm Scrips { get; private set; } = null!;
    /// <summary>Scrip kinds still to farm for the cart, and whether the run is a standalone farm from the Scrips tab.</summary>
    private readonly Queue<(ScripKind Kind, int Target)> scripQueue = new();
    private bool scripStandalone, scripsDone, scripQueued;

    // Batches when the cart does not fit into the bags.
    private List<CartLine>? currentBatch;
    private List<CartLine> remainingLines = new();
    private bool anyBatchFailed;
    internal int BatchNo { get; private set; }
    internal bool Batching => currentBatch != null;
    internal int BatchesLeft => remainingLines.Count;
    internal readonly HunterLink Hunter = new();
    private readonly Lumina.Excel.ExcelSheet<Item> englishItems;
    private List<VendorBuy> mobFarm = new();
    private readonly RsrEngage rsr = new();
    private bool rsrChecked;

    private string CurrentJobAbbr() => Jobs.Get(Jobs.CurrentJob())?.Abbr ?? "";

    private void RestoreRsr()
    {
        if (rsr.Restore(CurrentJobAbbr()) is { } note) Print(note);
    }
    private DateTime mobIdleSince = DateTime.MinValue;

    /// <summary>English item name (Hunter's mob database uses English names).</summary>
    internal string EnglishName(uint id) => englishItems.TryGetRow(id, out var it) ? it.Name.ExtractText() : "";
    internal GcFarm Gc { get; private set; } = null!;
    private bool Farming => Phase is Phase.Preparing or Phase.Farming or Phase.Stopping or Phase.AfgBuying;

    /// <summary>What follows once Auto FATE Grind has stopped: the vendor run, or crafting (after AFG bought the gemstone items).</summary>
    private Phase afterStop = Phase.Buying;
    private List<VendorBuy> afgBuys = new();
    /// <summary>Cart result items before crafting: item → (bag count, expected gain, name).</summary>
    private Dictionary<uint, (int Before, int Expected, string Name, uint RecipeId, int Crafts)> craftBaseline = new();
    /// <summary>Cart items the last craft run did not finish (craft tree marks them).</summary>
    internal HashSet<uint> FailedItems { get; } = new();

    /// <summary>Items gained for a cart line since the last craft run started; null before any run.</summary>
    internal int? Crafted(TreeNode n) =>
        craftBaseline.TryGetValue(n.ItemId, out var b) ? Math.Max(0, BagCount(n.ItemId) - b.Before) : null;

    private DateTime nextTick = DateTime.MinValue;
    private DateTime startAt;
    private DateTime stopSentAt;
    private DateTime idleSince = DateTime.MinValue;
    private bool forcedStop;

    // Buying / crafting sub-state.
    private bool vendorStarted;
    private DateTime phaseStartedAt;
    private bool artisanTurnedOff;
    private bool craftStarted;

    // Crafting watchdog: Vulcan retrying the same craft forever (mounted, or an ingredient it cannot select).
    private (int Index, uint Recipe, int Done, long Bags) craftSig;
    private DateTime craftSigAt;
    private int craftDismounts;
    private string? craftHang;
    private static readonly TimeSpan CraftStall = TimeSpan.FromMinutes(4);

    public Plugin()
    {
        Config = Pi.GetPluginConfig() as Configuration ?? new Configuration();
        Planner = new Planner(Data.GetExcelSheet<Recipe>(), Data.GetExcelSheet<SpecialShop>(), Data.GetExcelSheet<Item>(),
            new[] { Data.GetExcelSheet<Item>(ClientLanguage.English) });
        Zones = new Zones(Data.GetExcelSheet<FateShop>(), Data.GetExcelSheet<SpecialShop>(), Data.GetExcelSheet<Level>(), Data.GetExcelSheet<ENpcResident>());
        Jobs = new Jobs(Data.GetExcelSheet<ClassJob>());
        Sources = new Sources(Data.GetExcelSheet<GatheringItem>(), Data.GetExcelSheet<FishParameter>(), Data.GetExcelSheet<SpearfishingItem>(),
            Data.GetSubrowExcelSheet<GilShopItem>());
        Jobs.Normalize(Config.Jobs);

        // Runs left "running" by a plugin reload or game exit.
        foreach (var r in Config.History.Where(r => r.Result == "running"))
        {
            r.Result = "stopped";
            r.Summary = "Прервано: плагин или игра перезапустились посреди запуска.";
        }
        Instance = this;
        Leveler = new Leveler(this);
        Gc = new GcFarm(this);
        Scrips = new ScripFarm(this);
        englishItems = Data.GetExcelSheet<Item>(ClientLanguage.English);
        window = new MainWindow(this);
        windows.AddWindow(window);
        Pi.UiBuilder.Draw += windows.Draw;
        Pi.UiBuilder.OpenMainUi += OpenWindow;
        Pi.UiBuilder.OpenConfigUi += OpenWindow;

        Commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "окно; /craftme plan — что нужно корзине; /craftme start — нафармить биколоры, закупить и скрафтить; /craftme stop — остановить",
        });
        Commands.AddHandler(CmdShort, new CommandInfo(OnCommand) { HelpMessage = "то же, что /craftme" });
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        Commands.RemoveHandler(Cmd);
        Commands.RemoveHandler(CmdShort);
        Leveler.Stop();
        afg.Restore();
        if (Phase == Phase.MobFarming) Hunter.SetAutoHunt(false);
        Hunter.Restore();
        RestoreRsr();
        if (artisanTurnedOff && Config.ReenableArtisan) Vulcan.SetArtisan(true);
        Pi.UiBuilder.Draw -= windows.Draw;
        Pi.UiBuilder.OpenMainUi -= OpenWindow;
        Pi.UiBuilder.OpenConfigUi -= OpenWindow;
        windows.RemoveAllWindows();
    }

    private void OpenWindow() => window.IsOpen = true;

    internal void SaveConfig() => Pi.SavePluginConfig(Config);

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start": Start(); break;
            case "stop": Stop(); break;
            case "plan":
                Recalculate();
                Print(Config.Cart.Count == 0 ? "корзина пуста."
                    : $"биколоров нужно {Plan.Gems} (есть {Gems}); у вендоров купить: " +
                      (VendorBuys.Count == 0 ? "ничего" : string.Join(", ", VendorBuys.Select(v => $"{v.Name} x{v.Units}"))) +
                      (Zone != null && Plan.Gems > 0 ? $". Зона фарма: {Zone.Zone.Name}." : "."));
                break;
            default: window.IsOpen = !window.IsOpen; break;
        }
    }

    // ---------------------------------------------------------------- plan

    internal IReadOnlyList<CartLine> CartLines() => Config.Cart.Select(c => new CartLine(c.RecipeId, c.Crafts)).ToList();

    /// <summary>The crafts still to do: the cart itself, or in resume mode only what the bags do not cover yet (as Vulcan counts it).</summary>
    /// <summary>The crafts this step works on: the current batch while splitting, otherwise the whole (effective) cart.</summary>
    internal IReadOnlyList<CartLine> EffectiveCart() => currentBatch ?? FullEffectiveCart();

    internal IReadOnlyList<CartLine> FullEffectiveCart()
    {
        if (!Config.ResumeFinals) return CartLines();
        var lines = new List<CartLine>();
        foreach (var c in Config.Cart)
        {
            var yield = Planner.RecipeYield(c.RecipeId);
            var left = Math.Max(0, c.Crafts * yield - BagCount(Planner.RecipeItem(c.RecipeId)));
            var crafts = (left + yield - 1) / yield;
            if (crafts > 0) lines.Add(new CartLine(c.RecipeId, crafts));
        }
        return lines;
    }

    internal void SetResume(bool on)
    {
        if (Config.ResumeFinals == on) return;
        Config.ResumeFinals = on;
        SaveConfig();
        Recalculate();
        SyncVulcanList();
    }

    internal void Recalculate()
    {
        Warnings.Clear();
        Gems = GemCount();
        Plan = Planner.Build(EffectiveCart(), BagCount, id => RouteOf(id) == Route.Bicolor);
        Target = (int)Math.Min(Planner.GemCap, Plan.Gems + Math.Max(0, Config.Reserve));
        if (Plan.Gems + Config.Reserve > Planner.GemCap)
            Warnings.Add($"Корзине нужно {Plan.Gems} биколоров при лимите {Planner.GemCap} — запуск сам разделит её на партии (фарм и покупка под каждую).");

        var gbr = IsLoaded(GatherBuddy);
        if (!gbr) Warnings.Add("GatherBuddy Reborn не загружен: покупки и крафт делает его Vulcan.");
        if (Plan.Gems > Gems && !IsLoaded(AutoFateGrind)) Warnings.Add("Auto FATE Grind не загружен: биколоры фармить некому.");

        // Routes: gemstone materials go to the gemstone plan above; the rest by the route chosen on the Materials tab.
        Materials = Plan.Purchases.Select(p => MaterialOf(p.ItemId, p.Units))
            .Concat(Plan.Leaves.Select(kv => MaterialOf(kv.Key, kv.Value)))
            .OrderBy(m => m.Route).ThenByDescending(m => m.Missing).ToList();
        Books = BookNeeds(EffectiveCart());
        foreach (var b in Books.Where(b => BagCount(b.ItemId) == 0 && Materials.All(m => m.ItemId != b.ItemId)))
            Materials.Insert(0, MaterialOf(b.ItemId, 1) with { Note = $"мастер-книга для {string.Join(", ", b.Recipes.Select(Planner.RecipeName))}" });
        VendorBuys = Plan.Purchases.Select(p => new VendorBuy(p.ItemId, p.Name, p.Units, BagCount(p.ItemId) + p.Units, true)).ToList();
        foreach (var m in Materials.Where(m => m.Route is Route.Vendor or Route.GcFarm or Route.ScripFarm or Route.Tomes))
            VendorBuys.Add(new VendorBuy(m.ItemId, m.Name, m.Missing, m.Have + m.Missing, false));

        Zone = Config.ZoneOverride != 0 && Zones.Get(Config.ZoneOverride) is { } manual
            ? new ZoneChoice(manual, "выбрана вручную", Array.Empty<(string, int)>())
            : Zones.Choose(Plan);
        if (Zone?.Sold.Any(s => s.Rank > 1) == true)
            Warnings.Add($"Часть материалов у торговца {Zone.Zone.Trader} открывается только с ранга {Zone.Sold.Max(s => s.Rank)} общих FATE в {Zone.Zone.Name}.");
        if (Materials.Any(m => m.Route == Route.GcFarm) && GcFarm.DeliveryStatus().Problem is { } delivery)
            Warnings.Add($"Фарм печатей GC не сможет сдать лут: {delivery}");
        if (gbr && Materials.Any(m => m.Route == Route.ScripFarm) && !ScripFarm.TurnInAvailable())
            Warnings.Add("Фарм скриптов не сможет сдать коллектаблы: " + ScripFarm.TurnInMissing);
        if (Config.SwitchJob && Zone != null && Plan.Gems > Gems && Jobs.Pick(Config.Jobs, Zone.Zone.MinLevel) == null)
            Warnings.Add($"Нет включённого класса уровня {Zone.Zone.MinLevel}+ с гирсетом — фарм пойдёт текущим классом.");
    }

    // ---------------------------------------------------------------- routes

    /// <summary>Routes that can work for an item: gather/fish, Gemstone Trader, a vendor Vulcan knows, or by hand.</summary>
    internal List<Route> RouteOptions(uint itemId)
    {
        var options = new List<Route>();
        var src = Sources.Of(itemId);
        if (src is Source.Gather or Source.Fish) options.Add(Route.Gather);
        if (Planner.HasGemOffer(itemId)) options.Add(Route.Bicolor);
        if (src == Source.Vendor || (!Planner.HasGemOffer(itemId) && IsLoaded(GatherBuddy) && Vulcan.CanBuy(itemId))) options.Add(Route.Vendor);
        // Sold for Grand Company seals: the seals can be farmed with AutoDuty first.
        var vp = options.Contains(Route.Vendor) && IsLoaded(GatherBuddy) ? Vulcan.PriceOf(itemId) : null;
        if (vp?.Group == "GrandCompanySeals") options.Add(Route.GcFarm);
        // Sold for crafter/gatherer scrips: the scrips can be farmed with collectables first.
        if (vp?.Group == "Scrips" && ScripFarm.KindOfItem(vp.CurrencyId) != null) options.Add(Route.ScripFarm);
        // Sold for tomestones: marked separately ("Tomes" instead of a plain vendor).
        if (vp?.Group == "Tomestones") { options.Remove(Route.Vendor); options.Insert(0, Route.Tomes); }
        // A mob drop Hunter knows (its mob database: who drops it and where).
        if (IsLoaded(HunterLink.InternalName) && Hunter.DroppedBy(EnglishName(itemId)).Count > 0) options.Add(Route.MobFarm);
        options.Add(Route.Market);
        return options;
    }

    /// <summary>
    /// The chosen route if it is still possible, otherwise the first of gather → gemstones → vendor → by hand.
    /// A vendor whose currency is short (no beast tribe currency, too few GC seals…) is skipped by "auto".
    /// </summary>
    internal Route RouteOf(uint itemId, int missing = 0)
    {
        var options = RouteOptions(itemId);
        if (Config.Routes.TryGetValue(itemId, out var r) && options.Contains(r)) return r;
        foreach (var o in options)
        {
            if (o is Route.Vendor or Route.Tomes && missing > 0 && !CanAfford(itemId, missing, out _)) continue;
            return o;
        }
        return Route.Market;
    }

    /// <summary>Whether the currency Vulcan would pay with covers <paramref name="units"/> (gil is always assumed).</summary>
    internal bool CanAfford(uint itemId, int units, out string? shortage)
    {
        shortage = null;
        if (!IsLoaded(GatherBuddy) || Vulcan.PriceOf(itemId) is not { } price || price.Group == "Gil" || price.Cost == 0) return true;
        var have = Vulcan.CurrencyHave(price);
        var need = (long)price.Cost * units;
        if (have >= need) return true;
        shortage = $"{price.CurrencyName}: {have}/{need}";
        return false;
    }

    private Material MaterialOf(uint itemId, int missing)
    {
        var options = RouteOptions(itemId);
        string? priceText = null, shortage = null;
        var affordable = true;
        if ((options.Contains(Route.Vendor) || options.Contains(Route.Tomes)) && IsLoaded(GatherBuddy) && Vulcan.PriceOf(itemId) is { } price)
        {
            priceText = $"{price.Cost} {price.CurrencyName}" + (price.Vendor.Length > 0 ? $" · {price.Vendor}" : "");
            affordable = CanAfford(itemId, missing, out shortage);
        }
        return new(itemId, Planner.ItemName(itemId), BagCount(itemId), missing, options, RouteOf(itemId, missing),
                   Config.Routes.ContainsKey(itemId), priceText, affordable, shortage);
    }

    internal void SetRoute(uint itemId, Route? route)
    {
        if (route is { } r) Config.Routes[itemId] = r;
        else Config.Routes.Remove(itemId);
        SaveConfig();
        Recalculate();
    }

    internal static string RouteName(Route r) => r switch
    {
        Route.Gather => "Сбор",
        Route.Bicolor => "Биколоры",
        Route.Vendor => "Вендор",
        Route.GcFarm => "Фарм печатей GC",
        Route.MobFarm => "Фарм мобов (Hunter)",
        Route.ScripFarm => "Фарм скриптов",
        Route.Tomes => "За томстоуны",
        _ => "Рынок / вручную",
    };

    // ---------------------------------------------------------------- cart

    /// <summary>Last import result shown on the Cart tab; set from the framework thread.</summary>
    internal string CartMessage { get; set; } = string.Empty;
    internal bool ImportBusy { get; private set; }

    /// <returns>true when the import finished (or failed) right away; false while a list link is downloading.</returns>
    internal bool Import(string text, bool replace)
    {
        if (Teamcraft.ListId(text) is { } listId)
        {
            if (ImportBusy) return false;
            ImportBusy = true;
            CartMessage = "Загружаю список Teamcraft…";
            Task.Run(async () =>
            {
                string? json = null, error = null;
                try { json = await Teamcraft.FetchListJson(listId); }
                catch (Exception ex) { error = $"Не удалось загрузить список Teamcraft: {ex.Message}"; }
                await Framework.RunOnFrameworkThread(() =>
                {
                    ImportBusy = false;
                    if (error != null) { CartMessage = error; return; }
                    try
                    {
                        var res = Teamcraft.FromListJson(json!, Planner, out var name);
                        var msg = ApplyImport(res, replace);
                        if (res.Error == null) { Config.TeamcraftListUrl = $"https://ffxivteamcraft.com/list/{listId}"; SaveConfig(); }
                        CartMessage = (name.Length > 0 ? $"«{name}»: " : "") + msg;
                    }
                    catch (Exception ex)
                    {
                        CartMessage = $"Не разобрал список Teamcraft: {ex.Message}";
                    }
                });
            });
            return false;
        }
        CartMessage = ApplyImport(Teamcraft.Parse(text, Planner), replace);
        return true;
    }

    private string ApplyImport(ImportResult res, bool replace)
    {
        if (res.Error != null)
            return res.Error + (res.Skipped.Count > 0 ? " " + string.Join("; ", res.Skipped) : "");
        if (replace)
        {
            Config.Cart.Clear();
            Config.ImportManual.Clear();
            Config.TeamcraftListUrl = string.Empty;
            Config.ResumeFinals = false;
        }
        foreach (var l in res.Lines) AddToCart(l.RecipeId, l.Crafts, save: false);
        foreach (var m in res.Manual) if (!Config.ImportManual.Contains(m)) Config.ImportManual.Add(m);
        SaveConfig();
        Recalculate();
        SyncVulcanList();
        var msg = $"Импортировано рецептов: {res.Lines.Count}.";
        if (res.Skipped.Count > 0) msg += " Заметки: " + string.Join("; ", res.Skipped.Take(8)) + (res.Skipped.Count > 8 ? " …" : "");
        return msg;
    }

    internal void AddToCart(uint recipeId, int crafts, bool save = true)
    {
        if (crafts <= 0 || !Planner.IsRecipe(recipeId)) return;
        var e = Config.Cart.FirstOrDefault(x => x.RecipeId == recipeId);
        if (e != null) e.Crafts += crafts;
        else Config.Cart.Add(new CartEntry { RecipeId = recipeId, Crafts = crafts });
        if (save) SaveConfig();
    }

    internal void ClearCart()
    {
        Config.ResumeFinals = false;
        Config.Cart.Clear();
        Config.ImportManual.Clear();
        Config.TeamcraftListUrl = string.Empty;
        SaveConfig();
        Recalculate();
    }

    /// <summary>After a craft count was edited or a line removed on the Cart tab.</summary>
    internal void CartEdited()
    {
        SaveConfig();
        Recalculate();
        SyncVulcanList();
    }

    /// <summary>The imported Teamcraft list, or a Teamcraft import link for the cart; null when there is nothing.</summary>
    internal string? TeamcraftLink() =>
        Config.TeamcraftListUrl.Length > 0 ? Config.TeamcraftListUrl
        : Config.Cart.Count > 0 ? Teamcraft.ImportLink(CartLines(), Planner)
        : null;

    internal void OpenTeamcraft()
    {
        if (TeamcraftLink() is { } url) Dalamud.Utility.Util.OpenLink(url);
        else Print("корзина пуста — нечего открывать в Teamcraft.");
    }

    private DateTime materialsUntil = DateTime.MinValue;

    /// <summary>Syncs the cart into Vulcan's "CraftMeThis" list and opens it with Vulcan's Materials window.</summary>
    internal void OpenVulcanMaterials()
    {
        if (!IsLoaded(GatherBuddy)) { Print("GatherBuddy Reborn не загружен."); return; }
        if (Config.Cart.Count == 0) { Print("корзина пуста — нечего показывать."); return; }
        if (Vulcan.SyncList(CartLines(), Config.UseRetainers, Config.ResumeFinals, out var err) == null) { Print(err!); return; }
        Run($"/vulcan {VulcanLink.ListName}");
        materialsUntil = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>Keeps Vulcan's "CraftMeThis" list in step with the cart (quietly; nothing to do without GatherBuddy).</summary>
    private void SyncVulcanList()
    {
        if (Running || Config.Cart.Count == 0 || !IsLoaded(GatherBuddy)) return;
        if (Vulcan.SyncList(CartLines(), Config.UseRetainers, Config.ResumeFinals, out var err) == null) Log.Warning(err ?? "Vulcan sync failed");
    }

    internal void OpenVulcan()
    {
        if (!IsLoaded(GatherBuddy)) Print("GatherBuddy Reborn не загружен.");
        else Run("/vulcan");
    }

    internal void OpenFateGrind()
    {
        if (!IsLoaded(AutoFateGrind)) Print("Auto FATE Grind не загружен.");
        else Run("/afg");
    }

    /// <summary>What CraftMeThis (with Vulcan and Auto FATE Grind) will not do on its own for the cart.</summary>
    /// <summary>
    /// Why the character cannot craft a recipe: a master recipe book that is not learned, or the crafter level.
    /// Vulcan cannot open such a recipe and retries it forever, so these precrafts are planned as purchases instead.
    /// </summary>
    internal static unsafe string? RecipeBlockReason(uint recipeId)
    {
        if (!ClientState.IsLoggedIn || !Data.GetExcelSheet<Recipe>().TryGetRow(recipeId, out var r)) return null;
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (ps == null) return null;
        if (UnlearnedBook(recipeId) is { } book) return $"не изучено: {book.Name}";
        var job = 8 + r.CraftType.RowId;
        if (Data.GetExcelSheet<ClassJob>().TryGetRow(job, out var cj) && cj.ExpArrayIndex >= 0 && cj.ExpArrayIndex < ps->ClassJobLevels.Length)
        {
            var have = ps->ClassJobLevels[cj.ExpArrayIndex];
            var need = r.RecipeLevelTable.Value.ClassJobLevel;
            if (have < need) return $"{cj.Abbreviation.ExtractText()} {have} уровня, а нужен {need}";
        }
        return null;
    }

    /// <summary>The master recipe book a recipe needs, when the character has not learned it yet.</summary>
    internal static unsafe (uint BookId, uint ItemId, string Name)? UnlearnedBook(uint recipeId)
    {
        if (!ClientState.IsLoggedIn || !Data.GetExcelSheet<Recipe>().TryGetRow(recipeId, out var r)) return null;
        var book = r.SecretRecipeBook.RowId;
        if (book == 0 || !r.SecretRecipeBook.IsValid) return null;
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        if (ps == null || ps->IsSecretRecipeBookUnlocked(book)) return null;
        var sb = r.SecretRecipeBook.Value;
        return (book, sb.Item.RowId, sb.Name.ExtractText());
    }

    /// <summary>Recipes in the cart's tree the character cannot craft now, with the reason (still missing ones only).</summary>
    internal List<(uint RecipeId, string Why)> BlockedRecipes(IReadOnlyList<CartLine> lines)
    {
        Planner.Build(lines, BagCount, id => RouteOf(id) == Route.Bicolor);
        return Planner.LastRecipes.Select(id => (id, RecipeBlockReason(id))).Where(x => x.Item2 != null).Select(x => (x.id, x.Item2!)).ToList();
    }

    /// <summary>Unlearned master books the cart's tree needs, one per book.</summary>
    internal List<BookNeed> BookNeeds(IReadOnlyList<CartLine> lines)
    {
        Planner.Build(lines, BagCount, id => RouteOf(id) == Route.Bicolor);
        var books = new Dictionary<uint, (uint Item, string Name, List<uint> Recipes)>();
        foreach (var id in Planner.LastRecipes)
        {
            if (UnlearnedBook(id) is not { } b) continue;
            if (!books.TryGetValue(b.BookId, out var e)) books[b.BookId] = e = (b.ItemId, b.Name, new List<uint>());
            e.Recipes.Add(id);
        }
        return books.Select(kv => new BookNeed(kv.Key, kv.Value.Item, kv.Value.Name, kv.Value.Recipes)).ToList();
    }

    internal List<BookNeed> Books { get; private set; } = new();

    /// <summary>One line for the tree: the book, its price and what is still to farm (scrips), or that it waits to be learned.</summary>
    internal string BookLine(uint recipeId)
    {
        if (UnlearnedBook(recipeId) is not { } b) return "";
        if (BagCount(b.ItemId) > 0) return $"Книга {b.Name} уже в сумке — изучу перед крафтом.";
        if (!IsLoaded(GatherBuddy) || Vulcan.PriceOf(b.ItemId) is not { } price)
            return $"Книга {b.Name}: у вендоров Vulcan не нашлась — купи на рынке.";
        var have = Vulcan.CurrencyHave(price);
        var farm = price.Group == "Scrips" ? (have >= price.Cost ? " — хватает, куплю в запуске" : $" — нафармить ещё {price.Cost - have} коллектаблами и купить в запуске") : " — куплю в запуске";
        return $"Книга {b.Name}: {price.Cost} {price.CurrencyName} (есть {have}){farm}.";
    }

    // Learning books before crafting: use the book items that are in the bags.
    private DateTime learnUntil = DateTime.MinValue;

    /// <summary>Uses unlearned book items that are in the bags; true while waiting for the game to register them.</summary>
    // Master books used as soon as they are in the bags (bought with scrips during the run), whatever the phase.
    private DateTime nextBookCheck;
    private readonly Dictionary<uint, (int Tries, DateTime At)> bookTries = new();

    private unsafe void LearnBoughtBooks(DateTime now)
    {
        if (now < nextBookCheck) return;
        nextBookCheck = now.AddSeconds(5);
        try
        {
            if (!IsIdle()) return;
            foreach (var b in BookNeeds(FullEffectiveCart()))
            {
                if (BagCount(b.ItemId) == 0) continue;
                bookTries.TryGetValue(b.ItemId, out var t);
                if (t.Tries >= 3 || now - t.At < TimeSpan.FromSeconds(20)) continue;
                bookTries[b.ItemId] = (t.Tries + 1, now);
                Print(t.Tries == 0 ? $"Купил {b.Name} — сразу изучаю." : $"{b.Name} всё ещё не изучена — пробую ещё раз.");
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()->UseItem(b.ItemId);
                Log.Information($"[books] use {b.Name} on sight (try {t.Tries + 1})");
                return; // one at a time: the use animation blocks the next one
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"learn bought books: {ex.Message}");
        }
    }

    /// <summary>
    /// Before crafting: use the bought master books one at a time (a second use during the first one's animation is
    /// ignored — two books bought together left one unlearned and every recipe blocked). Waits until each is learned,
    /// up to 3 tries of 6 s each; true while still busy.
    /// </summary>
    private unsafe bool LearnBooks(DateTime now)
    {
        var pending = BookNeeds(EffectiveCart()).Where(b => BagCount(b.ItemId) > 0).ToList();
        if (pending.Count == 0) { learnUntil = DateTime.MinValue; bookTries.Clear(); return false; }
        if (now < learnUntil) return true;
        var b = pending.FirstOrDefault(x => bookTries.GetValueOrDefault(x.ItemId).Tries < 3);
        if (b == null)
        {
            learnUntil = DateTime.MinValue;
            Print($"Не получилось изучить: {string.Join(", ", pending.Select(x => x.Name))}. Изучи вручную (используй книгу) — рецепты с ней пропущу.");
            return false;
        }
        var t = bookTries.GetValueOrDefault(b.ItemId);
        bookTries[b.ItemId] = (t.Tries + 1, now);
        Print(t.Tries == 0 ? $"Изучаю {b.Name}." : $"{b.Name} ещё не изучена — пробую снова.");
        try { FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()->UseItem(b.ItemId); }
        catch (Exception ex) { Log.Warning($"use book: {ex.Message}"); }
        Log.Information($"[books] use {b.Name} (try {t.Tries + 1})");
        learnUntil = now.AddSeconds(6);
        Status = "Изучаю мастер-книги…";
        return true;
    }

    internal List<string> ManualTasks()
    {
        var list = new List<string>(Config.ImportManual);
        var levelBlocked = BlockedRecipes(FullEffectiveCart()).Where(b => UnlearnedBook(b.RecipeId) == null).ToList();
        if (levelBlocked.Count > 0)
            list.Add($"Не хватает уровня крафтера: {string.Join("; ", levelBlocked.Select(b => $"{Planner.RecipeName(b.RecipeId)} — {b.Why}"))}. Эти рецепты будут пропущены.");
        foreach (var b in BookNeeds(FullEffectiveCart()))
            list.Add($"Не изучено: {b.Name} (для {string.Join(", ", b.Recipes.Select(Planner.RecipeName))}). {BookLine(b.Recipes[0])}");
        var bought = VendorBuys.Select(v => v.ItemId).ToHashSet();

        var market = Materials.Where(m => m.Route == Route.Market && !bought.Contains(m.ItemId))
                              .Select(m => $"{m.Name} x{m.Missing}").ToList();
        if (market.Count > 0)
            list.Add($"Купить на рынке или добыть самому (путь «рынок / вручную» на вкладке «Материалы»): {string.Join(", ", market)}.");

        if (Zone?.Sold.Where(s => s.Rank > 1).ToList() is { Count: > 0 } ranked && Plan.Gems > 0)
            list.Add($"Открыть ранг {ranked.Max(s => s.Rank)} общих FATE в {Zone.Zone.Name}: без него торговец не продаст {string.Join(", ", ranked.Select(s => s.Item))}.");


        if (Materials.Any(m => m.Route == Route.GcFarm) && GcFarm.DeliveryStatus().Problem is { } delivery)
            list.Add($"Настроить сдачу в GC для фарма печатей: {delivery}");

        var tomes = Materials.Where(m => m.Route is Route.Tomes or Route.Market && m.Options.Contains(Route.Tomes) && !m.Affordable)
                             .Select(m => $"{m.Name} ({m.Shortage})").ToList();
        if (tomes.Count > 0)
            list.Add($"Набрать томстоуны (рулетки, хант, Wonderous Tails — сам плагин их не фармит): {string.Join(", ", tomes)}.");

        var poor = Materials.Where(m => m.Route == Route.Vendor && !m.Affordable).Select(m => $"{m.Name} ({m.Shortage})").ToList();
        if (poor.Count > 0)
            list.Add($"Не хватает валюты у вендора (трайба, печати GC, скрипы…): {string.Join(", ", poor)}. Добудь валюту или переключи путь.");
        return list;
    }

    // ---------------------------------------------------------------- run

    internal void Start()
    {
        if (Running)
        {
            Print("уже работает.");
            return;
        }
        if (Leveler.Running)
        {
            Print("сейчас идёт автоуровень — останови его сначала.");
            return;
        }
        Recalculate();
        Gc = new GcFarm(this);
        scripsDone = false;
        scripQueued = false;
        ResetBatches();
        scripStandalone = false;
        scripQueue.Clear();
        if (Config.Cart.Count == 0)
        {
            SetStatus(Phase.Idle, "Корзина пуста: импортируй список на вкладке «Корзина».");
            return;
        }
        if (!IsLoaded(GatherBuddy))
        {
            SetStatus(Phase.Failed, "GatherBuddy Reborn не загружен: покупки и крафт делает его Vulcan.");
            return;
        }
        if (EffectiveCart().Count == 0)
        {
            SetStatus(Phase.Done, "Докрафт: всё из корзины уже в сумке, делать нечего.");
            return;
        }
        BeginRun();
        if (!DecideBatches()) return;
        RunBatch();
    }

    /// <summary>
    /// The whole pipeline for the current batch (or the whole cart): gemstones → GC seals → scrips → vendors → mob drops
    /// → craft. Each batch starts here again, so currencies are farmed per batch and never above their caps.
    /// </summary>
    private void RunBatch()
    {
        Gc = new GcFarm(this);
        scripsDone = false;
        scripQueued = false;
        scripQueue.Clear();
        Recalculate();
        if (Plan.Gems == 0 || Gems >= Target || !Config.BuyFromVendors)
        {
            EnterBuying();
            return;
        }
        if (!IsLoaded(AutoFateGrind))
        {
            SetStatus(Phase.Failed, $"Нужно ещё {Target - Gems} биколоров, а Auto FATE Grind не загружен.");
            return;
        }

        // Auto FATE Grind only farms; Vulcan buys from the Gemstone Trader afterwards.
        var applyErr = afg.Apply(Config.SetAfgZone && Zone != null ? Zone.Zone.Territory : 0, Array.Empty<(uint, int)>(), Target);
        if (applyErr != null)
        {
            afg.Restore();
            SetStatus(Phase.Failed, applyErr);
            return;
        }

        var wait = 0.5;
        if (Config.SwitchJob && Zone != null)
        {
            var pick = Jobs.Pick(Config.Jobs, Zone.Zone.MinLevel);
            if (pick is { } j && j.Job.Id != Jobs.CurrentJob())
            {
                if (Jobs.Equip(j.Gearset))
                {
                    Print($"переключаюсь на {j.Job.Abbr} {j.Level} (гирсет {j.Gearset + 1}).");
                    wait = 3;
                }
                else
                {
                    Print($"не получилось надеть гирсет {j.Gearset + 1} ({j.Job.Abbr}), фармлю текущим классом.");
                }
            }
        }

        startAt = DateTime.UtcNow.AddSeconds(wait);
        forcedStop = false;
        SetStatus(Phase.Preparing, (Zone != null ? $"Готовлюсь: зона {Zone.Zone.Name}, " : "Готовлюсь: ") + $"нужно {Target} биколоров (есть {Gems}).");
    }

    internal void Stop()
    {
        if (Farming) Run("/afg stop");
        if (Phase == Phase.Buying) Vulcan.StopVendor();
        if (Phase == Phase.GcFarming) Run("/autoduty stop");
        if (Phase == Phase.ScripFarming) Scrips.Stop();
        ResetBatches();
        if (Phase == Phase.MobFarming) { Hunter.SetAutoHunt(false); Hunter.Restore(); RestoreRsr(); }
        if (Phase == Phase.Crafting) Vulcan.StopCrafting();
        afg.Restore();
        RestoreArtisan();
        SetStatus(Phase.Idle, "Остановлено вручную.");
    }

    private void EnterBuying()
    {
        Recalculate();
        // Grand Company seals first, if a GC purchase is short of them and AutoDuty can farm them.
        // Crafter/gatherer scrips next, one kind after another.
        if (Config.BuyFromVendors && !scripsDone && (Gc.Finished || GcSeals().Have >= GcSeals().Need || !Materials.Any(m => m.Route == Route.GcFarm)))
        {
            if (!scripQueued)
            {
                scripQueued = true;
                foreach (var (kind, need) in ScripNeeds())
                    if (ScripFarm.Have(kind) < need) scripQueue.Enqueue((kind, Math.Min(need, ScripLimit(kind))));
            }
            if (scripQueue.Count > 0)
            {
                if (!IsLoaded(GatherBuddy)) { scripQueue.Clear(); }
                else
                {
                    var (kind, target) = scripQueue.Dequeue();
                    var scripErr = Scrips.Begin(kind, target, Config.ScripItem.GetValueOrDefault(kind), Config.ScripAllowFish);
                    if (scripErr == null)
                    {
                        SetStatus(Phase.ScripFarming, $"Фармлю скрипы ({ScripFarm.Info(kind).Name}): {ScripFarm.Have(kind)}/{target} на {Scrips.SetName}.");
                        return;
                    }
                    Print(scripErr + " Покупаю, на что хватает.");
                }
            }
            scripsDone = scripQueue.Count == 0;
            if (!scripsDone) { EnterBuying(); return; }
        }

        var (sealsHave, sealsNeed) = GcSeals();
        if (Config.BuyFromVendors && !Gc.Finished && sealsHave < sealsNeed && Materials.Any(m => m.Route == Route.GcFarm))
        {
            if (!IsLoaded("AutoDuty"))
            {
                Print("Печатей GC не хватает, а AutoDuty не загружен — покупаю, на что хватает.");
            }
            else
            {
                Gc.Begin();
                SetStatus(Phase.GcFarming, $"Фармлю печати GC через AutoDuty: {sealsHave}/{sealsNeed}.");
                return;
            }
        }
        if (!Config.BuyFromVendors || VendorBuys.Count == 0)
        {
            EnterMobFarming();
            return;
        }
        var accepted = Vulcan.SetVendorList(VendorBuys.Select(v => (v.ItemId, v.Target)).ToList(), out var err);
        if (err != null)
        {
            SetStatus(Phase.Failed, err);
            return;
        }
        var refused = VendorBuys.Where(v => !accepted.Contains(v.ItemId)).Select(v => v.Name).ToList();
        if (refused.Count > 0) Print($"Vulcan не нашёл вендора для: {string.Join(", ", refused)}.");
        if (accepted.Count == 0)
        {
            EnterMobFarming();
            return;
        }
        vendorStarted = false;
        phaseStartedAt = DateTime.UtcNow;
        SetStatus(Phase.Buying, $"Vulcan закупает у вендоров: {string.Join(", ", VendorBuys.Where(v => accepted.Contains(v.ItemId)).Select(v => $"{v.Name} x{v.Units}"))}.");
    }

    private void EnterCrafting()
    {
        if (!Config.CraftAfterBuying)
        {
            SetStatus(Phase.Done, "Покупки готовы. Крафт выключен в настройках — запусти его в Vulcan сам.");
            return;
        }
        craftStarted = false;
        learnUntil = DateTime.MinValue; bookTries.Clear();
        phaseStartedAt = DateTime.UtcNow;
        if (VulcanLink.ArtisanLoaded)
        {
            if (!Config.ToggleArtisan)
            {
                SetStatus(Phase.Failed, "Vulcan не крафтит при загруженном Artisan. Выключи Artisan или включи «выключать Artisan» в настройках.");
                return;
            }
            var err = Vulcan.SetArtisan(false);
            if (err != null)
            {
                SetStatus(Phase.Failed, err);
                return;
            }
            artisanTurnedOff = true;
            SetStatus(Phase.Crafting, "Выключаю Artisan для крафта в Vulcan…");
            return;
        }
        SetStatus(Phase.Crafting, "Запускаю крафт в Vulcan…");
    }

    private void RestoreArtisan()
    {
        if (!artisanTurnedOff) return;
        artisanTurnedOff = false;
        if (Config.ReenableArtisan && Vulcan.SetArtisan(true) is { } err) Print(err);
    }

    private void OnUpdate(IFramework _)
    {
        if (materialsUntil != DateTime.MinValue)
        {
            // The Vulcan window builds the list editor a frame after "/vulcan <list>".
            if (Vulcan.ShowMaterialsWindow() || DateTime.UtcNow > materialsUntil) materialsUntil = DateTime.MinValue;
        }
        if (!Running && !Leveler.Running) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddSeconds(1);
        if (!ClientState.IsLoggedIn) return;
        if (Leveler.Running) { Leveler.Tick(now); return; }
        LearnBoughtBooks(now);

        switch (Phase)
        {
            case Phase.Preparing: TickPreparing(now); break;
            case Phase.Farming: TickFarming(now); break;
            case Phase.Stopping: TickStopping(now); break;
            case Phase.GcFarming:
                Gc.Tick(now);
                Status = Gc.Now;
                if (Gc.Finished) EnterBuying();
                break;
            case Phase.ScripFarming:
                Scrips.Tick(now);
                Status = Scrips.Now;
                if (!Scrips.Running)
                {
                    if (scripStandalone) { scripStandalone = false; SetStatus(Phase.Done, Scrips.Now); }
                    else EnterBuying();
                }
                break;
            case Phase.Buying: TickBuying(now); break;
            case Phase.AfgBuying: TickAfgBuying(now); break;
            case Phase.MobFarming: TickMobFarming(now); break;
            case Phase.Crafting: TickCrafting(now); break;
        }
    }

    private void TickPreparing(DateTime now)
    {
        if (now < startAt) return;
        if (!Run("/afg start"))
        {
            afg.Restore();
            SetStatus(Phase.Failed, "Команда /afg start не сработала.");
            return;
        }
        SetStatus(Phase.Farming, $"Фармлю FATE{(Zone != null ? " в " + Zone.Zone.Name : "")}: {Gems}/{Target} биколоров.");
    }

    private void TickFarming(DateTime now)
    {
        Gems = GemCount();
        Status = $"Фармлю FATE{(Zone != null ? " в " + Zone.Zone.Name : "")}: {Gems}/{Target} биколоров.";
        if (Gems < Target) return;
        Run("/afg stop soft");
        stopSentAt = now;
        idleSince = DateTime.MinValue;
        afterStop = Phase.Buying;
        SetStatus(Phase.Stopping, $"Набрал {Gems}/{Target}. Жду, пока Auto FATE Grind закончит текущий FATE.");
    }

    private void TickStopping(DateTime now)
    {
        if (!forcedStop && now - stopSentAt > TimeSpan.FromMinutes(Math.Max(1, Config.SoftStopTimeoutMinutes)))
        {
            Run("/afg stop");
            forcedStop = true;
            Status = "Мягкая остановка затянулась, остановил Auto FATE Grind принудительно.";
        }
        if (now - stopSentAt < TimeSpan.FromSeconds(3) || !IsIdle())
        {
            idleSince = DateTime.MinValue;
            return;
        }
        if (idleSince == DateTime.MinValue) idleSince = now;
        if (now - idleSince < TimeSpan.FromSeconds(Math.Max(2, Config.IdleSeconds))) return;

        afg.Restore();
        if (afterStop == Phase.Crafting) EnterMobFarming();
        else EnterBuying();
    }

    private void TickBuying(DateTime now)
    {
        if (!vendorStarted)
        {
            var res = Vulcan.StartVendorList();
            switch (res)
            {
                case "Started":
                case "AlreadyRunning":
                    vendorStarted = true;
                    phaseStartedAt = now;
                    return;
                case "Empty":
                case "NoPendingEntries":
                    EnterMobFarming();
                    return;
                case "VendorDataLoading":
                case "LocationDataLoading":
                case "WaitingForPreviousInteraction":
                case "AnotherPurchaseRunning":
                    Status = $"Vulcan ещё не готов к покупкам ({res}), жду…";
                    if (now - phaseStartedAt > TimeSpan.FromSeconds(120))
                        SetStatus(Phase.Failed, $"Vulcan так и не начал покупки: {res}. {Vulcan.VendorStatus}");
                    return;
                default:
                    SetStatus(Phase.Failed, $"Vulcan не начал покупки: {res}. {Vulcan.VendorStatus}");
                    return;
            }
        }

        if (Vulcan.VendorBusy || now - phaseStartedAt < TimeSpan.FromSeconds(3))
        {
            var vs = Vulcan.VendorStatus;
            Status = "Vulcan закупает у вендоров" + (vs.Length > 0 ? $": {vs}" : "…");
            return;
        }

        var missing = VendorBuys.Where(v => BagCount(v.ItemId) < v.Target).ToList();
        if (missing.Count > 0)
            Print($"Vulcan купил не всё: {string.Join(", ", missing.Select(v => $"{v.Name} {BagCount(v.ItemId)}/{v.Target}"))}. {Vulcan.VendorStatus}");

        // Gemstone items Vulcan could not buy (e.g. "Timed out opening Kajeel Ja's special shop"):
        // Auto FATE Grind's own trader hand-off buys them instead.
        var gemMissing = missing.Where(v => v.ForGemstones).ToList();
        if (gemMissing.Count > 0 && IsLoaded(AutoFateGrind))
        {
            EnterAfgBuying(gemMissing);
            return;
        }
        EnterMobFarming();
    }

    /// <summary>Mob drops through Hunter, then crafting.</summary>
    private void EnterMobFarming()
    {
        Recalculate();
        mobFarm = Materials.Where(m => m.Route == Route.MobFarm && m.Missing > 0)
                           .Select(m => new VendorBuy(m.ItemId, m.Name, m.Missing, m.Have + m.Missing, false)).ToList();
        if (mobFarm.Count == 0) { EnterCrafting(); return; }
        if (!IsLoaded(HunterLink.InternalName))
        {
            Print("Hunter не загружен — дроп с мобов не фармлю.");
            EnterCrafting();
            return;
        }
        var err = Hunter.SetList(mobFarm.Select(v => (v.ItemId, EnglishName(v.ItemId), v.Target)).ToList()) ?? Hunter.SetAutoHunt(true);
        if (err != null)
        {
            Hunter.Restore();
            Print(err);
            EnterCrafting();
            return;
        }
        EquipFarmJob(forDungeons: true);
        phaseStartedAt = DateTime.UtcNow;
        mobIdleSince = DateTime.MinValue;
        rsrChecked = false;
        SetStatus(Phase.MobFarming, $"Hunter фармит мобов: {string.Join(", ", mobFarm.Select(v => $"{v.Name} ×{v.Units}"))}.");
    }

    private void TickMobFarming(DateTime now)
    {
        // Once the farm job is on (gearset swap takes a moment), let RSR pull fresh mobs: Hunter only targets and waits.
        if (!rsrChecked && now - phaseStartedAt > TimeSpan.FromSeconds(3))
        {
            rsrChecked = true;
            if (Config.RsrEngageAll && rsr.Allow(CurrentJobAbbr()) is { } note) Print(note);
        }
        var left = mobFarm.Where(v => BagCount(v.ItemId) < v.Target).ToList();
        var hs = Hunter.Status();
        Status = left.Count == 0 ? "Дроп собран, жду, пока Hunter закончит."
            : $"Hunter фармит: {string.Join(", ", left.Select(v => $"{v.Name} {BagCount(v.ItemId)}/{v.Target}"))}" + (hs.Length > 0 ? $" — {hs}" : "");
        var timedOut = now - phaseStartedAt > TimeSpan.FromMinutes(Math.Max(5, Config.MobFarmMaxMinutes));

        if (Hunter.AutoHuntOn)
        {
            if (left.Count > 0 && !timedOut) return;                  // still hunting
            if (left.Count == 0)
            {
                // Everything dropped: Hunter turns itself off after "/li thespot"; give it a minute to do so.
                if (mobIdleSince == DateTime.MinValue) mobIdleSince = now;
                if (now - mobIdleSince < TimeSpan.FromSeconds(60)) return;
            }
            else
            {
                Print($"Hunter за {Config.MobFarmMaxMinutes} мин собрал не всё — крафчу, что получится.");
            }
            Hunter.SetAutoHunt(false);
        }
        if (!IsIdle()) return;
        Hunter.Restore();
        RestoreRsr();
        if (left.Count > 0 && !timedOut)
            Print($"Hunter остановился, а собрано не всё: {string.Join(", ", left.Select(v => $"{v.Name} {BagCount(v.ItemId)}/{v.Target}"))}.");
        EnterCrafting();
    }

    private void EnterAfgBuying(List<VendorBuy> items)
    {
        afgBuys = items;
        var gems = 0L;
        foreach (var v in items)
        {
            var p = Plan.Purchases.FirstOrDefault(x => x.ItemId == v.ItemId);
            if (p == null) continue;
            var units = Math.Max(0, v.Target - BagCount(v.ItemId));
            gems += (units + p.ReceivePerTrade - 1) / p.ReceivePerTrade * p.CostPerTrade;
        }
        var err = afg.Apply(Config.SetAfgZone && Zone != null ? Zone.Zone.Territory : 0,
            items.Select(v => (v.ItemId, v.Target)).ToList(), (int)Math.Clamp(gems, 1, Planner.GemCap));
        if (err != null || !Run("/afg start"))
        {
            afg.Restore();
            Print(err ?? "Команда /afg start не сработала.");
            EnterCrafting();
            return;
        }
        phaseStartedAt = DateTime.UtcNow;
        SetStatus(Phase.AfgBuying, $"Vulcan не смог купить за биколоры — покупает Auto FATE Grind: {string.Join(", ", items.Select(v => v.Name))}.");
    }

    private void TickAfgBuying(DateTime now)
    {
        var left = afgBuys.Where(v => BagCount(v.ItemId) < v.Target).ToList();
        if (left.Count > 0)
        {
            Status = $"Auto FATE Grind покупает у Gemstone Trader: осталось {string.Join(", ", left.Select(v => $"{v.Name} {BagCount(v.ItemId)}/{v.Target}"))}.";
            if (now - phaseStartedAt < TimeSpan.FromMinutes(20)) return;
            Print("Auto FATE Grind за 20 минут не купил всё — крафчу то, что получится.");
        }
        Run("/afg stop soft");
        stopSentAt = now;
        idleSince = DateTime.MinValue;
        forcedStop = false;
        afterStop = Phase.Crafting;
        SetStatus(Phase.Stopping, "Покупки за биколоры готовы. Жду, пока Auto FATE Grind остановится.");
    }

    private void TickCrafting(DateTime now)
    {
        if (!craftStarted)
        {
            if (VulcanLink.ArtisanLoaded)
            {
                if (now - phaseStartedAt > TimeSpan.FromSeconds(30))
                    SetStatus(Phase.Failed, "Artisan так и не выгрузился, а Vulcan не крафтит при загруженном Artisan.");
                return;
            }
            if (LearnBooks(now)) return;
            var batchLines = currentBatch ?? CartLines();
            var craftable = new List<CartLine>();
            var skipped = new List<string>();
            foreach (var l in batchLines)
            {
                var blocked = BlockedRecipes(new[] { l });
                if (blocked.Count == 0) craftable.Add(l);
                else skipped.Add($"{Planner.RecipeName(l.RecipeId)} ({string.Join(", ", blocked.Select(b => $"{Planner.RecipeName(b.RecipeId)}: {b.Why}"))})");
            }
            if (skipped.Count > 0)
                Print($"Не отдаю в Vulcan то, что упирается в недоступный рецепт: {string.Join("; ", skipped)}. Когда книга будет изучена — докрафт.");
            if (craftable.Count == 0)
            {
                RestoreArtisan();
                Config.ResumeFinals = true;
                SaveConfig();
                SetStatus(Phase.Failed, "Все рецепты партии упираются в недоступные рецепты — крафтить нечего.");
                return;
            }
            var err = currentBatch != null
                ? Vulcan.StartCraftingNamed("CraftMeThis Batch", craftable, Config.UseRetainers)
                : Vulcan.StartCrafting(craftable, Config.UseRetainers, Config.ResumeFinals);
            if (err != null)
            {
                RestoreArtisan();
                SetStatus(Phase.Failed, err);
                return;
            }
            craftStarted = true;
            phaseStartedAt = now;
            craftBaseline = new();
            FailedItems.Clear();
            craftSig = default;
            craftSigAt = now;
            craftDismounts = 0;
            craftHang = null;
            WarnMissingBeforeCraft();
            foreach (var c in EffectiveCart())
            {
                var item = Planner.RecipeItem(c.RecipeId);
                if (item == 0 || craftBaseline.ContainsKey(item)) continue;
                craftBaseline[item] = (BagCount(item), c.Crafts * Planner.RecipeYield(c.RecipeId), Planner.ItemName(item), c.RecipeId, c.Crafts);
            }
            SetStatus(Phase.Crafting, $"Vulcan собирает и крафтит корзину ({Config.Cart.Count} рецептов).");
            return;
        }

        if (Vulcan.CraftingRunning || now - phaseStartedAt < TimeSpan.FromSeconds(5))
        {
            var st = Vulcan.CraftingState;
            Status = "Vulcan: " + (st.Length > 0 ? st : "запускается…");
            WatchCraft(now, st);
            return;
        }
        RestoreArtisan();
        if (craftHang != null) Print(craftHang);
        ReportCrafts();
    }

    /// <summary>
    /// Vulcan's queue does not notice some failures: crafting while mounted, or an ingredient it cannot select that
    /// it reports as a timeout instead of "missing materials" (Cordia Lumber without Cordia Log looped for 7 hours).
    /// When the queue position, the craft count and the cart items in the bags stay the same for a few minutes outside
    /// gathering/retainer/solver states: dismount (twice at most), then stop Vulcan and report the stuck recipe.
    /// </summary>
    private unsafe void WatchCraft(DateTime now, string state)
    {
        var prog = Vulcan.QueueProgress();
        var bags = craftBaseline.Keys.Sum(i => (long)BagCount(i));
        if (prog is { Recipe: not 0 } pr) bags += BagCount(Planner.RecipeItem(pr.Recipe));
        var sig = (prog?.Index ?? -1, prog?.Recipe ?? 0u, prog?.Done ?? 0, bags);
        var waiting = state.Contains("Gather") || state.Contains("Retainer") || state.Contains("Raphael") || state.Contains("Repair");
        if (sig != craftSig || waiting) { craftSig = sig; craftSigAt = now; return; }
        if (now - craftSigAt < CraftStall) return;

        var recipe = prog?.Recipe ?? 0;
        var name = recipe != 0 ? Planner.RecipeName(recipe) : "рецепте";
        if (Condition[ConditionFlag.Mounted] && craftDismounts < 2)
        {
            craftDismounts++;
            craftSigAt = now;
            Print($"Vulcan {CraftStall.TotalMinutes:0} мин не может начать {name}, а персонаж на маунте — спешиваюсь.");
            try { ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23); } // Dismount
            catch (Exception ex) { Log.Warning($"dismount: {ex.Message}"); }
            return;
        }

        var lacking = recipe == 0 ? new List<string>() : Planner.Ingredients(recipe)
            .Where(i => i.ItemId > 19 && BagCount(i.ItemId) < i.Amount)
            .Select(i => $"{Planner.ItemName(i.ItemId)} {BagCount(i.ItemId)}/{i.Amount}").ToList();
        craftHang = $"Vulcan завис на {name}: {(now - craftSigAt + CraftStall).TotalMinutes:0} мин без прогресса"
                    + (lacking.Count > 0 ? $", не хватает: {string.Join(", ", lacking)}" : "")
                    + ". Остановил крафт; остальное — в отчёте ниже, докрафт продолжит после того, как добудешь недостающее.";
        Vulcan.StopCrafting();
        craftSigAt = now;
    }

    /// <summary>Before crafting: raw materials still missing that Vulcan will not gather itself (vendors that failed,
    /// scrip items, market, mob drops) — the recipes that need them will be skipped.</summary>
    private void WarnMissingBeforeCraft()
    {
        try
        {
            var plan = Planner.Build(EffectiveCart(), BagCount, id => RouteOf(id) == Route.Bicolor);
            var lacking = plan.Leaves.Where(kv => kv.Value > 0 && RouteOf(kv.Key, kv.Value) != Route.Gather)
                              .Select(kv => $"{Planner.ItemName(kv.Key)} ×{kv.Value}")
                              .Concat(plan.Purchases.Where(g => g.Units > 0).Select(g => $"{Planner.ItemName(g.ItemId)} ×{g.Units}"))
                              .ToList();
            if (lacking.Count > 0)
                Print($"Перед крафтом не хватает того, что Vulcan сам не добудет: {string.Join(", ", lacking)}"
                      + (Config.UseRetainers ? " (если нет у ретейнеров)" : "") + ". Рецепты с ними будут пропущены.");
        }
        catch (Exception ex)
        {
            Log.Warning($"missing check: {ex.Message}");
        }
    }

    /// <summary>Compares the bags with what the cart asked for and says what was not crafted and why.</summary>
    private void ReportCrafts()
    {
        var done = new List<string>();
        var failed = new List<string>();
        foreach (var (item, b) in craftBaseline)
        {
            var got = Math.Max(0, BagCount(item) - b.Before);
            if (got >= b.Expected) { done.Add($"{b.Name} +{got}"); continue; }
            // Ingredients not even enough for one more craft: the usual reason Vulcan skipped the recipe.
            var lacking = Planner.Ingredients(b.RecipeId)
                .Where(i => i.ItemId > 19 && BagCount(i.ItemId) < i.Amount)
                .Select(i => $"{Planner.ItemName(i.ItemId)} {BagCount(i.ItemId)}/{i.Amount}")
                .ToList();
            FailedItems.Add(item);
            failed.Add($"{b.Name} {got}/{b.Expected}" + (lacking.Count > 0 ? $" (не хватает: {string.Join(", ", lacking)})" : ""));
        }
        if (currentBatch != null)
        {
            anyBatchFailed |= failed.Count > 0;
            Print($"Партия {BatchNo} готова. Скрафчено: {(done.Count > 0 ? string.Join(", ", done) : "ничего")}"
                  + (failed.Count > 0 ? $". Не скрафчено: {string.Join("; ", failed)}." : "."));
            if (remainingLines.Count > 0)
            {
                if (StartNextBatch()) RunBatch();
                return;
            }
            currentBatch = null;
            failed.Clear();
            if (anyBatchFailed) failed.Add("часть партий — см. сообщения выше");
        }
        // A failed run switches to resume mode, so the next start only crafts what is still missing.
        Config.ResumeFinals = failed.Count > 0;
        SaveConfig();
        Recalculate();
        SyncVulcanList();
        if (failed.Count == 0)
            SetStatus(Phase.Done, $"Готово: {(done.Count > 0 ? string.Join(", ", done) : "всё уже было в сумке")}.");
        else
            SetStatus(Phase.Done, $"Vulcan закончил. Скрафчено: {(done.Count > 0 ? string.Join(", ", done) : "ничего")}. Не скрафчено: {string.Join("; ", failed)}. "
                + "Следующий запуск докрафтит только недостающее (Include Final Crafts).");
    }

    // ---------------------------------------------------------------- live activity

    /// <summary>Pipeline steps for the activity strip: name, state (-1 skipped, 0 to do, 1 now, 2 done).</summary>
    internal List<(string Name, int State)> Steps()
    {
        var farmNeeded = Plan.Gems > 0 && Gems < Target;
        var steps = new List<(string, int)>
        {
            ("Фарм биколоров", Phase is Phase.Preparing or Phase.Farming or Phase.Stopping && afterStop == Phase.Buying ? 1
                : Phase is Phase.Buying or Phase.AfgBuying or Phase.Crafting or Phase.Done ? 2 : farmNeeded ? 0 : -1),
            ("Фарм печатей GC (AutoDuty)", Phase == Phase.GcFarming ? 1
                : Gc.Finished || (Phase is Phase.Buying or Phase.AfgBuying or Phase.Crafting or Phase.Done && Materials.Any(m => m.Route == Route.GcFarm)) ? 2
                : Materials.Any(m => m.Route == Route.GcFarm) ? 0 : -1),
            ("Фарм скриптов (GatherBuddy)", Phase == Phase.ScripFarming ? 1
                : Phase is Phase.Buying or Phase.AfgBuying or Phase.MobFarming or Phase.Crafting or Phase.Done && Materials.Any(m => m.Route == Route.ScripFarm) ? 2
                : Materials.Any(m => m.Route == Route.ScripFarm) ? 0 : -1),
            ("Покупки у вендоров", Phase == Phase.Buying ? 1 : Phase is Phase.AfgBuying or Phase.Crafting or Phase.Done ? 2 : VendorBuys.Count > 0 ? 0 : -1),
            ("Фарм мобов (Hunter)", Phase == Phase.MobFarming ? 1 : Phase is Phase.Crafting or Phase.Done && Materials.Any(m => m.Route == Route.MobFarm) ? 2
                : Materials.Any(m => m.Route == Route.MobFarm) ? 0 : -1),
            ("Сбор и крафт", Phase == Phase.Crafting ? 1 : Phase == Phase.Done ? 2 : Config.Cart.Count > 0 ? 0 : -1),
        };
        if (Phase is Phase.AfgBuying || (Phase == Phase.Stopping && afterStop == Phase.Crafting))
            steps.Insert(2, ("Покупка за биколоры (AFG)", 1));
        return steps;
    }

    /// <summary>What is happening right now, in detail, and what comes next.</summary>
    internal (string Now, List<string> Next) Activity()
    {
        var (now0, next0) = ActivityCore();
        if (Batching) { now0 = $"[партия {BatchNo}] " + now0; if (BatchesLeft > 0) next0.Add($"Потом ещё партии: осталось {BatchesLeft} строк корзины"); }
        return (now0, next0);
    }

    private (string Now, List<string> Next) ActivityCore()
    {
        var next = new List<string>();
        string ItemList(IEnumerable<VendorBuy> v) => string.Join(", ", v.Select(x => $"{x.Name} ×{Math.Max(0, x.Target - BagCount(x.ItemId))}"));
        var toBuy = VendorBuys.Where(v => BagCount(v.ItemId) < v.Target).ToList();
        var crafts = string.Join(", ", EffectiveCart().Select(c => $"{Planner.RecipeName(c.RecipeId)} ×{c.Crafts}"));

        switch (Phase)
        {
            case Phase.Preparing:
                return ("Готовлю фарм: класс и зона в Auto FATE Grind…", Upcoming(toBuy, crafts, ItemList));
            case Phase.Farming:
                return ($"Auto FATE Grind фармит FATE{(Zone != null ? " в " + Zone.Zone.Name : "")}: биколоры {Gems}/{Target}.", Upcoming(toBuy, crafts, ItemList));
            case Phase.Stopping:
                return ("Auto FATE Grind доделывает текущий FATE и останавливается.", afterStop == Phase.Crafting ? new() { $"Скрафтить: {crafts}" } : Upcoming(toBuy, crafts, ItemList));
            case Phase.Buying:
            {
                var item = Vulcan.ActiveVendorItem();
                var v = VendorBuys.FirstOrDefault(x => x.ItemId == item);
                var vs = Vulcan.VendorStatus;
                var now = v != null
                    ? $"Vulcan покупает {v.Name} ×{Math.Max(0, v.Target - BagCount(v.ItemId))}" + (Vulcan.PriceOf(v.ItemId) is { } pr && pr.Vendor.Length > 0 ? $" у {pr.Vendor}" : "")
                    : "Vulcan закупает у вендоров";
                if (vs.Length > 0) now += $" — {vs}";
                var rest = toBuy.Where(x => x.ItemId != item).ToList();
                if (rest.Count > 0) next.Add($"Купить: {ItemList(rest)}");
                next.Add($"Скрафтить: {crafts}");
                return (now, next);
            }
            case Phase.ScripFarming:
                return (Scrips.Now, new() { $"Купить: {ItemList(toBuy)}", $"Скрафтить: {crafts}" });
            case Phase.GcFarming:
            {
                var (h, n) = GcSeals();
                return ($"{Gc.Now} (печати {h}/{n})", new() { $"Купить: {ItemList(toBuy)}", $"Скрафтить: {crafts}" });
            }
            case Phase.MobFarming:
                return (Status, new() { $"Скрафтить: {crafts}" });
            case Phase.AfgBuying:
                return ($"Auto FATE Grind покупает у Gemstone Trader: {ItemList(toBuy.Where(v => v.ForGemstones))}.", new() { $"Скрафтить: {crafts}" });
            case Phase.Crafting:
            {
                var state = Vulcan.CraftingState;
                var prog = Vulcan.QueueProgress();
                string now;
                if (state.Contains("WaitingForGather"))
                {
                    var g = VulcanLink.AutoGatherStatus();
                    now = "Vulcan собирает недостающее" + (g.Length > 0 ? $": {g}" : "…");
                }
                else if (state.Contains("Retainer")) now = "Vulcan забирает материалы у ретейнеров.";
                else if (state.Contains("JobSwitch")) now = "Vulcan меняет класс для следующего рецепта.";
                else if (state.Contains("Raphael")) now = "Vulcan считает решение крафта (Raphael).";
                else if (state.Contains("Repair")) now = "Vulcan чинит снаряжение.";
                else if (prog is { Recipe: not 0 } pr)
                    now = $"Vulcan крафтит {Planner.RecipeName(pr.Recipe)} {pr.Done}/{pr.Total}";
                else now = "Vulcan: " + (state.Length > 0 ? state : "запускается…");
                if (prog is { } p2 && p2.Count > 0) now += $" · позиция очереди {Math.Min(p2.Index + 1, p2.Count)}/{p2.Count}";
                var left = EffectiveCart().Where(c => prog == null || c.RecipeId != prog.Value.Recipe)
                                          .Select(c => Planner.RecipeName(c.RecipeId)).ToList();
                if (left.Count > 0) next.Add($"Ещё в очереди: {string.Join(", ", left)}");
                return (now, next);
            }
            case Phase.Done:
            case Phase.Failed:
                return (Status, Upcoming(toBuy, crafts, ItemList, Plan.Gems > 0 && Gems < Target));
            default:
                return ("Ничего не запущено.", Upcoming(toBuy, crafts, ItemList, Plan.Gems > 0 && Gems < Target));
        }
    }

    private List<string> Upcoming(List<VendorBuy> toBuy, string crafts, Func<IEnumerable<VendorBuy>, string> list, bool farm = false)
    {
        var next = new List<string>();
        if (farm) next.Add($"Нафармить биколоры{(Zone != null ? " в " + Zone.Zone.Name : "")}: {Gems}/{Target}");
        var (sh, sn) = GcSeals();
        if (sh < sn && Materials.Any(m => m.Route == Route.GcFarm)) next.Add($"Нафармить печати GC через AutoDuty: {sh}/{sn}");
        var mobs = Materials.Where(m => m.Route == Route.MobFarm).Select(m => $"{m.Name} ×{m.Missing}").ToList();
        if (mobs.Count > 0) next.Add($"Нафармить с мобов (Hunter): {string.Join(", ", mobs)}");
        if (toBuy.Count > 0) next.Add($"Купить: {list(toBuy)}");
        if (crafts.Length > 0) next.Add($"Скрафтить: {crafts}");
        return next;
    }

    /// <summary>Grand Company seals: (have, needed for the GC purchases still missing).</summary>
    internal (long Have, long Need) GcSeals()
    {
        long need = 0, have = 0;
        foreach (var v in VendorBuys)
        {
            if (Vulcan.PriceOf(v.ItemId) is not { Group: "GrandCompanySeals" } price) continue;
            need += (long)price.Cost * Math.Max(0, v.Target - BagCount(v.ItemId));
            have = Vulcan.CurrencyHave(price);
        }
        return (have, need);
    }

    internal void RunCommand(string command) => Run(command);

    private void ResetBatches()
    {
        currentBatch = null;
        remainingLines = new();
        anyBatchFailed = false;
        BatchNo = 0;
    }

    /// <summary>Takes the next batch that fits the free bag slots; false (and Failed) when there is no room at all.</summary>
    private bool StartNextBatch()
    {
        var free = Space.FreeSlots();
        var budget = Config.SplitBySpace ? free - Config.SpaceReserve : int.MaxValue;
        if (budget < 3)
        {
            SetStatus(Phase.Failed, $"В сумке свободно {free} ячеек (запас {Config.SpaceReserve}) — не во что крафтить. Освободи место и запусти снова (докрафт продолжит).");
            Config.ResumeFinals = true;
            SaveConfig();
            ResetBatches();
            return false;
        }
        currentBatch = Space.NextBatch(this, remainingLines, lines => Fits(lines, budget));
        BatchNo++;
        craftBaseline = new();
        Recalculate();
        Print($"Партия {BatchNo}: {string.Join(", ", currentBatch.Select(c => $"{Planner.RecipeName(c.RecipeId)} ×{c.Crafts}"))}"
              + $" (~{Space.Estimate(this, currentBatch)} ячеек из {free} свободных, осталось строк: {remainingLines.Count}).");
        return true;
    }

    // ---------------------------------------------------------------- currency caps

    internal const int ScripCap = 4000;

    /// <summary>
    /// How many scrips of a kind a batch may plan for: the cap minus one turn-in of the best collectable, because the
    /// exchange refuses a turn-in that would overflow (3600 + 600 > 4000), so the last steps below the cap are unreachable.
    /// </summary>
    internal int ScripLimit(ScripKind kind) => Math.Max(500, ScripCap - Scrips.MaxReward(kind, Config.ScripAllowFish));

    /// <summary>Grand Company seal cap for the character's rank (GrandCompanyRank.MaxSeals), 90 000 when unknown.</summary>
    internal static unsafe int GcSealCap()
    {
        try
        {
            var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (ps == null || ps->GrandCompany == 0) return 90000;
            var rank = ps->GetGrandCompanyRank();
            return Data.GetExcelSheet<GrandCompanyRank>().TryGetRow(rank, out var r) && r.MaxSeals > 0 ? (int)r.MaxSeals : 90000;
        }
        catch
        {
            return 90000;
        }
    }

    /// <summary>What these lines cost in capped currencies: gemstones, GC seals, scrips per kind.</summary>
    internal (long Gems, long Seals, Dictionary<ScripKind, long> Scrips) CurrencyNeeds(IReadOnlyList<CartLine> lines)
    {
        var plan = Planner.Build(lines, BagCount, id => RouteOf(id) == Route.Bicolor);
        long seals = 0;
        var scrips = new Dictionary<ScripKind, long>();
        foreach (var (id, qty) in plan.Leaves)
        {
            var route = RouteOf(id, qty);
            if (route is not (Route.Vendor or Route.GcFarm or Route.ScripFarm) || Vulcan.PriceOf(id) is not { } price) continue;
            var cost = (long)price.Cost * qty;
            if (price.Group == "GrandCompanySeals") seals += cost;
            else if (price.Group == "Scrips" && ScripFarm.KindOfItem(price.CurrencyId) is { } kind) scrips[kind] = scrips.GetValueOrDefault(kind) + cost;
        }
        // Master books the run buys: their price counts towards the caps too.
        foreach (var b in BookNeeds(lines).Where(b => BagCount(b.ItemId) == 0))
        {
            if (Vulcan.PriceOf(b.ItemId) is not { } price) continue;
            if (price.Group == "Scrips" && ScripFarm.KindOfItem(price.CurrencyId) is { } kind) scrips[kind] = scrips.GetValueOrDefault(kind) + price.Cost;
            else if (price.Group == "GrandCompanySeals") seals += price.Cost;
        }
        return (plan.Gems + Math.Max(0, Config.Reserve), seals, scrips);
    }

    /// <summary>Lines fit one batch: bag slots within the budget and every capped currency within its cap.</summary>
    private bool Fits(IReadOnlyList<CartLine> lines, int slotBudget)
    {
        if (slotBudget != int.MaxValue && Space.Estimate(this, lines) > slotBudget) return false;
        var (gems, seals, scrips) = CurrencyNeeds(lines);
        return gems <= Planner.GemCap && seals <= GcSealCap() && scrips.All(kv => kv.Value <= ScripLimit(kv.Key));
    }

    /// <summary>Splits the run into batches when the cart does not fit the bags or a currency cap; false if it cannot start.</summary>
    private bool DecideBatches()
    {
        var full = FullEffectiveCart().ToList();
        if (full.Count == 0) return true;
        var free = Space.FreeSlots();
        var slots = Space.Estimate(this, full);
        var (gems, seals, scrips) = CurrencyNeeds(full);
        var reasons = new List<string>();
        if (Config.SplitBySpace && slots > free - Config.SpaceReserve) reasons.Add($"в сумке свободно {free} ячеек, а нужно ~{slots}");
        if (gems > Planner.GemCap) reasons.Add($"биколоров нужно {gems}, лимит {Planner.GemCap}");
        var cap = GcSealCap();
        if (seals > cap) reasons.Add($"печатей GC нужно {seals}, лимит {cap}");
        foreach (var (k, v) in scrips.Where(kv => kv.Value > ScripLimit(kv.Key)))
            reasons.Add($"{ScripFarm.Info(k).Name.ToLowerInvariant()} скриптов нужно {v}, а за раз реально набрать {ScripLimit(k)} (лимит {ScripCap} минус одна сдача)");
        if (reasons.Count == 0) return true;
        remainingLines = full;
        Print($"Делю корзину на партии ({string.Join("; ", reasons)}): каждая вещь целиком со всеми полуфабрикатами, валюты фармятся под партию.");
        return StartNextBatch();
    }

    /// <summary>Bag space for the Farm tab: free slots and what the whole cart is expected to take.</summary>
    internal (int Free, int Need) SpaceInfo() => (Space.FreeSlots(), Space.Estimate(this, FullEffectiveCart()));

    /// <summary>Scrips the cart's scrip-farm materials cost, per kind (total needed, not the shortfall).</summary>
    internal List<(ScripKind Kind, int Need)> ScripNeeds()
    {
        var needs = new Dictionary<ScripKind, long>();
        foreach (var v in VendorBuys)
        {
            var m = Materials.FirstOrDefault(x => x.ItemId == v.ItemId);
            // Plain vendor routes count too: "auto" picks Vendor when the scrips cover that one item, but three master
            // books at 1200 each with 1356 in hand still need a farm first (only one got bought).
            if (m?.Route is not (Route.ScripFarm or Route.Vendor) || Vulcan.PriceOf(v.ItemId) is not { } price || ScripFarm.KindOfItem(price.CurrencyId) is not { } kind) continue;
            needs[kind] = needs.GetValueOrDefault(kind) + (long)price.Cost * Math.Max(0, v.Target - BagCount(v.ItemId));
        }
        return needs.Select(kv => (kv.Key, (int)Math.Min(int.MaxValue, kv.Value))).ToList();
    }

    /// <summary>Scrips tab: farm one kind of scrips up to a target, on its own.</summary>
    internal void StartScripFarm(ScripKind kind, int target)
    {
        if (Running) { Print("уже работает."); return; }
        if (!IsLoaded(GatherBuddy)) { SetStatus(Phase.Failed, "GatherBuddy Reborn не загружен."); return; }
        var err = Scrips.Begin(kind, target, Config.ScripItem.GetValueOrDefault(kind), Config.ScripAllowFish);
        if (err != null) { SetStatus(Phase.Failed, err); return; }
        scripStandalone = true;
        BeginRun();
        SetStatus(Phase.ScripFarming, $"Фармлю скрипы ({ScripFarm.Info(kind).Name}): {ScripFarm.Have(kind)}/{target} на {Scrips.SetName}.");
    }

    internal int CurrentJobLevel() => Jobs.Get(Jobs.CurrentJob()) is { } j ? Jobs.Level(j) : 0;

    internal void StartLeveler()
    {
        if (Running) { Print("сейчас идёт крафт-запуск — останови его сначала."); return; }
        Leveler.Start();
    }

    /// <summary>The leveler's end, kept in the run history like the craft runs.</summary>
    internal void LevelerFinished(string why)
    {
        Config.History.Insert(0, new RunRecord
        {
            Started = DateTime.Now, Ended = DateTime.Now, Result = why.StartsWith("Готово") ? "done" : why.StartsWith("Остановлено") ? "stopped" : "failed",
            Cart = new List<string> { "Автоуровень (beta)" }, Summary = why,
            Events = Leveler.Log.AsEnumerable().Reverse().Select(l => new RunEvent { Time = DateTime.Now, Phase = "автоуровень", Text = l }).ToList(),
        });
        if (Config.History.Count > 30) Config.History.RemoveRange(30, Config.History.Count - 30);
        SaveConfig();
    }

    internal static bool IsIdleNow() => IsIdle();

    /// <summary>Equips the first enabled job from the priority list (max level first for dungeons).</summary>
    internal void EquipFarmJob(bool forDungeons)
    {
        if (!Config.SwitchJob) return;
        var pick = forDungeons ? Jobs.Pick(Config.Jobs, 100) ?? Jobs.Pick(Config.Jobs, 1) : null;
        if (pick is { } j && j.Job.Id != Jobs.CurrentJob() && Jobs.Equip(j.Gearset))
            Print($"переключаюсь на {j.Job.Abbr} {j.Level} (гирсет {j.Gearset + 1}).");
    }

    private static unsafe bool IsIdle()
    {
        if (Condition[ConditionFlag.InCombat] || Condition[ConditionFlag.BetweenAreas] || Condition[ConditionFlag.BetweenAreas51]
            || Condition[ConditionFlag.Casting] || Condition[ConditionFlag.Occupied] || Condition[ConditionFlag.OccupiedInQuestEvent]
            || Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.BoundByDuty] || Condition[ConditionFlag.Unconscious])
            return false;
        var fm = FateManager.Instance();
        return fm == null || fm->CurrentFate == null;
    }

    // ---------------------------------------------------------------- helpers

    internal static int GemCount() => CurrencyCount(Planner.BicolorGemstone);

    /// <summary>Count of a currency item (gemstones, scrips…): bags plus the currency container.</summary>
    internal static unsafe int CurrencyCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var count = im->GetInventoryItemCount(itemId);
        var c = im->GetInventoryContainer(InventoryType.Currency);
        if (c != null)
        {
            var sum = 0;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId) sum += slot->Quantity;
            }
            count = Math.Max(count, sum);
        }
        return count;
    }

    internal static unsafe int BagCount(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        return im->GetInventoryItemCount(itemId) + im->GetInventoryItemCount(itemId, true);
    }

    internal static bool IsLoaded(string internalName) =>
        Pi.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);

    private bool Run(string command)
    {
        var ok = Commands.ProcessCommand(command);
        Log.Information($"{command} -> {(ok ? "ok" : "not handled")}");
        return ok;
    }

    private void SetStatus(Phase phase, string text)
    {
        Phase = phase;
        Status = text;
        Print(text);
        LogRun(phase, text);
    }

    // ---------------------------------------------------------------- history

    private const int HistoryLimit = 30;
    private RunRecord? currentRun;

    private void BeginRun()
    {
        currentRun = new RunRecord
        {
            Started = DateTime.Now,
            Cart = EffectiveCart().Select(c => $"{Planner.RecipeName(c.RecipeId)} ×{c.Crafts}").ToList(),
        };
        Config.History.Insert(0, currentRun);
        if (Config.History.Count > HistoryLimit) Config.History.RemoveRange(HistoryLimit, Config.History.Count - HistoryLimit);
        SaveConfig();
    }

    /// <summary>Every status change of a run goes into its log; a final phase closes the record.</summary>
    private void LogRun(Phase phase, string text)
    {
        if (currentRun == null) return;
        currentRun.Events.Add(new RunEvent { Time = DateTime.Now, Phase = PhaseName(phase), Text = text });
        if (phase is Phase.Done or Phase.Failed or Phase.Idle)
        {
            currentRun.Ended = DateTime.Now;
            currentRun.Result = phase switch { Phase.Done => "done", Phase.Failed => "failed", _ => "stopped" };
            currentRun.Summary = text;
            currentRun = null;
        }
        SaveConfig();
    }

    internal static string PhaseName(Phase p) => p switch
    {
        Phase.Preparing => "подготовка",
        Phase.Farming => "фарм FATE",
        Phase.Stopping => "остановка AFG",
        Phase.GcFarming => "печати GC",
        Phase.ScripFarming => "скрипы",
        Phase.Buying => "покупки",
        Phase.AfgBuying => "биколоры (AFG)",
        Phase.MobFarming => "фарм мобов",
        Phase.Crafting => "крафт",
        Phase.Done => "готово",
        Phase.Failed => "ошибка",
        _ => "ожидание",
    };

    internal void ClearHistory()
    {
        Config.History.RemoveAll(r => r != currentRun);
        SaveConfig();
    }

    // ---------------------------------------------------------------- saved carts

    internal string SaveCart(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "Дай корзине имя.";
        if (Config.Cart.Count == 0) return "Корзина пуста — нечего сохранять.";
        var saved = new SavedCart
        {
            Name = name, Saved = DateTime.Now,
            Cart = Config.Cart.Select(c => new CartEntry { RecipeId = c.RecipeId, Crafts = c.Crafts }).ToList(),
            TeamcraftListUrl = Config.TeamcraftListUrl,
            ImportManual = new List<string>(Config.ImportManual),
        };
        var i = Config.SavedCarts.FindIndex(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) Config.SavedCarts[i] = saved; else Config.SavedCarts.Insert(0, saved);
        SaveConfig();
        return i >= 0 ? $"Корзина «{name}» перезаписана." : $"Корзина «{name}» сохранена.";
    }

    internal string LoadCart(SavedCart saved, bool replace)
    {
        if (Running) return "Сначала останови запуск.";
        if (replace)
        {
            Config.Cart.Clear();
            Config.ImportManual.Clear();
            Config.TeamcraftListUrl = saved.TeamcraftListUrl;
            Config.ResumeFinals = false;
        }
        foreach (var c in saved.Cart) AddToCart(c.RecipeId, c.Crafts, save: false);
        foreach (var m in saved.ImportManual) if (!Config.ImportManual.Contains(m)) Config.ImportManual.Add(m);
        SaveConfig();
        Recalculate();
        SyncVulcanList();
        return replace ? $"Загружена корзина «{saved.Name}»." : $"Корзина «{saved.Name}» добавлена к текущей.";
    }

    internal void DeleteCart(SavedCart saved)
    {
        Config.SavedCarts.Remove(saved);
        SaveConfig();
    }

    private static void Print(string text) => Chat.Print($"[CraftMeThis] {text}");
}
