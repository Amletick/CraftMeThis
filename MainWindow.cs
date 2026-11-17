using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CraftMeThis;

public sealed class MainWindow : Window
{
    private static readonly Vector4 Warn = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Good = new(0.5f, 0.9f, 0.5f, 1f);

    private readonly Plugin p;
    private string importText = string.Empty;
    private bool importReplace = true;
    private bool sendReplace = true;
    private string cartMessage = string.Empty;

    public MainWindow(Plugin plugin) : base("CraftMeThis###CraftMeThisMain")
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
            if (ImGui.BeginTabItem("Классы")) { DrawJobs(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Настройки")) { DrawSettings(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    // ---------------------------------------------------------------- farm

    private void DrawFarm()
    {
        var running = p.Running;
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
        ImGui.Separator();
        DrawZone(running);
    }

    private void DrawZone(bool running)
    {
        var c = p.Config;
        var current = p.Zone;
        ImGui.Text("Зона фарма:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        var preview = c.ZoneOverride == 0 ? $"авто{(current != null ? $" — {current.Zone.Name}" : "")}" : current?.Zone.Name ?? "?";
        if (!running && ImGui.BeginCombo("##zone", preview))
        {
            if (ImGui.Selectable("авто (по материалам корзины)", c.ZoneOverride == 0)) { c.ZoneOverride = 0; p.SaveConfig(); p.Recalculate(); }
            foreach (var z in p.Zones.All)
            {
                var label = $"{z.Name}  (ур. {z.MinLevel}+, {z.Trader})";
                if (ImGui.Selectable(label, c.ZoneOverride == z.Territory)) { c.ZoneOverride = z.Territory; p.SaveConfig(); p.Recalculate(); }
            }
            ImGui.EndCombo();
        }
        else if (running)
        {
            ImGui.Text(preview);
        }
        if (current != null)
        {
            ImGui.TextDisabled($"{current.Reason}" +
                               (current.Sold.Count > 0 ? ": " + string.Join(", ", current.Sold.Select(s => $"{s.Item} (ранг {s.Rank})")) : ""));
            var pick = c.SwitchJob ? p.Jobs.Pick(c.Jobs, current.Zone.MinLevel) : null;
            ImGui.TextDisabled(c.SwitchJob
                ? pick is { } j ? $"Класс: {j.Job.Abbr} {j.Level} (гирсет {j.Gearset + 1})" : "Класс: подходящего нет, останется текущий"
                : "Класс: текущий (смена выключена)");
        }
    }

    // ---------------------------------------------------------------- jobs

    private void DrawJobs()
    {
        var c = p.Config;
        var sw = c.SwitchJob;
        if (ImGui.Checkbox("Переключать класс перед фармом", ref sw)) { c.SwitchJob = sw; p.SaveConfig(); }
        ImGui.TextDisabled("Берётся первый включённый класс сверху, у которого есть гирсет и хватает уровня для зоны.");
        var minLevel = p.Zone?.Zone.MinLevel ?? 0;

        if (!ImGui.BeginTable("jobs", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(-1, -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableSetupColumn("Вкл", ImGuiTableColumnFlags.WidthFixed, 34);
        ImGui.TableSetupColumn("Класс", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Ур.", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Гирсет", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableHeadersRow();

        int? moveFrom = null, moveTo = null;
        for (var i = 0; i < c.Jobs.Count; i++)
        {
            var pref = c.Jobs[i];
            var job = p.Jobs.Get(pref.JobId);
            if (job == null) continue;
            ImGui.PushID((int)pref.JobId);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0) { moveFrom = i; moveTo = i - 1; }
            ImGui.SameLine(0, 2);
            if (ImGui.ArrowButton("dn", ImGuiDir.Down) && i < c.Jobs.Count - 1) { moveFrom = i; moveTo = i + 1; }
            ImGui.TableNextColumn();
            var en = pref.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { pref.Enabled = en; p.SaveConfig(); }
            ImGui.TableNextColumn();
            ImGui.Text($"{i + 1}. {job.Abbr}  {job.Name}");
            ImGui.TableNextColumn();
            var lvl = Jobs.Level(job);
            if (minLevel > 0 && lvl < minLevel) ImGui.TextDisabled(lvl.ToString()); else ImGui.Text(lvl.ToString());
            ImGui.TableNextColumn();
            var gs = Jobs.Gearset(job.Id);
            if (gs >= 0) ImGui.Text((gs + 1).ToString()); else ImGui.TextDisabled("нет");
            ImGui.PopID();
        }
        ImGui.EndTable();

        if (moveFrom is { } f && moveTo is { } t)
        {
            (c.Jobs[f], c.Jobs[t]) = (c.Jobs[t], c.Jobs[f]);
            p.SaveConfig();
        }
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
        var setZone = c.SetAfgZone;
        if (ImGui.Checkbox("Ставить зону фарма в Auto FATE Grind", ref setZone)) { c.SetAfgZone = setZone; p.SaveConfig(); }
        ImGui.TextDisabled("Твои зоны и очередь классов AFG возвращаются после фарма.");
        ImGui.Separator();
        ImGui.TextDisabled("Если зону не ставить, фарм идёт по зонам из настроек Auto FATE Grind.");
        ImGui.TextDisabled("В LazyCrafter должно быть включено «Prefer a currency shop».");
    }
}
