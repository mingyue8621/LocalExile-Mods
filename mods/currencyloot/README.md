# Currency Loot

Controls what currency a kill drops, item by item. It starts with **Chaos Orb and everything above it** — the ones a
player actually cares about — and each entry is **how many** every kill drops. A fraction is the chance of one more, so
`0.05` is a 5% chance of one and `2.5` is two plus a half.

| Setting | Default | |
|---|---|---|
| `mode` | `extra` | `extra` keeps the server's own drops and adds these beside them; `only` cancels the server's drops (equipment, gems, flasks and its own currency), so only these drop |
| `rarities` | rare, unique | which kills drop them; none chosen means every monster |
| `items` | Chaos 1, the rest small fractions | item name or metadata id → how many per kill (0 to 1000, fractions allowed) |
| `monsters` | empty | empty for every monster; otherwise the variety id or the name must contain one of these (case does not matter) |

Off by default: `/mod_enable currencyloot` in game, or Waypoint's Mods section.

## The defaults, from Chaos up

| Item | Every kill | 中文 |
|---|---|---|
| `Chaos Orb` | 1 | 混沌石 |
| `Orb of Annulment` | 0.05 | 剥离石 |
| `Exalted Orb` | 0.05 | 崇高石 |
| `Divine Orb` | 0.02 | 神圣石 |
| `Sacred Orb` | 0.02 | 圣洁石 |
| `Veiled Chaos Orb` | 0.01 | 隐匿混沌石 |
| `Ancient Orb` | 0.02 | 远古石 |
| `Awakened Sextant` | 0.02 | 觉醒六分仪 |
| `Fracturing Orb` | 0.01 | 破裂石 |
| `Hinekora's Lock` | 0.005 | 希内科拉之锁 |
| `Mirror of Kalandra` | 0.001 | 卡兰德魔镜 |

Any other item works too: the client's own name (`Orb of Alchemy`, `Gemcutter's Prism`, …) or a `BaseItemTypes` id
(`Metadata/Items/Currency/CurrencyDuplicate`). A wrong name is logged while loading:
`[mods] a mod's drop "…" is no item type or unique the server can create`.

## Examples

Only rare and unique monsters, and an Exalted Orb every second kill, nothing else:

```json
"rarities": ["rare", "unique"],
"items": { "Exalted Orb": 0.5 }
```

Every monster drops a Chaos Orb, and nothing else drops at all:

```json
"mode": "only",
"rarities": [],
"items": { "Chaos Orb": 1 }
```

## What it cannot do

It adds the items you list (or, with `mode: only`, replaces the whole roll with them). It does **not** re-weight the
server's own currency roll: to change how often the server itself picks each of its 24 core currencies, edit
`drop_currency_weights` in `world.db`.

Works with: LocalExile 0.8.83 and later (the `GameEvents.Drops` hook with `Items`, `Cancel` and the monster's rarity).
