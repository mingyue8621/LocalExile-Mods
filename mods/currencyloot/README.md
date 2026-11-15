# 控制通货

逐项控制每次击杀掉什么通货。默认从**混沌石往上**开始 —— 也就是玩家真正在乎的那些 ——
每一项填的是**每只怪掉几个**。小数表示再多掉一个的概率：`0.05` 是 5% 掉一个，`2.5` 是两个再加半个。

| 设置项 | 默认值 | 说明 |
|---|---|---|
| `mode`（掉落模式） | `extra` | `extra`：保留服务端自己的掉落，这些额外再掉；`only`：取消服务端掉落（装备、宝石、药剂以及它自己那套通货），只掉下面这些 |
| `rarities`（怪物稀有度） | 稀有、传奇 | 哪些击杀会掉；一个都不勾表示所有怪物 |
| `items`（每次击杀掉落数） | 混沌 1，其余是很小的概率 | 物品名或 metadata id → 每次击杀掉几个（0 到 1000，可以带小数） |
| `chaosAct`（混沌石从第几章开始掉） | 6 | 列表里的混沌石从这一章起才额外掉 |
| `highAct`（其他通货从第几章开始掉） | 8 | 剥离石、崇高石、神圣石、魔镜这些，从这一章起才掉；地图里不受限 |
| `monsters`（仅限这些怪物） | 空 | 留空表示所有怪物；否则种类 id 或名字要包含其中一项（不区分大小写） |

默认关闭：游戏里 `/mod_enable currencyloot`，或启动器的模组页面打开。

## 按章节限制

为了不让新角色一出门就掉崇高石、魔镜，列表里的通货按章节放行（和正式服后期才见高等通货一致）：

- **混沌石**：默认第六章起（`chaosAct`）
- **列表里其他通货**：默认第八章起（`highAct`）
- **没列进 items 的基础通货**：不受这个模组影响，服务端本来怎么掉就怎么掉
- **异界地图里**：一律不受章节限制

第一到第五章只会掉服务端自己那套基础通货；第六、七章多出混沌石；第八章之后才是完整列表。

## 默认配置（从混沌石往上）

| 物品 | 每只怪 | 中文名 |
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

别的物品也能填：客户端里的英文名（`Orb of Alchemy`、`Gemcutter's Prism` ……）或者 `BaseItemTypes` 的 id
（`Metadata/Items/Currency/CurrencyDuplicate`）。名字写错时加载日志会有一行：
`[mods] a mod's drop "…" is no item type or unique the server can create`。

## 例子

只要稀有和传奇怪，每两只有一只掉一个崇高石，别的都不掉：

```json
"rarities": ["rare", "unique"],
"items": { "Exalted Orb": 0.5 }
```

每只怪都掉一个混沌石，其余完全不掉：

```json
"mode": "only",
"rarities": [],
"items": { "Chaos Orb": 1 }
```

## 它做不到的事

它只是额外塞入你列出的物品（`mode: only` 时则整轮替换成这些）。它**不会**改服务端自己那套通货权重的比例：
要让服务端自己的 24 种核心通货掉得更频繁或更少，得改 `world.db` 里的 `drop_currency_weights`。

适用版本：LocalExile 0.8.83 及以后（`GameEvents.Drops` 钩子，带 `Items`、`Cancel` 和怪物稀有度）。
