# CraftMeThis

A Dalamud plugin that turns a Teamcraft list into crafted items. It coordinates two other plugins:

- **GatherBuddy Reborn**: its **Vulcan** module buys from vendors, gathers what is missing and crafts.
- **Auto FATE Grind** (AFG): farms Bicolor Gemstones when the cart needs items from a Gemstone Trader.

CraftMeThis plans the run, picks the farm zone and job, keeps track of everything and puts the other plugins' settings back afterwards.

## How a run goes

1. **Cart.** Import a Teamcraft list (list link, import link or text). The cart is mirrored into Vulcan's crafting
   list "CraftMeThis".
2. **Plan.** CraftMeThis walks the recipe tree against your bags and decides, for every missing raw material, how to
   get it:
   - *gather/fish*: Vulcan's queue gathers it;
   - *Bicolor*: Gemstone Trader, paid with gemstones that AFG farms;
   - *vendor*: Vulcan's vendor buy list (gil, Grand Company seals, beast tribe currencies, scrips…);
   - *GC seal farm*: for Grand Company items when the seals are short — AutoDuty runs dungeons and turns the loot in;
   - *mob farm*: drops that **Hunter** (HunterV2) has in its mob database — it flies to the spawns and kills;
   - *by hand*: market board or mob drops.

   "Auto" picks gather → gemstones → vendor → GC seal farm → mob farm → by hand. A vendor whose currency is short (no beast
   tribe currency, not enough GC seals…) is skipped; for GC items the seal farm is chosen instead. Each material can be switched by hand on the **Materials** tab.
3. **Farm** (only if gemstones are short). Equip the first suitable job from the priority list, set the farm zone (the
   zone whose Gemstone Trader sells the materials) in AFG, and turn off AFG's level filter and its own purchases.
   Then `/afg start`, and `/afg stop soft` once there are enough gemstones.
4. **GC seals** (only if a GC purchase is short of seals and AutoDuty is loaded). Like the job-leveling SND script:
   equip the highest job from the priority list, `/autoduty cfg leveling <Support|Trust_Solo|Trust_Group>`,
   `/autoduty run 1` (leveling mode picks the highest dungeon for the level), wait for the duty to end,
   `/autoduty gcturnin` (expert delivery; needs GC rank 6+ and AutoRetainer), `/autoduty autoequip`. This repeats
   until the seals cover the GC purchases or the run limit (default 10) is reached.
5. **Buy.** Vulcan runs the "CraftMeThis" vendor buy list. If it fails to buy a gemstone item (seen with the
   Tuliyollal trader: "Timed out opening … special shop"), AFG's own trader hand-off buys it instead.
6. **Mob drops** (if any material is on the mob-farm route). A "CraftMeThis" hunt list in Hunter becomes the only
   enabled list (item, target count); Auto-Hunt is switched on and runs until the items are in the bags (Hunter
   then turns itself off and goes home) or the time limit (default 60 min) is reached. Your own hunt lists are
   enabled again afterwards.
7. **Craft.** Turn Artisan off (Vulcan does not craft while it is loaded), run Vulcan's gather + craft queue on the
   "CraftMeThis" list ("Skip if Already Have Enough" on), then turn Artisan back on.
8. **Report.** List what was crafted and what was not, with the ingredients that were missing. After a run with gaps,
   **resume mode** turns on: Vulcan's "Include Final Crafts" is set, so the next run only crafts what is still missing.

All changes to AFG settings (zones, class queue, level filter, shopping list, trade settings) are restored when the
run ends or is stopped.

## Window (`/craftme`, `/cmt`)

The header has buttons for Teamcraft (opens the imported list, or the cart as a Teamcraft import), Vulcan and AFG.

- **Farm**: gemstones (have / need), farm zone (auto or manual) and the job that will be used, gemstone materials,
  vendor purchases, the start button and status. Resume mode is shown here when it is on.
- **Cart**: Teamcraft import, craft counts, "Materials in Vulcan" (opens Vulcan's own Materials window for the cart).
- **Materials**: every missing raw material, grouped by route, with Vulcan's vendor price and currency. Materials
  whose currency is short are shown in red. The route can be changed per material.
- **Tree**: the cart as a craft tree (cart recipe → precrafts → raw materials, crystals hidden). Each line has bag
  counts and a status: done, crafting now, ready, waiting for materials, gather, buy for gemstones,
  farm gemstones and buy, vendor, by hand, not crafted. At the bottom, a live strip shows the pipeline steps,
  what is happening right now (the item being bought, the recipe and count being crafted, GatherBuddy's
  auto-gather status) and what comes next.
- **Auto-leveling (beta)**: a port of the job-leveling SND script. It levels the current combat job: 1–14 with FATEs
  (Auto FATE Grind, until the next quest level), then one AutoDuty leveling dungeon at a time (Trust from 71, Support as
  fallback; autoequip and retry if no dungeon lets the job in), autoequip after each dungeon and GC turn-in when the
  bags run low. At each class/job quest level it hands over to Questionable (`/qst start`) until the character idles.
  A stall watchdog restarts a dungeon in which nothing happens. Job quests are the fragile part, hence beta.
- **Dependencies**: every plugin CraftMeThis works with, with its status (running / disabled / missing). Buttons add
  the plugin's repository to Dalamud and install it, and the (!) icon lists what to set up in that plugin. This uses
  Dalamud's own installer internals (`ThirdRepoList`, `SetPluginReposFromConfigAsync`, `InstallPluginAsync`) through
  reflection, and only runs when you click.
- **History**: the last 30 runs: start, duration, result (done / failed / stopped), the cart, the final summary and a
  time-stamped log of every step.
- Saved carts (top of the **Cart** tab): save the current cart under a name, then load it instead of the current one,
  add it to the current one, or delete it.
- **Jobs**: job priority for FATE farming: up/down arrows, on/off, level and gearset.
- **Settings**: extra gemstones, AFG zone on/off, vendor purchases on/off, craft after buying, retainers,
  Artisan off/on, resume mode, idle wait, AFG soft-stop timeout.

A panel at the bottom lists what the plugin will not do by itself: market/drop materials, missing vendor currency,
Gemstone Trader stock that needs a higher shared FATE rank, carts over 1500 gemstones, and Free Company workshop
phases.

Commands: `/craftme plan`, `/craftme start`, `/craftme stop`.

## Teamcraft import

- List link `https://ffxivteamcraft.com/list/<id>`. It is read from Teamcraft's public data, so the list must be
  shared by link. Free Company workshop items (`fc…` recipes) are built in the workshop, so their craftable
  components go into the cart instead.
- Import link `https://ffxivteamcraft.com/import/...`.
- "Copy as text" (only the *Final items* section is used), or plain lines like `5x Boiled Alpaca Steak`.

Teamcraft amounts are items, so they are converted to crafts using the recipe yield.

## Requirements and limits

- GatherBuddy Reborn (tested with 7.5.6.1); Auto FATE Grind (2.24) when gemstones are needed; AutoDuty (0.0.0.399) for GC seals; Hunter (HunterV2 1.4.5.3, needs vnavmesh, Rotation Solver Reborn, Lifestream) for mob drops.
- Neither of them has IPC for this. CraftMeThis calls Vulcan's `VendorBuyListManager`, `CraftingListManager` and
  `CraftingGatherBridge`, and edits AFG's `Plugin.Cfg`, all through reflection. An update of either plugin can break
  that part; the plugin then reports which part failed.
- Crafting uses Vulcan's solver (Raphael), not Artisan.
- Bicolor Gemstones are capped at 1500. Farm zones: Endwalker and Dawntrail field zones.

## Install

In `/xlsettings` → Experimental → Custom Plugin Repositories, add:

```
https://github.com/Amletick/CraftMeThis/releases/latest/download/repo.json
```

## Release

```
python publish.py 0.15.0
```

This builds the plugin, bumps the version, pushes and creates a GitHub release with `latest.zip` and `repo.json`.
The `releases/latest/download/...` link is not cached, so Dalamud sees the new version at once.
