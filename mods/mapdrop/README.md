# Map Drop

Controls how often a kill leaves a map, as Currency Loot does for currency.

The server rolls a map drop of its own on every kill, but a map area of this port spawns far fewer monsters than the
game's, so that roll alone leaves a character without maps to keep running. This mod adds its own roll: a chance by
monster rarity, of the tier the open map is played at (or a fixed tier outside a map), with a map's boss always
leaving one.

Off by default. Enable it with `/mod_enable mapdrop`; `/mod_settings mapdrop` shows what it is doing.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| Chance per normal kill | 0.03 | A normal monster's kill drops a map three times in a hundred |
| Chance per magic kill | 0.07 | Magic (blue) monsters |
| Chance per rare kill | 0.15 | Rare (yellow) monsters |
| Chance per unique kill | 1 | Unique monsters, a map's boss among them |
| Drop the open map's tier | on | The map dropped matches the tier of the map the kill happened in |
| Tier outside a map | 1 | The campaign, hideouts, and maps when the setting above is off |
| Only these monsters | empty | Every monster; otherwise the variety id or name must contain one of these |

Values are per kill, on top of the server's own map roll.
