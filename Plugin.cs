using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using Lumina.Excel.Sheets;

namespace BicolorBridge;

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
}

public enum Phase { Idle, Farming, Stopping, Done, Failed }

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

    private const string Cmd = "/bbridge";
    private const string LazyCrafter = "LazyCrafter";
    private const string AutoFateGrind = "AutoFateGrind";

    internal readonly Configuration Config;
    internal readonly Planner Planner;
    private readonly WindowSystem windows = new("BicolorBridge");
    private readonly MainWindow window;

    internal GemPlan Plan { get; private set; } = GemPlan.Empty;
    internal Phase Phase { get; private set; } = Phase.Idle;
    internal string Status { get; private set; } = "Ожидание.";
    internal int Target { get; private set; }
    internal int Gems { get; private set; }
    internal List<string> Warnings { get; } = new();

    private DateTime nextTick = DateTime.MinValue;
    private DateTime stopSentAt;
    private DateTime idleSince = DateTime.MinValue;
    private bool forcedStop;

    public Plugin()
    {
        Config = Pi.GetPluginConfig() as Configuration ?? new Configuration();
        Planner = new Planner(Data.GetExcelSheet<Recipe>(), Data.GetExcelSheet<SpecialShop>(), Data.GetExcelSheet<Item>());

        window = new MainWindow(this);
        windows.AddWindow(window);
        Pi.UiBuilder.Draw += windows.Draw;
        Pi.UiBuilder.OpenMainUi += OpenWindow;
        Pi.UiBuilder.OpenConfigUi += OpenWindow;

        Commands.AddHandler(Cmd, new CommandInfo(OnCommand)
        {
            HelpMessage = "окно; /bbridge plan — посчитать биколоры по корзине LazyCrafter; /bbridge start — нафармить и запустить корзину; /bbridge stop — остановить",
        });
        Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        Commands.RemoveHandler(Cmd);
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
                      string.Join(", ", Plan.Purchases.Select(p => $"{p.Name} x{p.Units} ({p.Gems})")));
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

    // ---------------------------------------------------------------- run

    internal void Start()
    {
        if (Phase is Phase.Farming or Phase.Stopping)
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
        if (!Run("/afg start"))
        {
            SetStatus(Phase.Failed, "Команда /afg start не сработала.");
            return;
        }
        forcedStop = false;
        SetStatus(Phase.Farming, $"Фармлю FATE: {Gems}/{Target} биколоров.");
    }

    internal void Stop()
    {
        if (Phase is Phase.Farming or Phase.Stopping) Run("/afg stop");
        SetStatus(Phase.Idle, "Остановлено вручную.");
    }

    private void OnUpdate(IFramework _)
    {
        if (Phase is not (Phase.Farming or Phase.Stopping)) return;
        var now = DateTime.UtcNow;
        if (now < nextTick) return;
        nextTick = now.AddSeconds(1);
        if (!ClientState.IsLoggedIn) return;

        Gems = GemCount();
        if (Phase == Phase.Farming)
        {
            Status = $"Фармлю FATE: {Gems}/{Target} биколоров.";
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

    private static void Print(string text) => Chat.Print($"[Bicolor Bridge] {text}");
}
