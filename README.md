# CraftMeThis

A Dalamud plugin that drives **LazyCrafter** and **Auto FATE Grind** together:
a Teamcraft list goes in, and crafted items come out.

1. **Cart.** Import a Teamcraft list into the plugin's cart and send it to LazyCrafter.
2. **Gemstones.** Walk the recipe tree, using what is already in your bags, and count the Bicolor Gemstones needed
   for materials sold by Gemstone Traders (for example Alpaca Fillet or Megamaguey Pineapple).
3. **Zone.** Farm in the field zone whose Gemstone Trader sells those materials. When only city traders sell them,
   use the highest-level zone of that expansion. You can also pick the zone by hand.
4. **Job.** Switch to the first enabled job in your priority list that has a gearset and the level for that zone.
5. **Farm.** Put the zone into Auto FATE Grind, run `/afg start`, then `/afg stop soft` once you have enough gemstones.
   Your own AFG zones and class-queue setting are restored afterwards.
6. **Craft.** Once the character is idle, run `/lcraft dispatch`. LazyCrafter now buys from the Gemstone Trader
   instead of the market board.

LazyCrafter only uses a currency shop when the currency is already there. Bridging that gap is the point of this plugin.

## Window (`/craftme` or `/cmt`)

- **Farm**: plan, gemstone count, farm zone (automatic or manual), the job that will be used, Start/Stop.
- **Cart**: import from Teamcraft, edit craft counts, send to LazyCrafter (replace its cart or add to it).
- **Jobs**: job priority (up/down arrows), on/off per job, level and gearset.
- **Settings**: extra gemstones on top, dispatch after farming, idle wait, soft-stop timeout, whether to set the AFG zone.

Commands: `/craftme plan`, `/craftme start`, `/craftme stop`.

## Teamcraft import

Paste any of these:
- an import link (`https://ffxivteamcraft.com/import/...`, also exported by LazyCrafter);
- a "Copy as text" list (only the *Final items* section is used);
- plain lines like `5x Boiled Alpaca Steak`.

Teamcraft amounts are items, so they are converted to crafts using the recipe yield.

## Requirements and limits

- LazyCrafter with **Prefer a currency shop** enabled.
- Auto FATE Grind with an **empty** gemstone shopping list, so that it does not spend the gemstones.
- Bicolor Gemstones are capped at 1500.
- Trader stock depends on the zone's shared FATE rank; the plugin shows the rank each material needs.
- Zones: Endwalker and Dawntrail field zones (those whose Gemstone Trader is placed in the Level sheet).
- Neither LazyCrafter nor Auto FATE Grind has IPC for this. The plugin calls LazyCrafter's own
  `Catalog.ClearCart/AddToCart` and edits Auto FATE Grind's `Plugin.Cfg` through reflection, so an update of either
  plugin can break that part. The plugin reports which part failed.

## Install

In `/xlsettings` → Experimental → Custom Plugin Repositories, add:

```
https://raw.githubusercontent.com/Amletick/CraftMeThis/main/repo.json
```

## Build

```
dotnet build -c Release
```

The output is `bin/Release/CraftMeThis/latest.zip`. Copy it to `dist/latest.zip` and bump `AssemblyVersion` in `repo.json`.
