# Loot Filter

Installs the 剑心 item filter into the client's own filter folder and selects the tier you pick.

## Converted to 3.29

The upstream pack is S31, which is written for 3.30 (the item classes and some item names changed). The copies in
`filters/` are converted to this server's `3.29.3c` client data by `tools/adapt-lootfilter.py`:

- every `Class` is mapped to a name that exists in 3.29's `ItemClasses` (`Flasks` → `Life/Mana/Hybrid/Utility
  Flasks`, `Support Gem` → `Support Gems`, `Atlas Upgrade Item` → `Atlas Upgrade Items`, …);
- the author's "wildcard" spellings (`" of Rage"`, `"Essence of"`, `"Awakened "`, `"Vaal "`, `"Talisman"`, …) are
  expanded into the concrete item names of 3.29, so those blocks actually match something;
- names that do not exist in 3.29 (the Thread currency, Soul Cores, Mycelium Core, Royal Visage, Cursed Poppet, …)
  are dropped, and a block left with nothing to match is removed — otherwise its bare `Class` would highlight
  every item of that class.

Result: 1369 `BaseType` names and 65 `Class` names, **none** missing from the 3.29 client (before: 35 items and 11
classes missing, i.e. 3.30-only content and 3.30 class names).

## What it does

Path of Exile reads `.filter` files from `Documents\My Games\Path of Exile\` (the same folder it keeps
`production_Config.ini` in). The seven tiers travel with this mod under `filters/`, and its sounds under a
`剑心音效` folder next to them (the filter's `CustomAlertSound` lines use that relative path).

On load the mod:

1. copies all seven `.filter` files into that folder (only the ones whose size differs, so the 900 KB are not
   rewritten every start);
2. fills in the sounds from **音效来源目录** (27 MB, missing files only);
3. unless the game is running, rewrites `item_filter=` in `production_Config.ini` to the tier you picked, after
   keeping a one-time backup `production_Config.ini.before-lootfilter`.

Running the client while it writes would lose the change: the client saves its own `production_Config.ini` when it
exits, so the mod skips the selection then and says so in the log.

## Tiers

| Key | File |
| --- | --- |
| `campaign` | 剑心过滤[1].剧情.filter (1-70) |
| `mapping` | 剑心过滤[2].异界.filter (71-90) |
| `advanced` | 剑心过滤[3].进阶.filter |
| `speed` | 剑心过滤[4].速刷.filter |
| `magic` | 剑心过滤[5].打宝.filter |
| `ultimate` | 剑心过滤[6].终极.filter |
| `clean` | 剑心过滤[7].纯净.filter |

All seven are installed; the setting only decides which one gets selected. In game you can switch at
Options → Game → Item Filter.

## Settings

| Key | Meaning |
| --- | --- |
| `enabled` | Off leaves the client alone. |
| `profile` | Which tier to select. |
| `selectInGame` | Rewrite the client's `item_filter` (off: files only). |
| `copySounds` | Copy the alert sounds too. |
| `soundSource` | Folder holding `剑心音效`'s mp3s. Empty: filters only. |
| `targetFolder` | Client filter folder. Empty: `Documents\My Games\Path of Exile`. |

## Command

`/lootfilter [status|install]` — installs again, or reports where the files are and how many arrived.

## Only the machine running the server

The mod writes into the file system of the machine the Gateway runs on. A player on another machine does not get
these files from it: their client needs the same `.filter` files and `剑心音效` folder, which is what the client
patch and the launcher's copy step are for.

## Credit

The filters are 剑影流心's 剑心过滤 (S31, 2026-11-13), https://www.jxpoe.cn — shipped unchanged.
