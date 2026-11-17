using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace BicolorBridge;

public sealed class MainWindow : Window
{
    private readonly Plugin p;

    public MainWindow(Plugin plugin) : base("Bicolor Bridge###BicolorBridgeMain")
    {
        p = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 260), MaximumSize = new Vector2(1600, 1600) };
        Size = new Vector2(560, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => p.Recalculate();

    public override void Draw()
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
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), w);

        ImGui.Text($"Корзина LazyCrafter: {(p.Plan.Recipes.Count == 0 ? "—" : string.Join(", ", p.Plan.Recipes))}");
        ImGui.Text($"Биколоры: есть {(running ? p.Gems : Plugin.GemCount())}, нужно {p.Plan.Gems}" +
                   (p.Config.Reserve > 0 ? $" + запас {p.Config.Reserve}" : ""));

        if (p.Plan.Purchases.Count > 0 && ImGui.BeginTable("gems", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            ImGui.TableSetupColumn("Материал", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Кол-во", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Цена", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Биколоры", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableHeadersRow();
            foreach (var x in p.Plan.Purchases)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.Text(x.Name);
                ImGui.TableNextColumn(); ImGui.Text(x.Units.ToString());
                ImGui.TableNextColumn(); ImGui.Text(x.ReceivePerTrade > 1 ? $"{x.CostPerTrade}/{x.ReceivePerTrade}шт" : x.CostPerTrade.ToString());
                ImGui.TableNextColumn(); ImGui.Text(x.Gems.ToString());
            }
            ImGui.EndTable();
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("Настройки"))
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
            ImGui.TextDisabled("Зоны для фарма берутся из настроек Auto FATE Grind.");
            ImGui.TextDisabled("В LazyCrafter должно быть включено «Prefer a currency shop».");
        }
    }
}
