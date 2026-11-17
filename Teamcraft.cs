using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CraftMeThis;

/// <summary>An item and amount read from a Teamcraft export, with the recipe if the export named one.</summary>
public readonly record struct ImportedItem(uint ItemId, uint? RecipeId, int Quantity);

/// <param name="Manual">Things the plugin will not do for this list (shown at the bottom of the window).</param>
public sealed record ImportResult(List<CartLine> Lines, List<string> Skipped, string? Error)
{
    public List<string> Manual { get; init; } = new();
}

/// <summary>
/// Reads Teamcraft exports:
///  * import links "https://ffxivteamcraft.com/import/&lt;base64&gt;" (or the bare base64), whose payload is
///    "itemId,recipeId|null,quantity;..." — the same format LazyCrafter exports;
///  * list links "https://ffxivteamcraft.com/list/&lt;id&gt;", read from Teamcraft's public Firestore document
///    (see <see cref="FromListJson"/>);
///  * "Copy as text" lists ("5x Item name" lines). When the text has a "Final items" section only that section
///    is used, so pre-crafts and raw materials are not added twice.
/// </summary>
public static class Teamcraft
{
    private static readonly Regex QtyFirst = new(@"^\s*(\d+)\s*[xх×]\s*(.+?)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex QtyLast = new(@"^\s*(.+?)\s+[xх×]\s*(\d+)\s*$", RegexOptions.IgnoreCase);

    public static ImportResult Parse(string input, Planner planner)
    {
        input = input.Trim();
        if (input.Length == 0) return new ImportResult(new(), new(), "Пусто. Вставь ссылку импорта Teamcraft или текст списка.");

        var items = TryParseLink(input, out var linkError);
        if (items == null && linkError != null) return new ImportResult(new(), new(), linkError);
        var skipped = new List<string>();
        items ??= ParseText(input, planner, skipped);
        if (items.Count == 0)
            return new ImportResult(new(), skipped, "Не нашёл ни одного предмета. Ожидаю ссылку ffxivteamcraft.com/import/... или строки вида «5x Название».");

        return ToCart(items, planner, skipped);
    }

    private static ImportResult ToCart(IEnumerable<ImportedItem> items, Planner planner, List<string> skipped)
    {
        var lines = new Dictionary<uint, int>();
        foreach (var it in items)
        {
            var recipe = planner.ResolveRecipe(it.ItemId, it.RecipeId);
            if (recipe == null)
            {
                skipped.Add($"{planner.ItemName(it.ItemId)} — не крафтится");
                continue;
            }
            var (recipeId, yield) = recipe.Value;
            var crafts = (it.Quantity + yield - 1) / yield;
            lines[recipeId] = lines.GetValueOrDefault(recipeId) + crafts;
        }
        return new ImportResult(lines.Select(kv => new CartLine(kv.Key, kv.Value)).ToList(), skipped, null);
    }

    // ------------------------------------------------------------- list links

    private static readonly Regex ListLink = new(@"ffxivteamcraft\.com/(?:[a-z-]+/)?list/([A-Za-z0-9]{8,})", RegexOptions.IgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>A Teamcraft import link for a cart: Teamcraft opens it and offers to create a list.</summary>
    public static string ImportLink(IEnumerable<CartLine> cart, Planner planner) =>
        "https://ffxivteamcraft.com/import/" + Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(";",
            cart.Where(l => l.Crafts > 0).Select(l => $"{planner.RecipeItem(l.RecipeId)},{l.RecipeId},{l.Crafts * planner.RecipeYield(l.RecipeId)}"))));

    /// <summary>The list id of a "ffxivteamcraft.com/list/&lt;id&gt;" link, or null.</summary>
    public static string? ListId(string input)
    {
        var m = ListLink.Match(input);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Downloads a list document. Lists shared by link are readable without signing in.</summary>
    public static async Task<string> FetchListJson(string id)
    {
        var url = $"https://firestore.googleapis.com/v1/projects/ffxivteamcraft/databases/(default)/documents/lists/{Uri.EscapeDataString(id)}";
        using var resp = await Http.GetAsync(url).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) throw new InvalidOperationException("такого списка нет");
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Teamcraft ответил {(int)resp.StatusCode} — список, наверное, приватный");
        return body;
    }

    /// <summary>
    /// A Teamcraft list document → cart. Final items with a normal recipe are crafted as they are. Free Company workshop
    /// items (recipe id "fc…") are built in the workshop phase by phase, which a crafting queue cannot do, so their
    /// craftable components go into the cart instead; their raw materials are listed as skipped.
    /// </summary>
    public static ImportResult FromListJson(string json, Planner planner, out string listName)
    {
        listName = "";
        var skipped = new List<string>();
        var items = new List<ImportedItem>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("fields", out var fields))
            return new ImportResult(new(), skipped, "Teamcraft вернул не список.");
        listName = Str(fields, "name") ?? "";
        if (!fields.TryGetProperty("finalItems", out var finals))
            return new ImportResult(new(), skipped, "В списке нет финальных предметов.");

        var workshop = new List<string>();
        var raw = new Dictionary<uint, int>();
        foreach (var f in Values(finals))
        {
            var id = Num(f, "id");
            var left = Math.Max(0, Num(f, "amount") - Num(f, "done"));
            if (id == 0 || left == 0) continue;
            var recipe = Str(f, "recipeId");
            if (uint.TryParse(recipe, out var rid) && rid > 0)
            {
                items.Add(new ImportedItem((uint)id, rid, left));
                continue;
            }

            // Workshop (or otherwise non-standard) item: craft its components.
            workshop.Add(planner.ItemName((uint)id));
            var yield = Math.Max(1, Num(f, "yield"));
            var times = (left + yield - 1) / yield;
            if (!f.TryGetProperty("requires", out var req)) continue;
            foreach (var r in Values(req))
            {
                var rid2 = (uint)Num(r, "id");
                var qty = Num(r, "amount") * times;
                if (rid2 == 0 || qty <= 0) continue;
                if (planner.ResolveRecipe(rid2, null) != null) items.Add(new ImportedItem(rid2, null, qty));
                else if (rid2 > 19) raw[rid2] = raw.GetValueOrDefault(rid2) + qty;
            }
        }

        var manual = new List<string>();
        if (workshop.Count > 0)
        {
            skipped.Add($"предметы мастерской ({string.Join(", ", workshop)}): в корзину пошли их компоненты");
            manual.Add($"Сдать этапы в мастерской компании: {string.Join(", ", workshop)}.");
        }
        if (raw.Count > 0)
            manual.Add("Добыть сырьё для мастерской (оно идёт в этапы, а не в крафт): " +
                       string.Join(", ", raw.OrderByDescending(kv => kv.Value).Select(kv => $"{planner.ItemName(kv.Key)} x{kv.Value}")) + ".");
        if (items.Count == 0)
            return new ImportResult(new(), skipped, "В списке нет ничего, что можно скрафтить.") { Manual = manual };
        return ToCart(items, planner, skipped) with { Manual = manual };
    }

    private static IEnumerable<JsonElement> Values(JsonElement arrayField) =>
        arrayField.TryGetProperty("arrayValue", out var av) && av.TryGetProperty("values", out var vals)
            ? vals.EnumerateArray().Select(v => v.TryGetProperty("mapValue", out var mv) && mv.TryGetProperty("fields", out var fs) ? fs : default)
                  .Where(x => x.ValueKind == JsonValueKind.Object)
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement fields, string key)
    {
        if (!fields.TryGetProperty(key, out var v)) return null;
        foreach (var p in v.EnumerateObject())
            return p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
        return null;
    }

    private static int Num(JsonElement fields, string key) =>
        int.TryParse(Str(fields, key), out var n) ? n : 0;

    // ------------------------------------------------------------- import links

    /// <returns>null when the input is not a link/base64 payload at all.</returns>
    private static List<ImportedItem>? TryParseLink(string input, out string? error)
    {
        error = null;
        var payload = input;
        var idx = input.IndexOf("/import/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            payload = input[(idx + "/import/".Length)..];
            var cut = payload.IndexOfAny(new[] { '?', '#', ' ', '\n', '\r' });
            if (cut >= 0) payload = payload[..cut];
            payload = Uri.UnescapeDataString(payload);
        }
        else if (input.Contains(' ') || input.Contains('\n'))
        {
            return null; // looks like text
        }

        string decoded;
        try
        {
            var b64 = payload.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }
        catch
        {
            if (idx >= 0) error = "Ссылка Teamcraft повреждена: не удалось раскодировать.";
            return null;
        }

        var result = new List<ImportedItem>();
        foreach (var part in decoded.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = part.Split(',');
            if (f.Length < 3 || !uint.TryParse(f[0], out var item) || !int.TryParse(f[2], out var qty) || qty <= 0) continue;
            uint? recipe = uint.TryParse(f[1], out var r) && r > 0 ? r : null;
            result.Add(new ImportedItem(item, recipe, qty));
        }
        if (result.Count == 0)
        {
            if (idx >= 0) error = "В ссылке Teamcraft нет предметов.";
            return null;
        }
        return result;
    }

    private static List<ImportedItem> ParseText(string input, Planner planner, List<string> skipped)
    {
        var lines = input.Replace("\r", "").Split('\n');
        // Teamcraft "Copy as text": sections "Crystals :", "Gathering :", "Pre crafts :", "Final items :" ...
        var finalStart = Array.FindIndex(lines, l => IsHeader(l) && l.Contains("final", StringComparison.OrdinalIgnoreCase));
        IEnumerable<string> body = lines;
        if (finalStart >= 0)
            body = lines.Skip(finalStart + 1).TakeWhile(l => !IsHeader(l));

        var result = new List<ImportedItem>();
        foreach (var raw in body)
        {
            var line = raw.Trim().TrimStart('-', '*', '•').Trim();
            if (line.Length == 0 || IsHeader(line)) continue;
            string name;
            int qty;
            var m = QtyFirst.Match(line);
            if (m.Success) { qty = int.Parse(m.Groups[1].Value); name = m.Groups[2].Value; }
            else if ((m = QtyLast.Match(line)).Success) { name = m.Groups[1].Value; qty = int.Parse(m.Groups[2].Value); }
            else { qty = 1; name = line; }

            var id = planner.FindItem(name);
            if (id == 0)
            {
                skipped.Add($"{name} — не нашёл такой предмет");
                continue;
            }
            // Without a "Final items" section only take what can be crafted: raw materials are gathered or bought during the run.
            if (finalStart < 0 && planner.ResolveRecipe(id, null) == null) continue;
            result.Add(new ImportedItem(id, null, qty));
        }
        return result;
    }

    private static bool IsHeader(string line)
    {
        var t = line.Trim();
        return t.EndsWith(':') && !QtyFirst.IsMatch(t);
    }
}
