using System;
using System.Collections.Generic;
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
            if (ImGui.BeginTabItem($"  Материалы ({p.Materials.Count})  ")) { Scroll("materials", DrawMaterials); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Дерево  ")) { DrawTree(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Скрипы  ")) { Scroll("scrips", DrawScrips); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Классы  ")) { DrawJobs(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Автоуровень β  ")) { Scroll("leveler", DrawLeveler); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("  Зависимости  ")) { Scroll("deps", DrawDeps); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem($"  История ({p.Config.History.Count})  ")) { Scroll("history", DrawHistory); ImGui.EndTabItem(); }
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
            Phase.GcFarming => ("фарм печатей GC", Ui.Warn),
            Phase.ScripFarming => ("фарм скриптов", Ui.Warn),
            Phase.MobFarming => ("фарм мобов", Ui.Bad),
            Phase.AfgBuying => ("покупка за биколоры", Ui.Gem),
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

        if (p.Config.Cart.Count > 0 && Ui.BeginCard("space"))
        {
            if (DateTime.UtcNow - spaceAt > TimeSpan.FromSeconds(3)) { space = p.SpaceInfo(); spaceAt = DateTime.UtcNow; }
            var fits = space.Need <= space.Free - p.Config.SpaceReserve;
            Ui.Icon(FontAwesomeIcon.Box, fits ? Ui.Good : Ui.Warn);
            ImGui.SameLine();
            ImGui.TextColored(fits ? Ui.Text : Ui.Warn, $"Сумка: свободно {space.Free}, корзине нужно ~{space.Need} ячеек (запас {p.Config.SpaceReserve}).");
            if (!fits)
                ImGui.TextColored(Ui.Muted, p.Config.SplitBySpace
                    ? "Не влезет целиком — будет крафтиться партиями: вещь со всеми полуфабрикатами, потом следующая."
                    : "Не влезет целиком, а деление на партии выключено в настройках.");
            if (p.Batching) ImGui.TextColored(Ui.Accent, $"Сейчас партия {p.BatchNo}, осталось строк: {p.BatchesLeft}.");
            Ui.EndCard();
        }

        if (p.Config.ResumeFinals && Ui.BeginCard("resume"))
        {
            Ui.Icon(FontAwesomeIcon.Redo, Ui.Warn);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Warn, "Докрафт: прошлый прогон не всё сделал, теперь считается только недостающее (Include Final Crafts).");
            ImGui.TextColored(Ui.Muted, "Строки, которых в сумке уже хватает, пропускаются — даже если их скрафтили раньше, не в этот раз.");
            if (Ui.IconButton(FontAwesomeIcon.Times, "Выключить докрафт")) p.SetResume(false);
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

    private string saveName = string.Empty;

    private void DrawSavedCarts()
    {
        Ui.BeginCard("saved");
        Ui.Caption($"Сохранённые корзины · {p.Config.SavedCarts.Count}");
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##savename", "имя корзины", ref saveName, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(p.Config.Cart.Count == 0 || saveName.Trim().Length == 0);
        if (Ui.IconButton(FontAwesomeIcon.Save, "Сохранить текущую")) p.CartMessage = p.SaveCart(saveName);
        ImGui.EndDisabled();

        SavedCart? delete = null;
        foreach (var s in p.Config.SavedCarts)
        {
            ImGui.PushID(s.Name);
            var crafts = s.Cart.Sum(c => c.Crafts);
            if (Ui.IconButton(FontAwesomeIcon.FolderOpen, "", "Загрузить вместо текущей")) p.CartMessage = p.LoadCart(s, true);
            ImGui.SameLine();
            if (Ui.IconButton(FontAwesomeIcon.Plus, "", "Добавить к текущей")) p.CartMessage = p.LoadCart(s, false);
            ImGui.SameLine();
            if (Ui.IconButton(FontAwesomeIcon.Trash, "", "Удалить")) delete = s;
            ImGui.SameLine();
            ImGui.TextUnformatted(s.Name);
            ImGui.SameLine();
            ImGui.TextColored(Ui.Muted, $"· {s.Cart.Count} рец., {crafts} крафт. · {s.Saved:dd.MM HH:mm}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", s.Cart.Select(c => $"{p.Planner.RecipeName(c.RecipeId)} ×{c.Crafts}")));
            ImGui.PopID();
        }
        if (delete != null) p.DeleteCart(delete);
        Ui.EndCard();
    }

    private void DrawCart()
    {
        DrawSavedCarts();
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

        if (Ui.IconButton(FontAwesomeIcon.Boxes, "Materials в Vulcan", "Открыть корзину в Vulcan (список «CraftMeThis») вместе с его окном Materials")) p.OpenVulcanMaterials();
        ImGui.SameLine();
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
                if (ImGui.InputInt("##n", ref crafts) && crafts > 0) { e.Crafts = crafts; p.CartEdited(); }
                ImGui.TableNextColumn();
                if (Ui.IconButton(FontAwesomeIcon.Times, "", "Убрать")) remove = e;
                ImGui.PopID();
            }
            ImGui.EndTable();
            if (remove != null) { cart.Remove(remove); p.CartEdited(); }
        }
        Ui.EndCard();
    }

    // ---------------------------------------------------------------- materials

    private static readonly (Route Route, string Title, FontAwesomeIcon Icon)[] RouteGroups =
    {
        (Route.Gather, "Сбор и рыбалка — Vulcan соберёт во время крафта", FontAwesomeIcon.Leaf),
        (Route.Bicolor, "Биколоры — нафармит AFG, купит Vulcan у Gemstone Trader", FontAwesomeIcon.Gem),
        (Route.Vendor, "Вендор — купит Vulcan (гил, GC, скрипы…)", FontAwesomeIcon.Store),
        (Route.GcFarm, "Печати GC — AutoDuty бегает данжи и сдаёт шмот в GC, потом купит Vulcan", FontAwesomeIcon.Dungeon),
        (Route.MobFarm, "Дроп с мобов — нафармит Hunter", FontAwesomeIcon.Skull),
        (Route.ScripFarm, "За скрипы — нафармит коллектаблами GatherBuddy, потом купит Vulcan", FontAwesomeIcon.Scroll),
        (Route.Tomes, "За томстоуны — купит Vulcan, если томов хватает (сами томы не фармятся)", FontAwesomeIcon.Book),
        (Route.Market, "Рынок / вручную / фарм мобов — сделать самому", FontAwesomeIcon.HandPaper),
    };

    private static Vector4 RouteColor(Route r) => r switch
    {
        Route.Gather => Ui.Good,
        Route.Bicolor => Ui.Gem,
        Route.Vendor => Ui.Accent,
        Route.GcFarm => Ui.Warn,
        Route.MobFarm => Ui.Bad,
        Route.ScripFarm => Ui.Warn,
        Route.Tomes => Ui.Gem,
        _ => Ui.Warn,
    };

    private void DrawMaterials()
    {
        if (p.Config.Cart.Count == 0)
        {
            ImGui.TextColored(Ui.Muted, "Корзина пуста — импортируй список на вкладке «Корзина».");
            return;
        }
        ImGui.TextColored(Ui.Muted, "Сырьё, которого не хватает на всю корзину (с учётом сумки). Путь можно сменить у каждого материала; «авто» — сбор → биколоры → вендор → вручную.");
        if (p.Materials.Count == 0)
        {
            ImGui.TextColored(Ui.Good, "Всё сырьё уже в сумке.");
            return;
        }
        if (Ui.IconButton(FontAwesomeIcon.Boxes, "Открыть Materials в Vulcan", "Окно материалов Vulcan для этой корзины (список «CraftMeThis»)")) p.OpenVulcanMaterials();
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Undo, "Сбросить выбор на авто")) { p.Config.Routes.Clear(); p.SaveConfig(); p.Recalculate(); }
        ImGui.SameLine();
        ImGui.TextColored(Ui.Gem, $"биколоров нужно: {p.Plan.Gems}");

        foreach (var (route, title, icon) in RouteGroups)
        {
            var rows = p.Materials.Where(m => m.Route == route).ToList();
            if (rows.Count == 0) continue;
            ImGui.Spacing();
            Ui.BeginCard($"mat-{route}");
            Ui.Icon(icon, RouteColor(route));
            ImGui.SameLine();
            ImGui.TextColored(RouteColor(route), $"{title}  ·  {rows.Count}");
            if (ImGui.BeginTable($"mat-t-{route}", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
            {
                ImGui.TableSetupColumn("Материал", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Цена у вендора", ImGuiTableColumnFlags.WidthFixed, 170);
                ImGui.TableSetupColumn("Есть", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Не хватает", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Путь", ImGuiTableColumnFlags.WidthFixed, 160);
                ImGui.TableHeadersRow();
                foreach (var m in rows)
                {
                    ImGui.PushID((int)m.ItemId);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Ui.GameIcon(p.Planner.ItemIcon(m.ItemId), RowIcon);
                    ImGui.SameLine();
                    Ui.CenteredText(m.Name, RowIcon);
                    if (m.Note != null)
                    {
                        ImGui.SameLine();
                        Ui.CenteredText("· " + m.Note, RowIcon, Ui.Warn);
                    }
                    ImGui.TableNextColumn();
                    if (m.Price != null)
                    {
                        Ui.CenteredText(m.Price, RowIcon, m.Affordable ? Ui.Muted : Ui.Bad);
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip(m.Affordable ? m.Price : $"Не хватает валюты — {m.Shortage}. «Авто» тогда не берёт вендора.");
                    }
                    ImGui.TableNextColumn(); Ui.CenteredText(m.Have.ToString(), RowIcon, m.Have > 0 ? Ui.Text : Ui.Muted);
                    ImGui.TableNextColumn(); Ui.CenteredText(m.Missing.ToString(), RowIcon);
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (RowIcon - ImGui.GetFrameHeight()) / 2);
                    ImGui.SetNextItemWidth(-1);
                    var preview = Plugin.RouteName(m.Route) + (m.Chosen ? "" : " (авто)");
                    if (ImGui.BeginCombo("##route", preview))
                    {
                        if (ImGui.Selectable("авто", !m.Chosen)) p.SetRoute(m.ItemId, null);
                        foreach (var o in m.Options)
                            if (ImGui.Selectable(Plugin.RouteName(o), m.Chosen && m.Route == o)) p.SetRoute(m.ItemId, o);
                        ImGui.EndCombo();
                    }
                    if (ImGui.IsItemHovered())
                    {
                        var mobs = m.Options.Contains(Route.MobFarm) ? p.Hunter.DroppedBy(p.EnglishName(m.ItemId)) : System.Array.Empty<string>();
                        if (mobs.Count > 0) ImGui.SetTooltip($"Падает с: {string.Join(", ", mobs.Take(6))}");
                        else if (m.Options.Count == 1) ImGui.SetTooltip("Других путей нет: ни сбора, ни Gemstone Trader, ни вендора, которого знает Vulcan, ни моба в базе Hunter.");
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
            }
            Ui.EndCard();
        }
    }

    // ---------------------------------------------------------------- craft tree

    private System.Collections.Generic.List<TreeNode> tree = new();
    private DateTime treeBuiltAt = DateTime.MinValue;
    private uint craftingRecipe;

    private static (FontAwesomeIcon Icon, Vector4 Color, string Label) StateLook(NodeState s) => s switch
    {
        NodeState.Done => (FontAwesomeIcon.CheckCircle, Ui.Good, "готово"),
        NodeState.Crafting => (FontAwesomeIcon.Cog, Ui.Accent, "крафтится"),
        NodeState.Ready => (FontAwesomeIcon.Hammer, Ui.Accent, "можно крафтить"),
        NodeState.Waiting => (FontAwesomeIcon.Hourglass, Ui.Muted, "ждёт материалы"),
        NodeState.Gather => (FontAwesomeIcon.Leaf, Ui.Good, "соберёт Vulcan"),
        NodeState.Bicolor => (FontAwesomeIcon.Gem, Ui.Gem, "купить за биколоры"),
        NodeState.BicolorFarm => (FontAwesomeIcon.Crosshairs, Ui.Gem, "нафармить и купить"),
        NodeState.Vendor => (FontAwesomeIcon.Store, Ui.Accent, "купит Vulcan"),
        NodeState.GcFarm => (FontAwesomeIcon.Dungeon, Ui.Warn, "печати GC: AutoDuty"),
        NodeState.MobFarm => (FontAwesomeIcon.Skull, Ui.Bad, "нафармит Hunter"),
        NodeState.ScripFarm => (FontAwesomeIcon.Scroll, Ui.Warn, "скрипы: коллектаблы"),
        NodeState.Tomes => (FontAwesomeIcon.Book, Ui.Gem, "за томстоуны"),
        NodeState.Manual => (FontAwesomeIcon.HandPaper, Ui.Warn, "вручную / рынок"),
        NodeState.Unlearned => (FontAwesomeIcon.BookOpen, Ui.Bad, "не изучено"),
        _ => (FontAwesomeIcon.TimesCircle, Ui.Bad, "не скрафчено"),
    };

    private NodeState State(TreeNode n) => CraftTree.StateOf(n, craftingRecipe, p.Crafted, p.FailedItems, p.Plan.Gems == 0 || p.Gems >= p.Target);

    private void DrawTree()
    {
        if (p.Config.Cart.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Ui.Muted, "Корзина пуста — импортируй список на вкладке «Корзина».");
            return;
        }
        if (DateTime.UtcNow - treeBuiltAt > TimeSpan.FromSeconds(1))
        {
            tree = CraftTree.Build(p);
            craftingRecipe = p.Phase == Phase.Crafting ? p.Vulcan.CurrentRecipeId : 0;
            treeBuiltAt = DateTime.UtcNow;
        }

        // Summary: how many lines are in each state.
        var all = new System.Collections.Generic.List<TreeNode>();
        void Walk(TreeNode n) { all.Add(n); foreach (var c in n.Children) Walk(c); }
        foreach (var r in tree) Walk(r);
        ImGui.Spacing();
        var first = true;
        foreach (var g in all.GroupBy(State).OrderBy(g => g.Key))
        {
            if (!first) ImGui.SameLine();
            first = false;
            var (_, color, label) = StateLook(g.Key);
            Ui.Pill($"{label}: {g.Count()}", color);
        }
        ImGui.SameLine();
        if (Ui.IconButton(FontAwesomeIcon.Boxes, "Materials в Vulcan")) p.OpenVulcanMaterials();

        const float activityHeight = 112f;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerH;
        if (!ImGui.BeginTable("tree", 3, flags, new Vector2(-1, -(footer + activityHeight)))) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Предмет", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Есть / нужно", ImGuiTableColumnFlags.WidthFixed, 96);
        ImGui.TableSetupColumn("Статус", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableHeadersRow();
        var i = 0;
        foreach (var root in tree) DrawNode(root, ref i);
        ImGui.EndTable();
        DrawActivity(activityHeight - ImGui.GetStyle().ItemSpacing.Y);
    }

    /// <summary>Live strip: pipeline steps, what is happening now (item by item) and what comes next.</summary>
    private void DrawActivity(float height)
    {
        Ui.BeginCard("activity", height);
        var steps = p.Steps();
        var first = true;
        foreach (var (name, state) in steps)
        {
            if (state < 0) continue;
            if (!first) { ImGui.SameLine(); ImGui.TextColored(Ui.Muted, "›"); ImGui.SameLine(); }
            first = false;
            var color = state switch { 1 => Ui.Accent, 2 => Ui.Good, _ => Ui.Muted };
            Ui.Pill(name, color);
        }
        var (now, next) = p.Activity();
        Ui.Icon(p.Running ? FontAwesomeIcon.Cog : FontAwesomeIcon.InfoCircle, p.Running ? Ui.Accent : Ui.Muted);
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, p.Running ? Ui.Text : Ui.Muted);
        ImGui.TextWrapped(now);
        ImGui.PopStyleColor();
        if (next.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
            ImGui.TextWrapped("Дальше: " + string.Join("  ›  ", next));
            ImGui.PopStyleColor();
        }
        Ui.EndCard();
    }

    private void DrawNode(TreeNode n, ref int index)
    {
        var state = State(n);
        var (icon, color, label) = StateLook(state);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.PushID(index++);

        var leaf = n.Children.Count == 0;
        var flags = ImGuiTreeNodeFlags.SpanFullWidth | ImGuiTreeNodeFlags.FramePadding;
        if (leaf) flags |= ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
        // Open what still needs work; finished branches start collapsed.
        if (state is not NodeState.Done) flags |= ImGuiTreeNodeFlags.DefaultOpen;

        var open = ImGui.TreeNodeEx("##n", flags);
        ImGui.SameLine();
        Ui.GameIcon(p.Planner.ItemIcon(n.ItemId), 22);
        ImGui.SameLine();
        var kind = n.IsCartLine ? "" : n.RecipeId != 0 ? "  · полуфабрикат" : "";
        Ui.CenteredText(n.Name + (n.IsCartLine ? $"  ×{n.Crafts} крафт." : ""), 22, n.IsCartLine ? Ui.Text : state == NodeState.Done ? Ui.Muted : Ui.Text);
        if (kind.Length > 0) { ImGui.SameLine(0, 0); Ui.CenteredText(kind, 22, Ui.Muted); }

        ImGui.TableNextColumn();
        var count = n.IsCartLine && p.Crafted(n) is { } got ? $"+{got} / {n.Need}" : $"{n.Have} / {n.Need}";
        Ui.CenteredText(count, 22, state == NodeState.Done ? Ui.Good : Ui.Text);

        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (22 - ImGui.GetTextLineHeight()) / 2);
        Ui.Icon(icon, color);
        ImGui.SameLine();
        ImGui.TextColored(color, label);

        if (state == NodeState.Unlearned && p.BookLine(n.RecipeId) is { Length: > 0 } book)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Indent(40);
            Ui.Icon(FontAwesomeIcon.Scroll, Ui.Warn);
            ImGui.SameLine();
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Ui.Warn, book);
            ImGui.PopTextWrapPos();
            ImGui.Unindent(40);
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
        }

        if (open && !leaf)
        {
            foreach (var c in n.Children) DrawNode(c, ref index);
            ImGui.TreePop();
        }
        ImGui.PopID();
    }

    // ---------------------------------------------------------------- dependencies

    private void DrawDeps()
    {
        ImGui.TextColored(Ui.Muted, "Плагины, с которыми работает CraftMeThis. Кнопки добавляют репозиторий в Dalamud и ставят плагин; значок (!) — что в нём настроить.");
        var d = p.Deps;
        if (d.Message.Length > 0)
        {
            ImGui.TextColored(d.Busy ? Ui.Accent : d.Message.StartsWith("Ошибка") || d.Message.StartsWith("Не ") ? Ui.Warn : Ui.Good, d.Message);
        }
        if (!ImGui.BeginTable("deps", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX | ImGuiTableFlags.BordersInnerH)) return;
        ImGui.TableSetupColumn("Плагин", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Статус", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 210);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableHeadersRow();
        foreach (var dep in Dependencies.All)
        {
            ImGui.PushID(dep.InternalName);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(dep.Name + (dep.Optional ? "  (по желанию)" : ""));
            ImGui.TextColored(Ui.Muted, dep.UsedFor);
            ImGui.TableNextColumn();
            var st = Dependencies.StatusOf(dep);
            var (label, color) = st switch
            {
                Dependencies.State.Loaded => ("работает", Ui.Good),
                Dependencies.State.Disabled => ("выключен", Ui.Warn),
                _ => ("не установлен", dep.Optional ? Ui.Muted : Ui.Bad),
            };
            Ui.Pill(label, color);
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(d.Busy);
            if (st == Dependencies.State.Missing)
            {
                if (dep.Repo != null && !Dependencies.HasRepo(dep.Repo))
                {
                    if (Ui.IconButton(FontAwesomeIcon.Link, "Репо", $"Добавить репозиторий в Dalamud:\n{dep.Repo}")) d.AddRepo(dep);
                    ImGui.SameLine();
                }
                if (Ui.IconButton(FontAwesomeIcon.Download, "Установить", dep.Repo == null ? "Из основного репозитория Dalamud" : $"Из {dep.Repo}")) d.Install(dep);
            }
            else if (st == Dependencies.State.Disabled)
            {
                ImGui.TextColored(Ui.Muted, "включи в /xlplugins");
            }
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            Ui.Icon(FontAwesomeIcon.ExclamationCircle, Ui.Warn);
            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
                ImGui.TextUnformatted(dep.Name);
                foreach (var n in dep.Notes) ImGui.TextUnformatted("• " + n);
                if (dep.Repo != null) ImGui.TextColored(Ui.Muted, dep.Repo);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    // ---------------------------------------------------------------- auto-leveling

    private void DrawLeveler()
    {
        var lv = p.Leveler;
        var c = p.Config;
        Ui.BeginCard("lvl-warn");
        Ui.Icon(FontAwesomeIcon.Flask, Ui.Warn);
        ImGui.SameLine();
        ImGui.TextColored(Ui.Warn, "Beta.");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
        ImGui.TextWrapped("Качает текущую боевую профу: 1–14 через FATE (Auto FATE Grind), дальше данжи AutoDuty по одному; на уровнях класс/джоб-квестов отдаёт управление Questionable. Джоб-квесты иногда остаются недоделанными — тогда доделай квест руками и перезапусти.");
        ImGui.PopStyleColor();
        Ui.EndCard();

        if (lv.Running)
        {
            if (Ui.Primary("Остановить автоуровень", FontAwesomeIcon.Stop, Ui.Bad)) lv.Stop();
        }
        else
        {
            ImGui.BeginDisabled(p.Running);
            if (Ui.Primary("Качать текущую профу", FontAwesomeIcon.ArrowUp, Ui.Accent)) p.StartLeveler();
            ImGui.EndDisabled();
        }
        ImGui.TextColored(lv.Running ? Ui.Text : Ui.Muted, lv.Now);

        Ui.BeginCard("lvl-set");
        Ui.Caption("Настройки");
        ImGui.SetNextItemWidth(120);
        var target = c.LevelTarget;
        if (ImGui.InputInt("До уровня", ref target)) { c.LevelTarget = Math.Clamp(target, 2, 100); p.SaveConfig(); }
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Режим с 71 (Trust)", c.LevelTrustMode))
        {
            foreach (var mode in new[] { "Trust_Group", "Trust_Solo", "Support" })
                if (ImGui.Selectable(mode, c.LevelTrustMode == mode)) { c.LevelTrustMode = mode; p.SaveConfig(); }
            ImGui.EndCombo();
        }
        void Int(string label, int value, Action<int> set, int min, int max)
        {
            ImGui.SetNextItemWidth(120);
            var v = value;
            if (ImGui.InputInt(label, ref v)) { set(Math.Clamp(v, min, max)); p.SaveConfig(); }
        }
        Int("Сдавать в GC, если свободных слотов меньше (0 = никогда)", c.LevelGcFreeSlots, v => c.LevelGcFreeSlots = v, 0, 140);
        Int("Questionable закончил, если стоим без дела, сек", c.LevelQuestIdleSeconds, v => c.LevelQuestIdleSeconds = v, 10, 600);
        Int("Лимит квестовой фазы, мин", c.LevelQuestMaxMinutes, v => c.LevelQuestMaxMinutes = v, 5, 1000);
        Int("Лимит одного данжа, мин", c.LevelDungeonMaxMinutes, v => c.LevelDungeonMaxMinutes = v, 30, 1000);
        Int("Лимит FATE-фазы (1–14), мин", c.LevelFateMaxMinutes, v => c.LevelFateMaxMinutes = v, 10, 1000);
        Int("Зависание: стоит без дела, мин", c.LevelStallMinutes, v => c.LevelStallMinutes = v, 1, 60);
        ImGui.TextColored(Ui.Muted, "Нужны: AutoDuty, Auto FATE Grind (зоны для 1–14 выбираются в /afg), Questionable с выключенным «Run AutoDuty Leveling mode when underleveled for MSQ», для сдачи в GC — ранг 6+ и AutoRetainer.");
        Ui.EndCard();

        if (lv.Log.Count > 0)
        {
            Ui.BeginCard("lvl-log");
            Ui.Caption("Журнал");
            foreach (var line in lv.Log.Take(40)) ImGui.TextColored(Ui.Muted, line);
            Ui.EndCard();
        }
    }

    // ---------------------------------------------------------------- scrips

    private void DrawScrips()
    {
        var c = p.Config;
        var running = p.Running;
        ImGui.TextColored(Ui.Muted, "Фарм скриптов коллектаблами через GatherBuddy Reborn: крафтерские — очередь Vulcan, собирательские — авто-сбор (в т.ч. рыба). Сдача — сам GatherBuddy (Collectables). Покупки за скрипы потом делает корзина.");

        // Balance and what the cart needs.
        var needs = p.ScripNeeds().ToDictionary(n => n.Kind, n => n.Need);
        if (ImGui.BeginTable("scripbal", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Скрипы", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Есть", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Нужно корзине", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableHeadersRow();
            foreach (var k in ScripFarm.Kinds)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Ui.GameIcon(p.Planner.ItemIcon(k.ItemId), 22);
                ImGui.SameLine();
                Ui.CenteredText(k.Name, 22);
                ImGui.TableNextColumn();
                var have = ScripFarm.Have(k.Kind);
                var need = needs.GetValueOrDefault(k.Kind);
                Ui.CenteredText(have.ToString(), 22, need > 0 && have < need ? Ui.Warn : Ui.Text);
                ImGui.TableNextColumn();
                Ui.CenteredText(need > 0 ? need.ToString() : "—", 22, Ui.Muted);
            }
            ImGui.EndTable();
        }

        Ui.BeginCard("scripfarm");
        Ui.Caption("Нафармить скрипы");
        ImGui.SetNextItemWidth(260);
        if (ImGui.BeginCombo("Скрипы##kind", ScripFarm.Info(c.ScripTabKind).Name))
        {
            foreach (var k in ScripFarm.Kinds)
                if (ImGui.Selectable(k.Name, c.ScripTabKind == k.Kind)) { c.ScripTabKind = k.Kind; p.SaveConfig(); }
            ImGui.EndCombo();
        }
        ImGui.SetNextItemWidth(160);
        var target = c.ScripTabTarget;
        if (ImGui.InputInt("Сколько должно стать", ref target, 100)) { c.ScripTabTarget = Math.Clamp(target, 1, 4000); p.SaveConfig(); }
        if (needs.TryGetValue(c.ScripTabKind, out var cartNeed) && cartNeed > 0)
        {
            ImGui.SameLine();
            if (Ui.IconButton(FontAwesomeIcon.ShoppingCart, $"как в корзине ({cartNeed})")) { c.ScripTabTarget = cartNeed; p.SaveConfig(); }
        }
        var fish = c.ScripAllowFish;
        if (ImGui.Checkbox("Разрешить рыбу для собирательских скриптов", ref fish)) { c.ScripAllowFish = fish; p.SaveConfig(); }
        if (fish && !ScripFarm.FishingAllowedInGbr())
            ImGui.TextColored(Ui.Warn, "GatherBuddy не ловит рыбу в авто-сборе, пока в его настройках выключен «Fish data collection» — рыба пропускается.");

        if (!ScripFarm.TurnInAvailable()) ImGui.TextColored(Ui.Bad, ScripFarm.TurnInMissing);
        var options = p.Scrips.Options(c.ScripTabKind, c.ScripAllowFish);
        var chosenId = c.ScripItem.GetValueOrDefault(c.ScripTabKind);
        var chosen = options.FirstOrDefault(o => o.ItemId == chosenId);
        string Label(Collectable o) => $"{o.Name}  ·  {(o.RecipeId != 0 ? p.Scrips.JobName(o.CraftJob) : o.Fish ? "FSH" : "MIN/BTN")} {o.Level}  ·  {o.Low}/{o.Mid}/{o.High}"
                                       + (p.Scrips.Missing(o).Count > 0 ? $"  ·  нет: {p.Scrips.MissingText(o)}" : "");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##collectable", chosen != null ? Label(chosen) : options.Count > 0 ? "авто: " + Label(options[0]) : "нет подходящих коллектаблов"))
        {
            if (ImGui.Selectable("авто — лучший по награде", chosenId == 0)) { c.ScripItem.Remove(c.ScripTabKind); p.SaveConfig(); }
            foreach (var o in options)
            {
                ImGui.PushID((int)o.ItemId);
                if (ImGui.Selectable(Label(o), o.ItemId == chosenId)) { c.ScripItem[c.ScripTabKind] = o.ItemId; p.SaveConfig(); }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGui.TextColored(Ui.Muted, "Награда: низкая/средняя/высокая коллективность. Расчёт партии — по средней. Список — то, что твои классы могут сделать сейчас; сначала те, для которых всё добывается (сбор, вендоры за гил). «нет: …» — материалы с мобов и т.п., такие пропускаются. Если коллектабл не дал скриптов, фарм сам переключается на следующий.");

        if (running)
        {
            if (Ui.Primary("Остановить", FontAwesomeIcon.Stop, Ui.Bad)) p.Stop();
        }
        else
        {
            ImGui.BeginDisabled(options.Count == 0);
            if (Ui.Primary($"Фармить до {c.ScripTabTarget}", FontAwesomeIcon.Scroll, Ui.Accent)) p.StartScripFarm(c.ScripTabKind, c.ScripTabTarget);
            ImGui.EndDisabled();
        }
        if (p.Phase == Phase.ScripFarming) ImGui.TextColored(Ui.Accent, p.Scrips.Now);
        Ui.EndCard();

        Ui.BeginCard("scripnote");
        Ui.Caption("Как это работает");
        ImGui.TextWrapped("1) Партия: крафтерские — временный список Vulcan «CraftMeThis Scrips» с выбранным коллектаблом (Artisan выключается), собирательские — временный список авто-сбора GatherBuddy. 2) После партии, когда персонаж встал из крафта, плагин жмёт в GatherBuddy «Run Turn-Ins Now» — он сдаёт коллектаблы и покупает по своему списку. 3) Свою автосдачу GatherBuddy плагин не трогает. 4) Повтор, пока скриптов не станет сколько нужно (до 10 партий; две партии без прибавки — стоп).");
        ImGui.TextColored(Ui.Muted, "В корзине материалы за скрипы получают путь «Фарм скриптов»: если скриптов не хватает, этот фарм идёт сам перед покупками. Материалы за томстоуны помечены отдельно — томы плагин не фармит.");
        Ui.EndCard();
    }

    // ---------------------------------------------------------------- history

    private void DrawHistory()
    {
        var runs = p.Config.History;
        if (runs.Count == 0)
        {
            ImGui.TextColored(Ui.Muted, "Запусков пока не было.");
            return;
        }
        if (Ui.IconButton(FontAwesomeIcon.Trash, "Очистить историю")) p.ClearHistory();
        foreach (var r in runs.ToList())
        {
            ImGui.PushID(r.Started.Ticks.GetHashCode());
            var (label, color) = r.Result switch
            {
                "done" => ("готово", Ui.Good),
                "failed" => ("ошибка", Ui.Bad),
                "stopped" => ("остановлено", Ui.Warn),
                _ => ("идёт", Ui.Accent),
            };
            var dur = (r.Ended ?? DateTime.Now) - r.Started;
            var header = $"{r.Started:dd.MM HH:mm}  ·  {(int)dur.TotalMinutes} мин  ·  {string.Join(", ", r.Cart.Take(3))}{(r.Cart.Count > 3 ? $" … (+{r.Cart.Count - 3})" : "")}";
            Ui.BeginCard($"run{r.Started.Ticks}");
            Ui.Pill(label, color);
            ImGui.SameLine();
            ImGui.TextUnformatted(header);
            if (r.Summary.Length > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
                ImGui.TextWrapped(r.Summary);
                ImGui.PopStyleColor();
            }
            if (ImGui.TreeNodeEx($"Журнал ({r.Events.Count})##log"))
            {
                foreach (var e in r.Events)
                {
                    ImGui.TextColored(Ui.Muted, $"{e.Time:HH:mm:ss}");
                    ImGui.SameLine();
                    ImGui.TextColored(Ui.Accent, e.Phase);
                    ImGui.SameLine();
                    ImGui.TextWrapped(e.Text);
                }
                ImGui.TreePop();
            }
            Ui.EndCard();
            ImGui.PopID();
        }
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

    private System.Collections.Generic.List<(uint Territory, string Name, int Level, uint ItemLevel)>? gcDuties;
    private (int Free, int Need) space;
    private DateTime spaceAt = DateTime.MinValue;

    private List<Food.Option>? craftFoods, craftMeds, gatherFoods;
    private DateTime foodsAt = DateTime.MinValue;
    private string? foodError;

    private void DrawFoodCard()
    {
        if (!Ui.BeginCard("set-food")) return;
        var c = p.Config;
        Ui.Caption("Еда и медицина");
        if (craftFoods == null || DateTime.UtcNow - foodsAt > TimeSpan.FromSeconds(3))
        {
            craftFoods = Food.InBags(Food.WellFed, Food.CraftParams);
            craftMeds = Food.InBags(Food.Medicated, Food.CraftParams);
            gatherFoods = Food.InBags(Food.WellFed, Food.GatherParams);
            foodsAt = DateTime.UtcNow;
        }

        if (FoodCombo("Еда на крафт", c.CraftFoodId, c.CraftFoodHq, craftFoods, out var id, out var hq))
        { c.CraftFoodId = id; c.CraftFoodHq = hq; p.SaveConfig(); }
        if (FoodCombo("Медицина на крафт", c.CraftMedicineId, c.CraftMedicineHq, craftMeds!, out id, out hq))
        { c.CraftMedicineId = id; c.CraftMedicineHq = hq; p.SaveConfig(); }
        ImGui.TextColored(Ui.Muted, "Ставится в список Vulcan: он сам ест перед крафтом, когда бафф кончился. Бонус еды учитывается в проверке Craftsmanship/Control рецептов.");

        var gf = p.Vulcan.GatherFood();
        if (gf == null)
            ImGui.TextColored(Ui.Warn, "Еду на сбор не прочитать — GatherBuddy Reborn не загружен?");
        else
        {
            var (gid, gon) = gf.Value;
            uint cur = gon ? gid % 1000000 : 0;
            if (FoodCombo("Еда на сбор", cur, gon && gid > 1000000, gatherFoods!, out id, out hq))
                foodError = p.Vulcan.SetGatherFood(id, hq);
            ImGui.TextColored(Ui.Muted, "Это еда пресета «Default» автосбора GatherBuddy — меняется прямо там. Ест сам GatherBuddy.");
        }
        if (foodError != null) ImGui.TextColored(Ui.Bad, foodError);

        foreach (var (job, st) in c.CrafterStats.OrderBy(kv => kv.Key))
        {
            if (st.Length < 2) continue;
            var abbr = Plugin.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().TryGetRow(job, out var cj) ? cj.Abbreviation.ExtractText() : job.ToString();
            var line = $"{abbr}: Craftsmanship {st[0]}, Control {st[1]}{(st.Length >= 3 && st[2] == 1 ? " (с едой)" : "")}";
            if (c.CraftFoodId != 0 && !(st.Length >= 3 && st[2] == 1))
                line += $" → с едой ~{st[0] + Food.Bonus(c.CraftFoodId, c.CraftFoodHq, Food.Craftsmanship, st[0])} / ~{st[1] + Food.Bonus(c.CraftFoodId, c.CraftFoodHq, Food.Control, st[1])}";
            ImGui.TextColored(Ui.Muted, line);
        }
        if (c.CrafterStats.Count == 0) ImGui.TextColored(Ui.Muted, "Статы крафтеров запоминаются, когда надет класс крафтера.");
        Ui.EndCard();
    }

    /// <summary>Combo of food in the bags plus "не есть"; the chosen one stays listed even when it ran out.</summary>
    private static bool FoodCombo(string label, uint curId, bool curHq, List<Food.Option> options, out uint id, out bool hq)
    {
        id = curId; hq = curHq;
        var changed = false;
        var have = curId == 0 ? 0 : Food.CountInBags(curId, curHq);
        var preview = curId == 0 ? "не есть" : $"{Food.Label(curId, curHq)} — {have} шт.";
        ImGui.SetNextItemWidth(320);
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("не есть", curId == 0)) { id = 0; hq = false; changed = true; }
            foreach (var o in options)
            {
                if (ImGui.Selectable($"{Food.Label(o.ItemId, o.Hq)} — {o.Count} шт.##{o.ItemId}{o.Hq}", o.ItemId == curId && o.Hq == curHq))
                { id = o.ItemId; hq = o.Hq; changed = true; }
                if (ImGui.IsItemHovered() && o.Stats.Length > 0) ImGui.SetTooltip(o.Stats);
            }
            if (options.Count == 0) ImGui.TextColored(Ui.Muted, "в сумках нет подходящего");
            ImGui.EndCombo();
        }
        if (curId != 0 && have == 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Ui.Warn, "нет в сумках");
        }
        return changed;
    }

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
            var resume = c.ResumeFinals;
            if (ImGui.Checkbox("Докрафт: считать корзину целевым количеством (Include Final Crafts)", ref resume)) p.SetResume(resume);
            ImGui.TextColored(Ui.Muted, "Включается сам после прогона, где Vulcan что-то не скрафтил; выключается после удачного прогона или новой корзины.");
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
        DrawFoodCard();
        if (Ui.BeginCard("set-mob"))
        {
            Ui.Caption("Фарм мобов (Hunter)");
            ImGui.SetNextItemWidth(120);
            var mins = c.MobFarmMaxMinutes;
            if (ImGui.InputInt("Максимум минут на дроп", ref mins)) { c.MobFarmMaxMinutes = Math.Clamp(mins, 5, 600); p.SaveConfig(); }
            var engage = c.RsrEngageAll;
            if (ImGui.Checkbox("На время фарма: RSR Engage = все цели в радиусе", ref engage)) { c.RsrEngageAll = engage; p.SaveConfig(); }
            ImGui.TextColored(Ui.Muted, "Hunter сам не бьёт: он ставит /rsr auto, берёт моба в цель и ждёт. С «Engage» по умолчанию (все цели только в данже) RSR в открытом мире свежего моба не трогает. Прежнее значение возвращается после фарма.");
            ImGui.TextColored(Ui.Muted, "Путь «Фарм мобов» появляется у предметов из базы мобов Hunter. На время запуска его список «CraftMeThis» — единственный включённый; твои списки потом включаются обратно. Hunter нужны vnavmesh, Rotation Solver Reborn и Lifestream.");
            Ui.EndCard();
        }
        if (Ui.BeginCard("set-gc"))
        {
            Ui.Caption("Фарм печатей GC (AutoDuty)");
            var (deliveryProblem, deliveryOk) = GcFarm.DeliveryStatus();
            Ui.Icon(deliveryProblem == null ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.ExclamationTriangle, deliveryProblem == null ? Ui.Good : Ui.Bad);
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, deliveryProblem == null ? Ui.Good : Ui.Bad);
            ImGui.TextWrapped(deliveryProblem ?? deliveryOk);
            ImGui.PopStyleColor();
            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo("Режим AutoDuty", c.GcLevelingMode))
            {
                foreach (var mode in new[] { "Support", "Trust_Solo", "Trust_Group" })
                    if (ImGui.Selectable(mode, c.GcLevelingMode == mode)) { c.GcLevelingMode = mode; p.SaveConfig(); }
                ImGui.EndCombo();
            }
            if (gcDuties == null || ImGui.IsWindowAppearing()) gcDuties = GcFarm.AllDuties();
            var chosen = gcDuties.FirstOrDefault(d => d.Territory == c.GcDutyTerritory);
            var preview = c.GcDutyTerritory == 0 || chosen.Territory == 0 ? "авто — самый высокий доступный" : $"{chosen.Name} (ур. {chosen.Level}, ilvl {chosen.ItemLevel})";
            ImGui.SetNextItemWidth(320);
            if (ImGui.BeginCombo("Данж", preview))
            {
                if (ImGui.Selectable("авто — самый высокий доступный", c.GcDutyTerritory == 0)) { c.GcDutyTerritory = 0; p.SaveConfig(); }
                foreach (var d in gcDuties)
                    if (ImGui.Selectable($"{d.Name}##{d.Territory}", c.GcDutyTerritory == d.Territory)) { c.GcDutyTerritory = d.Territory; p.SaveConfig(); }
                    else if (ImGui.IsItemHovered()) ImGui.SetTooltip($"уровень {d.Level}, ilvl {d.ItemLevel}");
                ImGui.EndCombo();
            }
            if (gcDuties.Count == 0) ImGui.TextColored(Ui.Warn, "Список данжей AutoDuty не прочитался (AutoDuty не загружен?) — будет «авто».");
            ImGui.SetNextItemWidth(120);
            var runs = c.GcMaxRuns;
            if (ImGui.InputInt("Максимум пробегов", ref runs)) { c.GcMaxRuns = Math.Clamp(runs, 1, 100); p.SaveConfig(); }
            ImGui.TextColored(Ui.Muted, "Пробег: /autoduty run <Support|Trust> <данж> 1 → сдача лута в GC → следующий пробег. Без автоэкипировки. Если выбранный данж недоступен на уровне класса — берётся самый высокий доступный. Более низкий данж бывает быстрее.");
            ImGui.TextColored(Ui.Muted, "Сдача (/autoduty gcturnin) идёт через AutoRetainer: нужен ранг GC 6+ и разрешённая сдача шмота в его Expert Delivery. Две сдачи подряд без печатей останавливают фарм.");
            Ui.EndCard();
        }
        if (Ui.BeginCard("set-space"))
        {
            Ui.Caption("Место в сумке");
            var split = c.SplitBySpace;
            if (ImGui.Checkbox("Делить корзину на партии, если не влезает в сумку", ref split)) { c.SplitBySpace = split; p.SaveConfig(); }
            ImGui.SetNextItemWidth(120);
            var reserve = c.SpaceReserve;
            if (ImGui.InputInt("Держать свободными, ячеек", ref reserve)) { c.SpaceReserve = Math.Clamp(reserve, 0, 60); p.SaveConfig(); }
            ImGui.TextColored(Ui.Muted, "Оценка: сырьё, которое ещё надо добыть, полуфабрикаты и готовые вещи — по стекам (кристаллы не считаются). Валюты (биколоры, печати, скрипы) фармятся на всю корзину сразу, по партиям идут закупка, фарм мобов и крафт.");
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
