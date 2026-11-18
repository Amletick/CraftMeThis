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
    /// <summary>Give Hunter at most this many minutes for the mob drops.</summary>
    public int MobFarmMaxMinutes { get; set; } = 60;
    /// <summary>During the mob farm, set RSR's Engage to "all targets in range" for the farm job, so it starts fights itself.</summary>
    public bool RsrEngageAll { get; set; } = true;
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
}

/// <summary>A raw material of the cart with its possible and chosen route.</summary>
/// <param name="Price">Vulcan's vendor offer ("7 Ixali Oaknot · Ixali vendor"), null when no vendor.</param>
/// <param name="Affordable">Whether the currency for <see cref="Missing"/> units is there (beast tribe, GC, scrips…).</param>
public sealed record Material(uint ItemId, string Name, int Have, int Missing, IReadOnlyList<Route> Options, Route Route, bool Chosen,
                              string? Price = null, bool Affordable = true, string? Shortage = null);

[Serializable]
public sealed class CartEntry
{
    public uint RecipeId { get; set; }
    public int Crafts { get; set; }
}

public enum Phase { Idle, Preparing, Farming, Stopping, GcFarming, Buying, AfgBuying, MobFarming, Crafting, Done, Failed }

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

    internal bool Running => Phase is Phase.Preparing or Phase.Farming or Phase.Stopping or Phase.GcFarming or Phase.Buying or Phase.AfgBuying or Phase.MobFarming or Phase.Crafting;
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

        Gc = new GcFarm(this);
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
    internal IReadOnlyList<CartLine> EffectiveCart()
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
            Warnings.Add($"Корзине нужно {Plan.Gems} биколоров, а больше {Planner.GemCap} держать нельзя. Уменьши корзину или запусти её в несколько заходов.");

        var gbr = IsLoaded(GatherBuddy);
        if (!gbr) Warnings.Add("GatherBuddy Reborn не загружен: покупки и крафт делает его Vulcan.");
        if (Plan.Gems > Gems && !IsLoaded(AutoFateGrind)) Warnings.Add("Auto FATE Grind не загружен: биколоры фармить некому.");

        // Routes: gemstone materials go to the gemstone plan above; the rest by the route chosen on the Materials tab.
        Materials = Plan.Purchases.Select(p => MaterialOf(p.ItemId, p.Units))
            .Concat(Plan.Leaves.Select(kv => MaterialOf(kv.Key, kv.Value)))
            .OrderBy(m => m.Route).ThenByDescending(m => m.Missing).ToList();
        VendorBuys = Plan.Purchases.Select(p => new VendorBuy(p.ItemId, p.Name, p.Units, BagCount(p.ItemId) + p.Units, true)).ToList();
        foreach (var m in Materials.Where(m => m.Route is Route.Vendor or Route.GcFarm))
            VendorBuys.Add(new VendorBuy(m.ItemId, m.Name, m.Missing, m.Have + m.Missing, false));

        Zone = Config.ZoneOverride != 0 && Zones.Get(Config.ZoneOverride) is { } manual
            ? new ZoneChoice(manual, "выбрана вручную", Array.Empty<(string, int)>())
            : Zones.Choose(Plan);
        if (Zone?.Sold.Any(s => s.Rank > 1) == true)
            Warnings.Add($"Часть материалов у торговца {Zone.Zone.Trader} открывается только с ранга {Zone.Sold.Max(s => s.Rank)} общих FATE в {Zone.Zone.Name}.");
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
        if (options.Contains(Route.Vendor) && IsLoaded(GatherBuddy) && Vulcan.PriceOf(itemId)?.Group == "GrandCompanySeals") options.Add(Route.GcFarm);
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
            if (o == Route.Vendor && missing > 0 && !CanAfford(itemId, missing, out _)) continue;
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
        if (options.Contains(Route.Vendor) && IsLoaded(GatherBuddy) && Vulcan.PriceOf(itemId) is { } price)
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
    internal List<string> ManualTasks()
    {
        var list = new List<string>(Config.ImportManual);
        var bought = VendorBuys.Select(v => v.ItemId).ToHashSet();

        var market = Materials.Where(m => m.Route == Route.Market && !bought.Contains(m.ItemId))
                              .Select(m => $"{m.Name} x{m.Missing}").ToList();
        if (market.Count > 0)
            list.Add($"Купить на рынке или добыть самому (путь «рынок / вручную» на вкладке «Материалы»): {string.Join(", ", market)}.");

        if (Zone?.Sold.Where(s => s.Rank > 1).ToList() is { Count: > 0 } ranked && Plan.Gems > 0)
            list.Add($"Открыть ранг {ranked.Max(s => s.Rank)} общих FATE в {Zone.Zone.Name}: без него торговец не продаст {string.Join(", ", ranked.Select(s => s.Item))}.");

        if (Plan.Gems + Config.Reserve > Planner.GemCap)
            list.Add($"Корзине нужно {Plan.Gems} биколоров, а больше {Planner.GemCap} не носить: разбей корзину на части.");

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
        Recalculate();
        Gc = new GcFarm(this);
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
        if (!Running) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddSeconds(1);
        if (!ClientState.IsLoggedIn) return;

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
            var err = Vulcan.StartCrafting(CartLines(), Config.UseRetainers, Config.ResumeFinals);
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
            return;
        }
        RestoreArtisan();
        ReportCrafts();
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

    internal static unsafe int GemCount()
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var count = im->GetInventoryItemCount(Planner.BicolorGemstone);
        var c = im->GetInventoryContainer(InventoryType.Currency);
        if (c != null)
        {
            var sum = 0;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == Planner.BicolorGemstone) sum += slot->Quantity;
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
    }

    private static void Print(string text) => Chat.Print($"[CraftMeThis] {text}");
}
