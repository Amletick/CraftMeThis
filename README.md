# Bicolor Bridge

A Dalamud plugin that connects **LazyCrafter** and **Auto FATE Grind**.

LazyCrafter buys materials from a Gemstone Trader only when you already have enough Bicolor Gemstones.
Otherwise it puts them on the market-board list. Bicolor Bridge fills that gap:

1. Reads the LazyCrafter cart and walks the recipe tree, using what is already in your bags.
2. Counts the Bicolor Gemstones needed for materials sold for gemstones (for example Alpaca Fillet or Megamaguey Pineapple).
3. If you are short: `/afg start`, then `/afg stop soft` as soon as the count is reached.
4. Waits until the character is idle, then runs `/lcraft dispatch`. LazyCrafter now buys from the Gemstone Trader.

## Cart and Teamcraft import

The **Cart** tab holds the plugin's own cart. Fill it from Teamcraft by pasting one of these:
- an import link (`https://ffxivteamcraft.com/import/...`, also exported by LazyCrafter);
- a "Copy as text" list (only the *Final items* section is used);
- plain lines like `5x Boiled Alpaca Steak`.

Teamcraft amounts are items, so they are turned into crafts using the recipe yield.
**Send to LazyCrafter** replaces (or adds to) LazyCrafter's cart. LazyCrafter has no cart IPC, so this calls
its own `CatalogService.ClearCart/AddToCart` through reflection and may break when LazyCrafter changes.

## Usage

- `/bbridge`: opens the window (plan, gemstone count, settings)
- `/bbridge plan`: prints how many gemstones the cart needs
- `/bbridge start`: farms and starts the cart
- `/bbridge stop`: stops Auto FATE Grind

Requirements:
- LazyCrafter with **Prefer a currency shop** enabled.
- Auto FATE Grind with your farming zones selected and an **empty** gemstone shopping list.
- Bicolor Gemstones are capped at 1500, so larger carts need several runs.

## Install

In `/xlsettings` → Experimental → Custom Plugin Repositories, add:

```
https://raw.githubusercontent.com/Amletick/BicolorBridge/main/repo.json
```

## Build

```
dotnet build -c Release
```

The output is `bin/Release/BicolorBridge/latest.zip`. Copy it to `dist/latest.zip` before committing a release.
