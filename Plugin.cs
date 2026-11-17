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
    /// <summary>How each raw material is obtained, chosen on the Materials tab (missing = automatic).</summary>
    public Dictionary<uint, Route> Routes { get; set; } = new();
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
}

/// <summary>A raw material of the cart with its possible and chosen route.</summary>
public sealed record Material(uint ItemId, string Name, int Have, int Missing, IReadOnlyList<Route> Options, Route Route, bool Chosen);

[Serializable]
public sealed class CartEntry
{
    public uint RecipeId { get; set; }
    public int Crafts { get; set; }
}

public enum Phase { Idle, Preparing, Farming, Stopping, Buying, AfgBuying, Crafting, Done, Failed }

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

    internal bool Running => Phase is Phase.Preparing or Phase.Farming or Phase.Stopping or Phase.Buying or Phase.AfgBuying or Phase.Crafting;
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

    internal void Recalculate()
    {
        Warnings.Clear();
        Gems = GemCount();
        Plan = Planner.Build(CartLines(), BagCount, id => RouteOf(id) == Route.Bicolor);
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
        foreach (var m in Materials.Where(m => m.Route == Route.Vendor))
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
        options.Add(Route.Market);
        return options;
    }

    /// <summary>The chosen route if it is still possible, otherwise the first of gather → gemstones → vendor → by hand.</summary>
    internal Route RouteOf(uint itemId)
    {
        var options = RouteOptions(itemId);
        return Config.Routes.TryGetValue(itemId, out var r) && options.Contains(r) ? r : options[0];
    }

    private Material MaterialOf(uint itemId, int missing) =>
        new(itemId, Planner.ItemName(itemId), BagCount(itemId), missing, RouteOptions(itemId), RouteOf(itemId), Config.Routes.ContainsKey(itemId));

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
        if (Vulcan.SyncList(CartLines(), Config.UseRetainers, out var err) == null) { Print(err!); return; }
        Run($"/vulcan {VulcanLink.ListName}");
        materialsUntil = DateTime.UtcNow.AddSeconds(5);
    }

    /// <summary>Keeps Vulcan's "CraftMeThis" list in step with the cart (quietly; nothing to do without GatherBuddy).</summary>
    private void SyncVulcanList()
    {
        if (Running || Config.Cart.Count == 0 || !IsLoaded(GatherBuddy)) return;
        if (Vulcan.SyncList(CartLines(), Config.UseRetainers, out var err) == null) Log.Warning(err ?? "Vulcan sync failed");
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
        if (Phase == Phase.Crafting) Vulcan.StopCrafting();
        afg.Restore();
        RestoreArtisan();
        SetStatus(Phase.Idle, "Остановлено вручную.");
    }

    private void EnterBuying()
    {
        Recalculate();
        if (!Config.BuyFromVendors || VendorBuys.Count == 0)
        {
            EnterCrafting();
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
            EnterCrafting();
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
            case Phase.Buying: TickBuying(now); break;
            case Phase.AfgBuying: TickAfgBuying(now); break;
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
        if (afterStop == Phase.Crafting) EnterCrafting();
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
                    EnterCrafting();
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
            var err = Vulcan.StartCrafting(CartLines(), Config.UseRetainers);
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
            foreach (var c in Config.Cart)
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
        Recalculate();
        if (failed.Count == 0)
            SetStatus(Phase.Done, $"Готово: {string.Join(", ", done)}.");
        else
            SetStatus(Phase.Done, $"Vulcan закончил. Скрафчено: {(done.Count > 0 ? string.Join(", ", done) : "ничего")}. Не скрафчено: {string.Join("; ", failed)}.");
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
