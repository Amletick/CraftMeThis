# CraftMeThis

A Dalamud plugin that drives **LazyCrafter** and **Auto FATE Grind** together:
a Teamcraft list goes in, and crafted items come out.

1. **Cart.** Import a Teamcraft list into the plugin's cart and send it to LazyCrafter.
2. **Gemstones.** Walk the recipe tree, using what is already in your bags, and count the Bicolor Gemstones needed
   for materials sold by Gemstone Traders (for example Alpaca Fillet or Megamaguey Pineapple).
3. **Zone.** Farm in the field zone whose Gemstone Trader sells those materials. When only city traders sell them,
   use the highest-level zone of that expansion. You can also pick the zone by hand.
4. **Job.** Switch to the first enabled job in your priority list that has a gearset and the level for that zone.
5. **Farm and buy.** Give Auto FATE Grind the zone and a shopping list, then run `/afg start`. AFG farms until it has
   enough gemstones, buys the materials from the Gemstone Trader and stops. Your own AFG settings are restored afterwards.
6. **Craft.** Once the materials are in the bags and the character is idle, run `/lcraft dispatch`.

LazyCrafter cannot buy from Gemstone Traders. Bridging that gap is the point of this plugin.

## Window (`/craftme` or `/cmt`)

The header has buttons that open the LazyCrafter (`/lcraft`) and Auto FATE Grind (`/afg`) windows.

- **Farm**: plan, gemstone count, farm zone (automatic or manual), the job that will be used, Start/Stop.
- **Cart**: import from Teamcraft, edit craft counts, send to LazyCrafter (replace its cart or add to it).
- The **Teamcraft** button in the header opens the imported list, or the plugin's cart as a Teamcraft import.
- **Jobs**: job priority (up/down arrows), on/off per job, level and gearset.
- **Settings**: extra gemstones on top, dispatch after farming, idle wait, soft-stop timeout, whether to set the AFG zone.

Commands: `/craftme plan`, `/craftme start`, `/craftme stop`.

## What it will not do

A panel at the bottom of the window lists what is left to you for the current LazyCrafter cart:
- materials with no gathering node, fishing spot or gil vendor (market board, drops; LazyCrafter opens the board and waits);
- Gemstone Trader stock that needs a higher shared FATE rank;
- carts that need more than 1500 gemstones;
- Free Company workshop phases and their raw materials (from an imported list);
- taking materials out of the chocobo saddlebag or off retainer sale.

## Teamcraft import

Paste any of these:
- a list link (`https://ffxivteamcraft.com/list/<id>`). The list is read from Teamcraft's public data, so it must be
  shared by link. Free Company workshop items (submarine and airship parts, recipe id `fc…`) are built phase by phase
  in the workshop, which LazyCrafter cannot do, so their craftable components go into the cart instead;
- an import link (`https://ffxivteamcraft.com/import/...`, also exported by LazyCrafter);
- a "Copy as text" list (only the *Final items* section is used);
- plain lines like `5x Boiled Alpaca Steak`.

Teamcraft amounts are items, so they are converted to crafts using the recipe yield.

## Requirements and limits

- The gemstone materials are bought by **Auto FATE Grind**. LazyCrafter does not know Gemstone Traders (FateShop NPCs),
  so it always sends those materials to the market board. For the run, CraftMeThis gives AFG a shopping list
  (each item up to the amount needed), a trade threshold equal to the gemstones needed, "spend all" and "stop after
  trade". AFG farms, travels to the trader, buys and stops. Then the cart is dispatched with the materials already in
  the bags. AFG's own settings are restored afterwards.
- Bicolor Gemstones are capped at 1500.
- Trader stock depends on the zone's shared FATE rank; the plugin shows the rank each material needs.
- Zones: Endwalker and Dawntrail field zones (those whose Gemstone Trader is placed in the Level sheet).
- Neither LazyCrafter nor Auto FATE Grind has IPC for this. The plugin calls LazyCrafter's own
  `Catalog.ClearCart/AddToCart` and edits Auto FATE Grind's `Plugin.Cfg` through reflection, so an update of either
  plugin can break that part. The plugin reports which part failed.

## Install

In `/xlsettings` → Experimental → Custom Plugin Repositories, add:

```
https://github.com/Amletick/CraftMeThis/releases/latest/download/repo.json
```

## Release

```
python publish.py 0.5.1
```

This builds the plugin, bumps the version, pushes to GitHub and creates a GitHub release with `latest.zip` and `repo.json`.
The `releases/latest/download/...` link is not cached, so Dalamud sees the new version immediately
(raw.githubusercontent.com caches for about five minutes).
