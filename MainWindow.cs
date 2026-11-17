using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;

namespace CraftMeThis;

public sealed class MainWindow : Window
{
    private const float RowIcon = 26f;

    private readonly Plugin p;
    private string importText = string.Empty;
    private bool importReplace = true;

    public MainWindow(Plugin plugin) : base("CraftMeThis###CraftMeThisMain")
    {
        p = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 420), MaximumSize = new Vector2(1600, 1600) };
        Size = new Vector2(660, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => p.Recalculate();
    public override void PreDraw() => Ui.Push();
    public override void PostDraw() => Ui.Pop();

    /// <summary>Height kept free at the bottom for the "will not do" panel (0 when there is nothing to warn about).</summary>
    private float footer;

    public override void Draw()
    {
        var manual = p.ManualTasks();
        footer = manual.Count == 0 ? 0 : Math.Min(150f, 44f + manual.Count * ImGui.GetTextLineHeightWithSpacing() * 1.6f);

        DrawHeader();
        ImGui.Spacing();
        if (ImGui.BeginTabBar("tabs"))
        {
            if (ImGui.BeginTabItem("  Фарм  ")) { Scroll("farm", DrawFarm); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem($"  Корзина ({p.Config.Cart.Count})  ")) { Scroll("cart", DrawCart); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Классы  ")) { DrawJobs(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Настройки  ")) { Scroll("settings", DrawSettings); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
        if (manual.Count > 0) DrawManual(manual);
    }

    /// <summary>Bottom panel: what the plugin will not do by itself for the current cart.</summary>
    private void DrawManual(System.Collections.Generic.List<string> manual)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Ui.Warn with { W = 0.08f });
        ImGui.PushStyleColor(ImGuiCol.Border, Ui.Warn with { W = 0.45f });
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 8));
        ImGui.BeginChild("manual", new Vector2(-1, -1), true, ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warn);
        ImGui.SameLine();
        ImGui.TextColored(Ui.Warn, "Это CraftMeThis сам не сделает:");
        foreach (var m in manual)
        {
            ImGui.TextColored(Ui.Warn, "•");
            ImGui.SameLine();
            ImGui.TextWrapped(m);
        }
        ImGui.EndChild();
        ImGui.PopStyleColor(2);
    }

    private void Scroll(string id, Action body)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.BeginChild(id, new Vector2(-1, footer > 0 ? -footer : -1), false, ImGuiWindowFlags.None);
        ImGui.PopStyleColor();
        ImGui.Spacing();
        body();
        ImGui.EndChild();
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader()
    {
        Ui.Icon(FontAwesomeIcon.Hammer, Ui.Accent);
        ImGui.SameLine();
        ImGui.TextColored(Ui.Text, "CraftMeThis");
        ImGui.SameLine();
        var (label, color) = p.Phase switch
        {
            Phase.Preparing => ("подготовка", Ui.Warn),
            Phase.Farming => ("фарм FATE", Ui.Gem),
            Phase.Stopping => ("завершаю FATE", Ui.Warn),
            Phase.Buying => ("покупки", Ui.Accent),
            Phase.Crafting => ("крафт", Ui.Accent),
            Phase.Done => ("готово", Ui.Good),
            Phase.Failed => ("ошибка", Ui.Bad),
            _ => ("ожидание", Ui.Muted),
        };
        Ui.Pill(label, color);

        // Right-aligned shortcuts: the Teamcraft list and the other plugins' windows.
        const string tc = "Teamcraft", lc = "Vulcan", afg = "FATE Grind";
        var style = ImGui.GetStyle();
        var w = ImGui.CalcTextSize(tc).X + ImGui.CalcTextSize(lc).X + ImGui.CalcTextSize(afg).X
                + 3 * (style.FramePadding.X * 2 + 22) + 2 * style.ItemSpacing.X;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - w);
        var link = p.TeamcraftLink();
        ImGui.BeginDisabled(link == null);
        if (Ui.IconButton(FontAwesomeIcon.ExternalLinkAlt, tc,
                p.Config.TeamcraftListUrl.Length > 0 ? $"Открыть импортированный список: {p.Config.TeamcraftListUrl}"
                : link != null ? "Открыть корзину в Teamcraft (создаст там список)" : "Корзина пуста"))
            p.OpenTeamcraft();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Hammer, lc, "Открыть Vulcan (GatherBuddy, /vulcan)")) p.OpenVulcan();
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Crosshairs, afg, "Открыть окно Auto FATE Grind (/afg)")) p.OpenFateGrind();
    }

    // ---------------------------------------------------------------- farm

    private void DrawFarm()
    {
        var running = p.Running;

        foreach (var w in p.Warnings)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Ui.Warn);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Warn);
            ImGui.TextWrapped(w);
            ImGui.PopStyleColor();
        }
        if (p.Warnings.Count > 0) ImGui.Spacing();

        if (ImGui.BeginTable("farmcards", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            DrawGemCard(running);
            ImGui.TableNextColumn();
            DrawZoneCard(running);
            ImGui.EndTable();
        }

        if (p.Plan.Purchases.Count > 0 && Ui.BeginCard("materials"))
        {
            Ui.Caption("Материалы за биколоры");
            DrawGemTable("gems", p.Plan);
            Ui.EndCard();
        }

        var vendorOnly = p.VendorBuys.Where(v => !v.ForGemstones).ToList();
        if (vendorOnly.Count > 0 && Ui.BeginCard("vendors"))
        {
            Ui.Caption("Купит Vulcan у вендоров (гил, GC, скрипы…)");
            if (ImGui.BeginTable("vendorbuys", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn("Материал", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Кол-во", ImGuiTableColumnFlags.WidthFixed, 60);
                foreach (var v in vendorOnly)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Ui.GameIcon(p.Planner.ItemIcon(v.ItemId), RowIcon);
                    ImGui.SameLine();
                    Ui.CenteredText(v.Name, RowIcon);
                    ImGui.TableNextColumn(); Ui.CenteredText(v.Units.ToString(), RowIcon);
                }
                ImGui.EndTable();
            }
            Ui.EndCard();
        }

        if (Ui.BeginCard("cartsum"))
        {
            Ui.Caption("Корзина");
            ImGui.TextWrapped(p.Plan.Recipes.Count == 0 ? "пусто — импортируй список на вкладке «Корзина»" : string.Join(", ", p.Plan.Recipes));
            Ui.EndCard();
        }

        ImGui.Spacing();
        if (running)
        {
            if (Ui.Primary("Остановить", FontAwesomeIcon.Stop, Ui.Bad)) p.Stop();
        }
        else
        {
            var avail = ImGui.GetContentRegionAvail().X;
            var label = p.Plan.Gems > p.Gems ? "Нафармить, закупить и скрафтить" : p.VendorBuys.Count > 0 ? "Закупить и скрафтить" : "Скрафтить корзину";
            if (Ui.Primary(label, FontAwesomeIcon.Play, Ui.Accent, avail - 46)) p.Start();
            ImGui.SameLine();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 3);
            if (Ui.IconButton(FontAwesomeIcon.Sync, "", "Пересчитать")) p.Recalculate();
        }
        ImGui.TextColored(Ui.Muted, p.Status);
    }

    private void DrawGemCard(bool running)
    {
        if (!Ui.BeginCard("gemcard", 128)) { Ui.EndCard(); return; }
        Ui.Caption("Биколоры");
        Ui.GameIcon(p.Planner.ItemIcon(Planner.BicolorGemstone), 36);
        ImGui.SameLine();
        ImGui.BeginGroup();
        var have = running ? p.Gems : Plugin.GemCount();
        ImGui.TextColored(Ui.Gem, $"{have}");
        ImGui.SameLine(0, 4);
        ImGui.TextColored(Ui.Muted, $"/ {Planner.GemCap}");
        ImGui.TextColored(Ui.Muted, p.Plan.Gems > 0
            ? $"нужно {p.Plan.Gems}{(p.Config.Reserve > 0 ? $" + {p.Config.Reserve}" : "")}"
            : "корзине не нужны");
        ImGui.EndGroup();
        Ui.GemBar(have, p.Target);
        Ui.EndCard();
    }

    private void DrawZoneCard(bool running)
    {
        if (!Ui.BeginCard("zonecard", 128)) { Ui.EndCard(); return; }
        var c = p.Config;
        var zone = p.Zone;
        Ui.Caption("Зона фарма");
        Ui.Icon(FontAwesomeIcon.MapMarkerAlt, Ui.Accent);
        ImGui.SameLine();
        var preview = zone?.Zone.Name ?? "—";
        if (c.ZoneOverride == 0) preview += "  (авто)";
        ImGui.SetNextItemWidth(-1);
        if (running)
        {
            ImGui.TextUnformatted(preview);
        }
        else if (ImGui.BeginCombo("##zone", preview))
        {
            if (ImGui.Selectable("Авто — по материалам корзины", c.ZoneOverride == 0)) { c.ZoneOverride = 0; p.SaveConfig(); p.Recalculate(); }
            ImGui.Separator();
            foreach (var z in p.Zones.All)
            {
                if (ImGui.Selectable($"{z.Name}##{z.Territory}", c.ZoneOverride == z.Territory)) { c.ZoneOverride = z.Territory; p.SaveConfig(); p.Recalculate(); }
                ImGui.SameLine(200);
                ImGui.TextColored(Ui.Muted, $"ур. {z.MinLevel}+  ·  {z.Trader}");
            }
            ImGui.EndCombo();
        }

        if (zone != null)
        {
            var sold = zone.Sold.Count > 0 ? $"{zone.Zone.Trader}: " + string.Join(", ", zone.Sold.Select(s => $"{s.Item} (ранг {s.Rank})")) : zone.Reason;
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
            ImGui.TextWrapped(sold);
            ImGui.PopStyleColor();

            var pick = c.SwitchJob ? p.Jobs.Pick(c.Jobs, zone.Zone.MinLevel) : null;
            if (pick is { } j)
            {
                Ui.GameIcon(j.Job.Icon, 20);
                ImGui.SameLine();
                Ui.CenteredText($"{j.Job.Abbr} {j.Level}  ·  гирсет {j.Gearset + 1}", 20);
            }
            else
            {
                ImGui.TextColored(Ui.Muted, c.SwitchJob ? "класс: подходящего нет, останется текущий" : "класс: текущий");
            }
        }
        Ui.EndCard();
    }

    private void DrawGemTable(string id, GemPlan plan)
    {
        if (plan.Purchases.Count == 0 || !ImGui.BeginTable(id, 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX)) return;
        ImGui.TableSetupColumn("Материал", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Кол-во", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Цена", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Итого", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableHeadersRow();
        foreach (var x in plan.Purchases)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Ui.GameIcon(p.Planner.ItemIcon(x.ItemId), RowIcon);
            ImGui.SameLine();
            Ui.CenteredText(x.Name, RowIcon);
            ImGui.TableNextColumn(); Ui.CenteredText(x.Units.ToString(), RowIcon);
            ImGui.TableNextColumn(); Ui.CenteredText(x.ReceivePerTrade > 1 ? $"{x.CostPerTrade}/{x.ReceivePerTrade}" : x.CostPerTrade.ToString(), RowIcon, Ui.Muted);
            ImGui.TableNextColumn(); Ui.CenteredText(x.Gems.ToString(), RowIcon, Ui.Gem);
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- cart

    private void DrawCart()
    {
        if (Ui.BeginCard("import"))
        {
            Ui.Caption("Импорт из Teamcraft");
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
            ImGui.TextWrapped("Ссылка на список (ffxivteamcraft.com/list/…), ссылка импорта, текст списка (Copy as text) или строки «5x Boiled Alpaca Steak».");
            ImGui.PopStyleColor();
            ImGui.InputTextMultiline("##import", ref importText, 20000, new Vector2(-1, 70));
            if (Ui.IconButton(FontAwesomeIcon.Paste, "Из буфера")) importText = ImGui.GetClipboardText() ?? string.Empty;
            ImGui.SameLine();
            ImGui.BeginDisabled(p.ImportBusy || importText.Trim().Length == 0);
            if (Ui.IconButton(p.ImportBusy ? FontAwesomeIcon.Hourglass : FontAwesomeIcon.FileImport, p.ImportBusy ? "Загружаю…" : "Импорт")
                && p.Import(importText, importReplace) && p.CartMessage.StartsWith("Импортировано"))
                importText = string.Empty;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.Checkbox("заменить корзину", ref importReplace);

            var msg = p.CartMessage;
            if (msg.Length > 0)
            {
                var ok = msg.Contains("Импортировано") || msg.StartsWith("Загружаю") || msg.StartsWith("Корзина LazyCrafter") || msg.StartsWith("Добавлено");
                ImGui.PushStyleColor(ImGuiCol.Text, ok ? Ui.Good : Ui.Warn);
                ImGui.TextWrapped(msg);
                ImGui.PopStyleColor();
            }
            if (p.Config.TeamcraftListUrl.Length > 0 && !p.ImportBusy)
            {
                if (Ui.IconButton(FontAwesomeIcon.ExternalLinkAlt, "Открыть список в Teamcraft")) p.OpenTeamcraft();
                ImGui.SameLine();
                ImGui.TextColored(Ui.Muted, p.Config.TeamcraftListUrl);
            }
            Ui.EndCard();
        }

        var cart = p.Config.Cart;
        if (!Ui.BeginCard("cartlist")) { Ui.EndCard(); return; }
        Ui.Caption($"Корзина · {cart.Count} рецептов · {cart.Sum(e => e.Crafts)} крафтов");
        if (cart.Count == 0)
        {
            ImGui.TextColored(Ui.Muted, "Пусто — импортируй список выше.");
            Ui.EndCard();
            return;
        }

        if (Ui.IconButton(FontAwesomeIcon.Trash, "Очистить")) p.ClearCart();

        if (ImGui.BeginTable("cart", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
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
                ImGui.TableNextColumn();
                Ui.GameIcon(p.Planner.ItemIcon(p.Planner.RecipeItem(e.RecipeId)), RowIcon);
                ImGui.SameLine();
                Ui.CenteredText(p.Planner.RecipeName(e.RecipeId), RowIcon);
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var crafts = e.Crafts;
                if (ImGui.InputInt("##n", ref crafts) && crafts > 0) { e.Crafts = crafts; p.SaveConfig(); p.Recalculate(); }
                ImGui.TableNextColumn();
                if (Ui.IconButton(FontAwesomeIcon.Times, "", "Убрать")) remove = e;
                ImGui.PopID();
            }
            ImGui.EndTable();
            if (remove != null) { cart.Remove(remove); p.SaveConfig(); p.Recalculate(); }
        }
        Ui.EndCard();
    }

    // ---------------------------------------------------------------- jobs

    private void DrawJobs()
    {
        var c = p.Config;
        ImGui.Spacing();
        var sw = c.SwitchJob;
        if (ImGui.Checkbox("Переключать класс перед фармом", ref sw)) { c.SwitchJob = sw; p.SaveConfig(); }
        ImGui.TextColored(Ui.Muted, "Берётся первый включённый класс сверху, у которого есть гирсет и хватает уровня для зоны.");
        var minLevel = p.Zone?.Zone.MinLevel ?? 0;
        var chosen = c.SwitchJob && minLevel > 0 ? p.Jobs.Pick(c.Jobs, minLevel)?.Job.Id : null;

        if (!ImGui.BeginTable("jobs", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX, new Vector2(-1, footer > 0 ? -footer : -1))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 58);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn("Класс", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Ур.", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Гирсет", ImGuiTableColumnFlags.WidthFixed, 56);
        ImGui.TableHeadersRow();

        int? moveFrom = null, moveTo = null;
        for (var i = 0; i < c.Jobs.Count; i++)
        {
            var pref = c.Jobs[i];
            var job = p.Jobs.Get(pref.JobId);
            if (job == null) continue;
            ImGui.PushID((int)pref.JobId);
            ImGui.TableNextRow();
            if (chosen == job.Id) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(Ui.Accent with { W = 0.14f }));
            ImGui.TableNextColumn();
            if (ImGui.ArrowButton("up", ImGuiDir.Up) && i > 0) { moveFrom = i; moveTo = i - 1; }
            ImGui.SameLine(0, 2);
            if (ImGui.ArrowButton("dn", ImGuiDir.Down) && i < c.Jobs.Count - 1) { moveFrom = i; moveTo = i + 1; }
            ImGui.TableNextColumn();
            var en = pref.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { pref.Enabled = en; p.SaveConfig(); }
            ImGui.TableNextColumn();
            Ui.GameIcon(job.Icon, 22);
            ImGui.SameLine();
            Ui.CenteredText($"{i + 1}.  {job.Abbr}  ·  {job.Name}", 22, pref.Enabled ? Ui.Text : Ui.Muted);
            if (chosen == job.Id) { ImGui.SameLine(); Ui.CenteredText("← будет", 22, Ui.Accent); }
            ImGui.TableNextColumn();
            var lvl = Jobs.Level(job);
            Ui.CenteredText(lvl.ToString(), 22, minLevel > 0 && lvl < minLevel ? Ui.Muted : Ui.Good);
            ImGui.TableNextColumn();
            var gs = Jobs.Gearset(job.Id);
            Ui.CenteredText(gs >= 0 ? (gs + 1).ToString() : "нет", 22, gs >= 0 ? Ui.Text : Ui.Muted);
            ImGui.PopID();
        }
        ImGui.EndTable();

        if (moveFrom is { } f && moveTo is { } t)
        {
            (c.Jobs[f], c.Jobs[t]) = (c.Jobs[t], c.Jobs[f]);
            p.SaveConfig();
        }
    }

    // ---------------------------------------------------------------- settings

    private void DrawSettings()
    {
        var c = p.Config;
        if (Ui.BeginCard("set-farm"))
        {
            Ui.Caption("Фарм");
            ImGui.SetNextItemWidth(120);
            var reserve = c.Reserve;
            if (ImGui.InputInt("Запас биколоров сверху", ref reserve)) { c.Reserve = Math.Max(0, reserve); p.SaveConfig(); }
            var setZone = c.SetAfgZone;
            if (ImGui.Checkbox("Ставить зону фарма в Auto FATE Grind", ref setZone)) { c.SetAfgZone = setZone; p.SaveConfig(); }
            ImGui.TextColored(Ui.Muted, "Твои зоны и очередь классов AFG возвращаются после фарма.");
            Ui.EndCard();
        }
        if (Ui.BeginCard("set-vulcan"))
        {
            Ui.Caption("Vulcan (GatherBuddy)");
            var buy = c.BuyFromVendors;
            if (ImGui.Checkbox("Закупать у вендоров (гил, GC, биколоры…)", ref buy)) { c.BuyFromVendors = buy; p.SaveConfig(); }
            var craft = c.CraftAfterBuying;
            if (ImGui.Checkbox("Крафтить после закупки (сбор недостающего + крафт)", ref craft)) { c.CraftAfterBuying = craft; p.SaveConfig(); }
            var ret = c.UseRetainers;
            if (ImGui.Checkbox("Брать материалы у ретейнеров (нужен Allagan Tools)", ref ret)) { c.UseRetainers = ret; p.SaveConfig(); }
            var toggle = c.ToggleArtisan;
            if (ImGui.Checkbox("Выключать Artisan на время крафта", ref toggle)) { c.ToggleArtisan = toggle; p.SaveConfig(); }
            ImGui.BeginDisabled(!c.ToggleArtisan);
            var back = c.ReenableArtisan;
            if (ImGui.Checkbox("…и включать обратно после", ref back)) { c.ReenableArtisan = back; p.SaveConfig(); }
            ImGui.EndDisabled();
            ImGui.TextColored(Ui.Muted, "Vulcan не крафтит, пока загружен Artisan.");
            Ui.EndCard();
        }
        if (Ui.BeginCard("set-handoff"))
        {
            Ui.Caption("Остановка фарма");
            var idle = c.IdleSeconds;
            if (ImGui.InputInt("Секунд простоя перед запуском", ref idle)) { c.IdleSeconds = Math.Clamp(idle, 2, 60); p.SaveConfig(); }
            ImGui.SetNextItemWidth(120);
            var timeout = c.SoftStopTimeoutMinutes;
            if (ImGui.InputInt("Ждать мягкую остановку AFG, мин", ref timeout)) { c.SoftStopTimeoutMinutes = Math.Clamp(timeout, 1, 30); p.SaveConfig(); }
            Ui.EndCard();
        }
        if (Ui.BeginCard("set-req"))
        {
            Ui.Caption("Кто что делает");
            Ui.Icon(FontAwesomeIcon.Crosshairs, Ui.Accent); ImGui.SameLine();
            ImGui.TextWrapped("Auto FATE Grind только фармит биколоры: на время запуска CraftMeThis ставит ему зону, выключает фильтр уровней и его собственные покупки, потом возвращает твои настройки. Покупает у Gemstone Trader уже Vulcan, вместе с остальными вендорами.");
            Ui.EndCard();
        }
    }
}
