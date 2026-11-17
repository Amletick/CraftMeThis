using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    public int Version { get; set; } = 1;
    /// <summary>Extra gemstones to farm on top of what the cart costs.</summary>
    public int Reserve { get; set; }
    /// <summary>Start the LazyCrafter cart once enough gemstones are farmed.</summary>
    public bool DispatchAfterFarming { get; set; } = true;
    /// <summary>Seconds the character must stay idle after "/afg stop soft" before LazyCrafter is started.</summary>
    public int IdleSeconds { get; set; } = 6;
    /// <summary>Minutes to wait for the soft stop before forcing "/afg stop".</summary>
    public int SoftStopTimeoutMinutes { get; set; } = 8;
    /// <summary>The plugin's own cart (imported from Teamcraft or edited here), sent to LazyCrafter on demand.</summary>
    public List<CartEntry> Cart { get; set; } = new();
    /// <summary>Farm zone (territory id) chosen by hand; 0 = pick automatically from the cart.</summary>
    public uint ZoneOverride { get; set; }
    /// <summary>Set the farm zone in Auto FATE Grind before the run (its own zones are put back afterwards).</summary>
    public bool SetAfgZone { get; set; } = true;
    /// <summary>Switch to the first suitable job from <see cref="Jobs"/> before farming.</summary>
    public bool SwitchJob { get; set; } = true;
    /// <summary>Job priority, top first.</summary>
    public List<JobPref> Jobs { get; set; } = new();
}

[Serializable]
public sealed class CartEntry
{
    public uint RecipeId { get; set; }
    public int Crafts { get; set; }
}

public enum Phase { Idle, Preparing, Farming, Stopping, Done, Failed }

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
    private const string LazyCrafter = "LazyCrafter";
    private const string AutoFateGrind = "AutoFateGrind";

    internal readonly Configuration Config;
    internal readonly Planner Planner;
    internal readonly Zones Zones;
    internal readonly Jobs Jobs;
    private readonly AfgLink afg = new();
    private readonly WindowSystem windows = new("CraftMeThis");
    private readonly MainWindow window;

    internal GemPlan Plan { get; private set; } = GemPlan.Empty;
    internal Phase Phase { get; private set; } = Phase.Idle;
    internal string Status { get; private set; } = "Ожидание.";
    internal int Target { get; private set; }
    internal int Gems { get; private set; }
    internal List<string> Warnings { get; } = new();
    internal ZoneChoice? Zone { get; private set; }

    internal bool Running => Phase is Phase.Preparing or Phase.Farming or Phase.Stopping;
    private DateTime startAt;

    private DateTime nextTick = DateTime.MinValue;
    private DateTime stopSentAt;
    private DateTime idleSince = DateTime.MinValue;
    private bool forcedStop;

    public Plugin()
    {
        Config = Pi.GetPluginConfig() as Configuration ?? new Configuration();
        Planner = new Planner(Data.GetExcelSheet<Recipe>(), Data.GetExcelSheet<SpecialShop>(), Data.GetExcelSheet<Item>(),
            new[] { Data.GetExcelSheet<Item>(ClientLanguage.English) });
        Zones = new Zones(Data.GetExcelSheet<FateShop>(), Data.GetExcelSheet<SpecialShop>(), Data.GetExcelSheet<Level>(), Data.GetExcelSheet<ENpcResident>());
        Jobs = new Jobs(Data.GetExcelSheet<ClassJob>());
        Jobs.Normalize(Config.Jobs);

        window = new MainWindow(this);
        windows.AddWindow(window);
        Pi.UiBuilder.Draw += windows.Draw;
        Pi.UiBuilder.OpenMainUi += OpenWindow;
        Pi.UiBuilder.OpenConfigUi += OpenWindow;

        Commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "окно; /craftme plan — посчитать биколоры по корзине LazyCrafter; /craftme start — нафармить и запустить корзину; /craftme stop — остановить",
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
                Print(Plan.Purchases.Count == 0
                    ? "в корзине LazyCrafter нет материалов за биколоры."
                    : $"корзине нужно {Plan.Gems} биколоров, у тебя {Gems}: " +
                      string.Join(", ", Plan.Purchases.Select(p => $"{p.Name} x{p.Units} ({p.Gems})")) +
                      (Zone != null ? $". Зона фарма: {Zone.Zone.Name}." : ""));
                break;
            default: window.IsOpen = !window.IsOpen; break;
        }
    }

    // ---------------------------------------------------------------- plan

    internal void Recalculate()
    {
        Warnings.Clear();
        Gems = GemCount();
        var cart = ReadCart(out var cartError);
        if (cartError != null) Warnings.Add(cartError);
        Plan = Planner.Build(cart, BagCount);
        Target = (int)Math.Min(Planner.GemCap, Plan.Gems + Math.Max(0, Config.Reserve));
        if (Plan.Gems + Config.Reserve > Planner.GemCap)
            Warnings.Add($"Корзине нужно {Plan.Gems} биколоров, а больше {Planner.GemCap} держать нельзя. Уменьши корзину или запусти её в несколько заходов.");

        if (!IsLoaded(LazyCrafter)) Warnings.Add("LazyCrafter не загружен.");
        if (!IsLoaded(AutoFateGrind)) Warnings.Add("Auto FATE Grind не загружен.");
        CheckAfgConfig();

        Zone = Config.ZoneOverride != 0 && Zones.Get(Config.ZoneOverride) is { } manual
            ? new ZoneChoice(manual, "выбрана вручную", Array.Empty<(string, int)>())
            : Zones.Choose(Plan);
        if (Zone?.Sold.Any(s => s.Rank > 1) == true)
            Warnings.Add($"Часть материалов у торговца {Zone.Zone.Trader} открывается только с ранга {Zone.Sold.Max(s => s.Rank)} общих FATE в {Zone.Zone.Name}.");
        if (Config.SwitchJob && Zone != null && Plan.Purchases.Count > 0 && Jobs.Pick(Config.Jobs, Zone.Zone.MinLevel) == null)
            Warnings.Add($"Нет включённого класса уровня {Zone.Zone.MinLevel}+ с гирсетом — фарм пойдёт текущим классом.");
    }

    private List<CartLine> ReadCart(out string? error)
    {
        error = null;
        var path = Path.Combine(Pi.ConfigDirectory.Parent!.FullName, LazyCrafter + ".json");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var lines = new List<CartLine>();
            if (doc.RootElement.TryGetProperty("Cart", out var cart) && cart.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in cart.EnumerateArray())
                {
                    if (e.TryGetProperty("RecipeId", out var r) && e.TryGetProperty("Crafts", out var c))
                        lines.Add(new CartLine(r.GetUInt32(), c.GetInt32()));
                }
            }
            if (lines.Count == 0) error = "Корзина LazyCrafter пуста. Добавь рецепт в Catalog → Add to cart.";
            return lines;
        }
        catch (Exception ex)
        {
            error = $"Не удалось прочитать корзину LazyCrafter ({path}): {ex.Message}";
            return new List<CartLine>();
        }
    }

    private void CheckAfgConfig()
    {
        var path = Path.Combine(Pi.ConfigDirectory.Parent!.FullName, AutoFateGrind + ".json");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            var tradeList = root.TryGetProperty("TradeList", out var tl) && tl.ValueKind == JsonValueKind.Array && tl.GetArrayLength() > 0;
            var tradeOnCap = root.TryGetProperty("TradeOnCap", out var toc) && toc.ValueKind == JsonValueKind.True;
            var threshold = root.TryGetProperty("TradeThreshold", out var th) && th.TryGetInt32(out var t) ? t : int.MaxValue;
            if (tradeList && (tradeOnCap || threshold <= Target))
                Warnings.Add($"В Auto FATE Grind заполнен список покупок (порог {threshold}). Он может потратить биколоры раньше, чем их заберёт LazyCrafter. Очисти Settings → Gemstones → Shopping list.");
        }
        catch
        {
            // AFG not installed or the file is busy: not fatal, the loaded check reports a missing plugin.
        }
    }

    // ---------------------------------------------------------------- own cart

    /// <summary>Last import/send result shown on the Cart tab; set from the framework thread.</summary>
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
                        CartMessage = (name.Length > 0 ? $"«{name}»: " : "") + ApplyImport(res, replace);
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
        if (replace) Config.Cart.Clear();
        foreach (var l in res.Lines) AddToCart(l.RecipeId, l.Crafts, save: false);
        SaveConfig();
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

    /// <summary>Plan for the plugin's own cart, without touching LazyCrafter.</summary>
    internal GemPlan OwnCartPlan() => Planner.Build(Config.Cart.Select(c => new CartLine(c.RecipeId, c.Crafts)), BagCount);

    internal string SendToLazyCrafter(bool replace)
    {
        if (Config.Cart.Count == 0) return "Корзина пуста.";
        if (Running) return "Сначала останови фарм.";
        var err = LazyCrafterLink.SetCart(Config.Cart.Select(c => new CartLine(c.RecipeId, c.Crafts)).ToList(), replace);
        if (err != null) return err;
        Recalculate();
        return replace ? "Корзина LazyCrafter заменена." : "Добавлено в корзину LazyCrafter.";
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
        if (Plan.Purchases.Count == 0)
        {
            SetStatus(Phase.Idle, "В корзине нет материалов за биколоры. Запускаю LazyCrafter как есть.");
            Dispatch();
            return;
        }
        if (!IsLoaded(LazyCrafter))
        {
            SetStatus(Phase.Failed, "LazyCrafter не загружен.");
            return;
        }
        if (Gems >= Target)
        {
            SetStatus(Phase.Done, $"Биколоров хватает ({Gems}/{Target}). Запускаю корзину.");
            Dispatch();
            return;
        }
        if (!IsLoaded(AutoFateGrind))
        {
            SetStatus(Phase.Failed, $"Нужно ещё {Target - Gems} биколоров, но Auto FATE Grind не загружен.");
            return;
        }

        Run("/lcraft stop");

        if (Config.SetAfgZone && Zone != null)
        {
            var err = afg.Apply(Zone.Zone.Territory);
            if (err != null) Print($"{err} Фарм пойдёт по зонам, выбранным в Auto FATE Grind.");
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
        SetStatus(Phase.Preparing, Zone != null
            ? $"Готовлюсь: зона {Zone.Zone.Name}, нужно {Target} биколоров (есть {Gems})."
            : $"Готовлюсь: нужно {Target} биколоров (есть {Gems}).");
    }

    internal void Stop()
    {
        if (Running) Run("/afg stop");
        afg.Restore();
        SetStatus(Phase.Idle, "Остановлено вручную.");
    }

    private void OnUpdate(IFramework _)
    {
        if (!Running) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddSeconds(1);
        if (!ClientState.IsLoggedIn) return;

        if (Phase == Phase.Preparing)
        {
            if (now < startAt) return;
            if (!Run("/afg start"))
            {
                afg.Restore();
                SetStatus(Phase.Failed, "Команда /afg start не сработала.");
                return;
            }
            SetStatus(Phase.Farming, $"Фармлю FATE{(Zone != null ? " в " + Zone.Zone.Name : "")}: {Gems}/{Target} биколоров.");
            return;
        }

        Gems = GemCount();
        if (Phase == Phase.Farming)
        {
            Status = $"Фармлю FATE{(Zone != null ? " в " + Zone.Zone.Name : "")}: {Gems}/{Target} биколоров.";
            if (Gems < Target) return;
            Run("/afg stop soft");
            stopSentAt = now;
            idleSince = DateTime.MinValue;
            SetStatus(Phase.Stopping, $"Набрал {Gems}/{Target}. Жду, пока Auto FATE Grind закончит текущий FATE.");
            return;
        }

        // Stopping: wait until the character has been idle for a while.
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
        if (Config.DispatchAfterFarming)
        {
            SetStatus(Phase.Done, $"Биколоры есть ({Gems}). Запустил корзину LazyCrafter.");
            Dispatch();
        }
        else
        {
            SetStatus(Phase.Done, $"Биколоры есть ({Gems}). Корзину запусти сам.");
        }
    }

    internal void OpenLazyCrafter()
    {
        if (!IsLoaded(LazyCrafter)) Print("LazyCrafter не загружен.");
        else Run("/lcraft");
    }

    internal void OpenFateGrind()
    {
        if (!IsLoaded(AutoFateGrind)) Print("Auto FATE Grind не загружен.");
        else Run("/afg");
    }

    private void Dispatch()
    {
        if (!Run("/lcraft dispatch"))
            SetStatus(Phase.Failed, "Команда /lcraft dispatch не сработала.");
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

    private static unsafe int BagCount(uint itemId)
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
