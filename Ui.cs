using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;

namespace CraftMeThis;

/// <summary>The plugin's look: a dark theme with a teal accent, rounded cards, pills and game icons.</summary>
internal static class Ui
{
    public static readonly Vector4 Bg = Rgb(0x14, 0x16, 0x1c);
    public static readonly Vector4 Card = Rgb(0x1d, 0x20, 0x29);
    public static readonly Vector4 CardHover = Rgb(0x25, 0x29, 0x34);
    public static readonly Vector4 Line = Rgb(0x2e, 0x33, 0x40);
    public static readonly Vector4 Text = Rgb(0xe6, 0xe9, 0xef);
    public static readonly Vector4 Muted = Rgb(0x8b, 0x93, 0xa5);
    public static readonly Vector4 Accent = Rgb(0x3d, 0xd6, 0xc1);
    public static readonly Vector4 AccentDim = Rgb(0x1f, 0x6f, 0x66);
    public static readonly Vector4 Gem = Rgb(0xc7, 0x8b, 0xff);
    public static readonly Vector4 Good = Rgb(0x6b, 0xd9, 0x8a);
    public static readonly Vector4 Warn = Rgb(0xf5, 0xb0, 0x54);
    public static readonly Vector4 Bad = Rgb(0xef, 0x6b, 0x6b);

    private const int Colors = 26;
    private const int Vars = 11;

    public static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    private static Vector4 A(Vector4 c, float a) => c with { W = a };

    public static void Push()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Card);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Card);
        ImGui.PushStyleColor(ImGuiCol.Border, Line);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Rgb(0x26, 0x2a, 0x35));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Rgb(0x2e, 0x33, 0x40));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Rgb(0x34, 0x3a, 0x48));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, Bg);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, Rgb(0x18, 0x1b, 0x22));
        ImGui.PushStyleColor(ImGuiCol.Button, Rgb(0x2a, 0x2f, 0x3b));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Rgb(0x34, 0x3a, 0x49));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentDim);
        ImGui.PushStyleColor(ImGuiCol.Header, A(Accent, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, A(Accent, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, A(Accent, 0.38f));
        ImGui.PushStyleColor(ImGuiCol.Tab, Rgb(0x1d, 0x20, 0x29));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, A(Accent, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, AccentDim);
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Gem);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, Rgb(0x22, 0x26, 0x30));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, A(Text, 0.025f));
        ImGui.PushStyleColor(ImGuiCol.Separator, Line);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 12));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(9, 5));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f);
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(Vars);
        ImGui.PopStyleColor(Colors);
    }

    // ---------------------------------------------------------------- widgets

    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            if (color is { } c) ImGui.TextColored(c, icon.ToIconString());
            else ImGui.TextUnformatted(icon.ToIconString());
        }
    }

    public static void GameIcon(uint iconId, float size)
    {
        if (iconId == 0) { ImGui.Dummy(new Vector2(size)); return; }
        var wrap = Plugin.Textures.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(size));
    }

    /// <summary>Text vertically centred on a row of the given height (for icon + label rows).</summary>
    public static void CenteredText(string text, float rowHeight, Vector4? color = null)
    {
        var y = ImGui.GetCursorPosY();
        ImGui.SetCursorPosY(y + (rowHeight - ImGui.GetTextLineHeight()) / 2);
        if (color is { } c) ImGui.TextColored(c, text); else ImGui.TextUnformatted(text);
    }

    /// <summary>A small rounded label with a coloured background.</summary>
    public static void Pill(string text, Vector4 color)
    {
        var dl = ImGui.GetWindowDrawList();
        var pad = new Vector2(8, 2);
        var size = ImGui.CalcTextSize(text) + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(A(color, 0.18f)), size.Y / 2);
        dl.AddRect(pos, pos + size, ImGui.GetColorU32(A(color, 0.55f)), size.Y / 2);
        dl.AddText(pos + pad, ImGui.GetColorU32(color), text);
        ImGui.Dummy(size);
    }

    /// <summary>Small upper-case section caption.</summary>
    public static void Caption(string text)
    {
        ImGui.TextColored(Muted, text.ToUpperInvariant());
    }

    // Dalamud's ImGui has no auto-sizing child windows, so a card remembers its content height from the last frame.
    private static readonly System.Collections.Generic.Dictionary<uint, float> CardHeights = new();
    private static readonly System.Collections.Generic.Stack<(uint Id, bool Fixed)> Cards = new();

    /// <summary>A rounded card; height 0 = as tall as its content. Always pair with <see cref="EndCard"/>.</summary>
    public static bool BeginCard(string id, float height = 0)
    {
        var key = ImGui.GetID(id);
        var h = height > 0 ? height : CardHeights.TryGetValue(key, out var last) ? last : 60f;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        var open = ImGui.BeginChild(id, new Vector2(-1, h), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysUseWindowPadding);
        ImGui.PopStyleVar();
        Cards.Push((key, height > 0));
        // Always "open": callers draw and call EndCard unconditionally (ImGui clips a hidden child itself).
        _ = open;
        return true;
    }

    public static void EndCard()
    {
        var (key, isFixed) = Cards.Pop();
        if (!isFixed)
        {
            // Content height + bottom padding.
            var contentEnd = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + 10;
            CardHeights[key] = Math.Max(30f, contentEnd);
        }
        ImGui.EndChild();
    }

    /// <summary>A wide accent button; returns true when clicked.</summary>
    public static bool Primary(string label, FontAwesomeIcon icon, Vector4 color, float width = -1)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, A(color, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, A(color, 0.7f));
        ImGui.PushStyleColor(ImGuiCol.Text, Rgb(0x10, 0x14, 0x18));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
        string glyph;
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push()) glyph = icon.ToIconString();
        var w = width < 0 ? ImGui.GetContentRegionAvail().X : width;
        var clicked = ImGui.Button($"##{label}", new Vector2(w, ImGui.GetFrameHeight() + 6));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(label);
        float iconW;
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push()) iconW = ImGui.CalcTextSize(glyph).X;
        var total = iconW + 8 + textSize.X;
        var start = new Vector2(min.X + (max.X - min.X - total) / 2, min.Y + (max.Y - min.Y - textSize.Y) / 2);
        var ink = ImGui.GetColorU32(Rgb(0x10, 0x14, 0x18));
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push()) dl.AddText(start, ink, glyph);
        dl.AddText(start + new Vector2(iconW + 8, 0), ink, label);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        return clicked;
    }

    /// <summary>Icon + text button in the regular style.</summary>
    public static bool IconButton(FontAwesomeIcon icon, string label, string? tooltip = null)
    {
        string glyph;
        float iconW;
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push())
        {
            glyph = icon.ToIconString();
            iconW = ImGui.CalcTextSize(glyph).X;
        }
        var style = ImGui.GetStyle();
        var textW = label.Length > 0 ? ImGui.CalcTextSize(label).X + 6 : 0;
        var size = new Vector2(iconW + textW + style.FramePadding.X * 2, ImGui.GetFrameHeight());
        var clicked = ImGui.Button($"##{icon}{label}", size);
        var min = ImGui.GetItemRectMin();
        var dl = ImGui.GetWindowDrawList();
        var col = ImGui.GetColorU32(ImGuiCol.Text);
        var y = min.Y + style.FramePadding.Y;
        using (Plugin.Pi.UiBuilder.IconFontFixedWidthHandle.Push()) dl.AddText(new Vector2(min.X + style.FramePadding.X, y), col, glyph);
        if (label.Length > 0) dl.AddText(new Vector2(min.X + style.FramePadding.X + iconW + 6, y), col, label);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>Gemstone progress bar with "have / need" on it.</summary>
    public static void GemBar(int have, int need)
    {
        var frac = need <= 0 ? 1f : Math.Clamp(have / (float)need, 0f, 1f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, frac >= 1f ? Good : Gem);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Rgb(0x26, 0x2a, 0x35));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.ProgressBar(frac, new Vector2(-1, 18), need <= 0 ? "нечего фармить" : $"{have} / {need}");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }

    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
