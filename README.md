# CraftMeThis

A Dalamud plugin that drives **GatherBuddy Reborn's Vulcan** and **Auto FATE Grind** together:
a Teamcraft list goes in, and crafted items come out.

1. **Cart.** Import a Teamcraft list (list link, import link, text) into the plugin's cart.
2. **Plan.** Walk the recipe tree, using what is already in your bags: gemstones needed, vendor purchases
   (gil, Grand Company, scrips, Gemstone Trader) and what only the market board or drops give.
3. **Farm.** If gemstones are short: switch to the first suitable job from your priority list, put the farm zone
   (where the Gemstone Trader sells the materials) into Auto FATE Grind, turn off its level filter and own purchases,
   `/afg start`, and `/afg stop soft` once there are enough. Your AFG settings are restored afterwards.
4. **Buy.** Build a "CraftMeThis" vendor buy list in GatherBuddy's **Vulcan** and run it.
5. **Craft.** Turn Artisan off (Vulcan does not craft while it is loaded), start an ephemeral Vulcan crafting list
   with the cart (it gathers what is missing and crafts with its own solver), then turn Artisan back on.

## Window (`/craftme` or `/cmt`)

The header has buttons that open the Teamcraft list, Vulcan (`/vulcan`) and Auto FATE Grind (`/afg`).

- **Farm**: plan, gemstone count, farm zone (automatic or manual), the job that will be used, Start/Stop.
- **Cart**: import from Teamcraft, edit craft counts.
- The **Teamcraft** button in the header opens the imported list, or the plugin's cart as a Teamcraft import.
- **Materials**: every raw material the cart is missing, grouped by route — gather/fish (Vulcan's queue gathers it),
  Bicolor (AFG farms the gemstones, Vulcan buys), vendor (Vulcan's buy list: gil, GC, scrips…) or market/by hand.
  Each material can be switched to any route it supports; "auto" picks gather → gemstones → vendor → by hand.
- **Jobs**: job priority (up/down arrows), on/off per job, level and gearset.
- **Settings**: extra gemstones, AFG zone, vendor purchases on/off, craft after buying, retainers, Artisan off/on, idle wait, soft-stop timeout.

Commands: `/craftme plan`, `/craftme start`, `/craftme stop`.

## What it will not do

A panel at the bottom of the window lists what is left to you for the cart:
- materials with no gathering node, fishing spot or vendor Vulcan knows (market board, drops);
- Gemstone Trader stock that needs a higher shared FATE rank;
- carts that need more than 1500 gemstones;
- Free Company workshop phases and their raw materials (from an imported list);

## Teamcraft import

Paste any of these:
- a list link (`https://ffxivteamcraft.com/list/<id>`). The list is read from Teamcraft's public data, so it must be
  shared by link. Free Company workshop items (submarine and airship parts, recipe id `fc…`) are built phase by phase
  in the workshop, which a crafting queue cannot do, so their craftable components go into the cart instead;
- an import link (`https://ffxivteamcraft.com/import/...`);
- a "Copy as text" list (only the *Final items* section is used);
- plain lines like `5x Boiled Alpaca Steak`.

Teamcraft amounts are items, so they are converted to crafts using the recipe yield.

## Requirements and limits

- Requires **GatherBuddy Reborn** (Vulcan buys and crafts) and, when gemstones are needed, **Auto FATE Grind**.
  Vulcan's own vendor data covers gil vendors, Grand Company exchange, scrips and Gemstone Traders.
- Bicolor Gemstones are capped at 1500.
- Trader stock depends on the zone's shared FATE rank; the plugin shows the rank each material needs.
- Zones: Endwalker and Dawntrail field zones (those whose Gemstone Trader is placed in the Level sheet).
- Neither Vulcan nor Auto FATE Grind has IPC for this. The plugin calls Vulcan's own `VendorBuyListManager`,
  `CraftingListManager` and `CraftingGatherBridge` and edits Auto FATE Grind's `Plugin.Cfg` through reflection,
  so an update of either plugin can break that part. The plugin reports which part failed.
- Crafting uses Vulcan's solver, not Artisan.

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
