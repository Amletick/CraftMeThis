using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CraftMeThis;

/// <summary>An item and amount read from a Teamcraft export, with the recipe if the export named one.</summary>
public readonly record struct ImportedItem(uint ItemId, uint? RecipeId, int Quantity);

public sealed record ImportResult(List<CartLine> Lines, List<string> Skipped, string? Error);

/// <summary>
/// Reads Teamcraft exports:
///  * import links "https://ffxivteamcraft.com/import/&lt;base64&gt;" (or the bare base64), whose payload is
///    "itemId,recipeId|null,quantity;..." — the same format LazyCrafter exports;
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
            // Without a "Final items" section only take what can be crafted: raw materials are LazyCrafter's job.
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
