using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BicolorBridge;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Warn = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Good = new(0.5f, 0.9f, 0.5f, 1f);

    private readonly Plugin p;
    private string importText = string.Empty;
    private bool importReplace = true;
    private bool sendReplace = true;
    private string cartMessage = string.Empty;

    public MainWindow(Plugin plugin) : base("Bicolor Bridge###BicolorBridgeMain")
    {
        p = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(460, 300), MaximumSize = new Vector2(1600, 1600) };
        Size = new Vector2(600, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => p.Recalculate();

    public override void Draw()
    {
        if (ImGui.BeginTabBar("tabs"))
        {
            if (ImGui.BeginTabItem("Фарм")) { DrawFarm(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Корзина")) { DrawCart(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Настройки")) { DrawSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ---------------------------------------------------------------- farm

    private void DrawFarm()
    {
        var running = p.Phase is Phase.Farming or Phase.Stopping;
        if (running)
        {
            if (ImGui.Button("Остановить")) p.Stop();
        }
        else
        {
            if (ImGui.Button("Нафармить и запустить корзину")) p.Start();
            ImGui.SameLine();
            if (ImGui.Button("Пересчитать")) p.Recalculate();
        }
        ImGui.TextWrapped(p.Status);
        ImGui.Separator();

        foreach (var w in p.Warnings)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Warn);
            ImGui.TextWrapped(w);
            ImGui.PopStyleColor();
        }

        ImGui.TextWrapped($"Корзина LazyCrafter: {(p.Plan.Recipes.Count == 0 ? "—" : string.Join(", ", p.Plan.Recipes))}");
        ImGui.Text($"Биколоры: есть {(running ? p.Gems : Plugin.GemCount())}, нужно {p.Plan.Gems}" +
                   (p.Config.Reserve > 0 ? $" + запас {p.Config.Reserve}" : ""));
        DrawGemTable("gems", p.Plan);
    }

    private static void DrawGemTable(string id, GemPlan plan)
    {
        if (plan.Purchases.Count == 0 || !ImGui.BeginTable(id, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;
        ImGui.TableSetupColumn("Материал за биколоры", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Кол-во", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Цена", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("Биколоры", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableHeadersRow();
        foreach (var x in plan.Purchases)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.Text(x.Name);
            ImGui.TableNextColumn(); ImGui.Text(x.Units.ToString());
            ImGui.TableNextColumn(); ImGui.Text(x.ReceivePerTrade > 1 ? $"{x.CostPerTrade}/{x.ReceivePerTrade}шт" : x.CostPerTrade.ToString());
            ImGui.TableNextColumn(); ImGui.Text(x.Gems.ToString());
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- cart

    private void DrawCart()
    {
        ImGui.TextWrapped("Вставь ссылку импорта Teamcraft (Export → Copy import link), текст списка (Copy as text) или строки вида «5x Boiled Alpaca Steak».");
        ImGui.InputTextMultiline("##import", ref importText, 20000, new Vector2(-1, 90));
        if (ImGui.Button("Вставить из буфера")) importText = ImGui.GetClipboardText() ?? string.Empty;
        ImGui.SameLine();
        if (ImGui.Button("Импорт"))
        {
            cartMessage = p.Import(importText, importReplace);
            if (cartMessage.StartsWith("Импортировано")) importText = string.Empty;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Заменить корзину", ref importReplace);

        if (cartMessage.Length > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, cartMessage.StartsWith("Импортировано") || cartMessage.StartsWith("Корзина LazyCrafter") || cartMessage.StartsWith("Добавлено") ? Good : Warn);
            ImGui.TextWrapped(cartMessage);
            ImGui.PopStyleColor();
        }
        ImGui.Separator();

        var cart = p.Config.Cart;
        if (cart.Count == 0)
        {
            ImGui.TextDisabled("Корзина пуста.");
            return;
        }

        if (ImGui.Button("Отправить в LazyCrafter")) cartMessage = p.SendToLazyCrafter(sendReplace);
        ImGui.SameLine();
        ImGui.Checkbox("вместо его корзины", ref sendReplace);
        ImGui.SameLine();
        if (ImGui.Button("Очистить")) { cart.Clear(); p.SaveConfig(); }

        if (ImGui.BeginTable("cart", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Рецепт", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Крафтов", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableHeadersRow();
            CartEntry? remove = null;
            foreach (var e in cart)
            {
                ImGui.PushID((int)e.RecipeId);
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(p.Planner.RecipeName(e.RecipeId));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var crafts = e.Crafts;
                if (ImGui.InputInt("##n", ref crafts) && crafts > 0) { e.Crafts = crafts; p.SaveConfig(); }
                ImGui.TableNextColumn();
                if (ImGui.SmallButton("x")) remove = e;
                ImGui.PopID();
            }
            ImGui.EndTable();
            if (remove != null) { cart.Remove(remove); p.SaveConfig(); }
        }

        var plan = p.OwnCartPlan();
        ImGui.Text($"Этой корзине нужно биколоров: {plan.Gems} (есть {Plugin.GemCount()})");
        DrawGemTable("owngems", plan);
    }

    // ---------------------------------------------------------------- settings

    private void DrawSettings()
    {
        var c = p.Config;
        var reserve = c.Reserve;
        if (ImGui.InputInt("Запас сверху", ref reserve)) { c.Reserve = System.Math.Max(0, reserve); p.SaveConfig(); }
        var dispatch = c.DispatchAfterFarming;
        if (ImGui.Checkbox("Запускать корзину после фарма", ref dispatch)) { c.DispatchAfterFarming = dispatch; p.SaveConfig(); }
        var idle = c.IdleSeconds;
        if (ImGui.InputInt("Секунд простоя перед запуском", ref idle)) { c.IdleSeconds = System.Math.Clamp(idle, 2, 60); p.SaveConfig(); }
        var timeout = c.SoftStopTimeoutMinutes;
        if (ImGui.InputInt("Ждать мягкую остановку, мин", ref timeout)) { c.SoftStopTimeoutMinutes = System.Math.Clamp(timeout, 1, 30); p.SaveConfig(); }
        ImGui.Separator();
        ImGui.TextDisabled("Зоны для фарма берутся из настроек Auto FATE Grind.");
        ImGui.TextDisabled("В LazyCrafter должно быть включено «Prefer a currency shop».");
    }
}
