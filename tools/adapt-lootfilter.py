"""把 3.30 口径的过滤器（剑心 S31、一乐 S31 都算）改成当前客户端的 3.29 口径。

做三件事，数据全部取自 G:/POE1.1/data/client.db：
1. Class 名换成 3.29 的 ItemClasses.Name（`Flasks` → 四个具体药水类、`Support Gem` → `Support Gems` …）。
2. 作者用「不完整名」当通配的地方（`" of Rage"`、`"Essence of"`、`"Awakened "`、`"Contract: "` …）展开成 3.29 里真实存在的物品名。
3. 3.29 里根本不存在的物品名（丝线、灵魂核心等 3.30 新内容）删掉；删完一个块里既没有 Class 也没有 BaseType 的，整块删掉
   —— 否则它会变成「匹配所有物品」，把所有东西都染成同一个颜色。

用法：python adapt-lootfilter.py <源目录> <目标目录> [原作者注释行]
"""
import os
import re
import shutil
import sqlite3
import sys

DB = r"G:/POE1.1/data/client.db"

# 3.29 的类名映射：filter 里的写法 -> 3.29 的 ItemClasses.Name（空列表 = 3.29 没有这个类）。
# 文子那套大量用老版的单数写法（Amulet / Belt / Bow），3.29 的 ItemClasses 一律是复数。
FLASKS = ["Life Flasks", "Mana Flasks", "Hybrid Flasks", "Utility Flasks", "Critical Utility Flasks"]
ONE_HAND = ["One Hand Axes", "One Hand Maces", "One Hand Swords", "Thrusting One Hand Swords"]
TWO_HAND = ["Two Hand Axes", "Two Hand Maces", "Two Hand Swords"]
RELICS = ["Relics", "Large Relics", "Medium Relics", "Small Relics", "Sanctified Relics"]
CLASS_MAP = {
    "Abyss Jewel": ["Abyss Jewels"],
    "Amulet": ["Amulets"],
    "Atlas Upgrade Item": ["Atlas Upgrade Items"],
    "Belt": ["Belts"],
    "Blueprint": ["Blueprints"],
    "Body Armour": ["Body Armours"],
    "Bow": ["Bows"],
    "Breachstone": ["Breachstones"],
    "Claw": ["Claws"],
    "Contract": ["Contracts"],
    "Dagger": ["Daggers"],
    "Expedition Logbook": ["Expedition Logbooks"],
    "Flask": FLASKS,
    "Flasks": FLASKS,
    "Gems": ["Skill Gems", "Support Gems"],
    "Heist Brooch": ["Heist Brooches"],
    "Heist Cloak": ["Heist Cloaks"],
    "Heist Target": ["Heist Targets"],
    "Heist Tool": ["Heist Tools"],
    "Helmet": ["Helmets"],
    "Idol": ["Idols"],
    "Incursion Item": ["Incursion Items"],
    "Jewel": ["Jewels"],
    "Labyrinth Item": ["Labyrinth Items"],
    "Labyrinth Map Item": ["Labyrinth Map Items"],
    "Labyrinth Trinket": ["Labyrinth Trinkets"],
    "Map Fragment": ["Map Fragments"],
    "One Hand": ONE_HAND,
    "Piece": ["Pieces"],
    "Quiver": ["Quivers"],
    "Relic": RELICS,
    "Sceptre": ["Sceptres"],
    "Sentinel Drone": ["Sentinels"],
    "Shield": ["Shields"],
    "Skill Gem": ["Skill Gems"],
    "Support Gem": ["Support Gems"],
    "Tincture": ["Tinctures"],
    "Trinket": ["Trinkets"],
    "Two Hand": TWO_HAND,
    "Two Hand Axe": ["Two Hand Axes"],
    "Viscera": [],
    "Wand": ["Wands"],
}

# 动作行：判断一个块还剩没剩「条件」
ACTIONS = {
    "Show", "Hide",
    "SetTextColor", "SetBorderColor", "SetBackgroundColor", "SetFontSize",
    "MinimapIcon", "PlayEffect", "PlayEffectColor",
    "CustomAlertSound", "CustomAlertSoundOptional",
    "DisableDropSound", "EnableDropSound", "Continue",
}

# 3.29 客户端认识的块内条件关键字。不在这张表里的一律当「这个版本没有的条件」，
# 整块删掉——只删那一行会把条件放宽成「匹配所有物品」，比删块更糟。
# 依据：报错前能跑通的关键字 + 逐个在客户端 exe 的字符串表里核过。
# 已知 3.30 才有的：Fated（命运机制）。3.29 没有命运物品，这个块本来也匹配不到东西。
CONDITIONS = {
    "ItemLevel", "DropLevel", "Quality", "Rarity", "Class", "BaseType", "SocketGroup",
    "Sockets", "LinkedSockets", "Width", "Height", "StackSize",
    "AreaLevel", "GemLevel", "MapTier", "HasExplicitMod", "AnyEnchantment", "HasEnchantment",
    "Corrupted", "Mirrored", "Identified", "CorruptedMods",
    "ElderItem", "ShaperItem", "CrusaderItem", "RedeemerItem", "HunterItem", "WarlordItem",
    "SynthesisedItem", "FracturedItem", "HasInfluence", "Scourged", "Replica", "AlternateQuality",
    "BlightedMap", "UberBlightedMap", "MirageMap", "ZanaMemory", "Foulborn", "Vestigial",
    "BaseDefencePercentile", "BaseArmour", "BaseEnergyShield", "BaseEvasion", "BaseWard",
    "MemoryStrands", "UnidentifiedItemTier", "EnchantmentPassiveNum", "EnchantmentPassiveNode",
    "TransfiguredGem", "HasSearingExarchImplicit", "HasEaterOfWorldsImplicit",
    # 剑心那套用了这三个，3.29 客户端在跑（exe 字符串表里也有），别误判成未知：
    "HasImplicitMod", "HasCruciblePassiveTree", "Imbued",
    # 文子那套的老条件：朔界 / 长老地图。客户端字符串表里 ShapedMap、ElderMap 紧挨着
    # ItemLevel、Mirrored（同一张条件关键字表），说明 3.29 的解析器仍然认识，保留这些块。
    "ShapedMap", "ElderMap",
}
KNOWN = ACTIONS | CONDITIONS

HEADER = [
    "# 由 lootfilter 模组按 3.29 客户端数据适配（Class 名、通配展开、3.30 新物品剔除）。",
]

# 第二行注释写原作者；第三个参数可以换掉（一乐那套就不是剑心）。
AUTHOR = "# 原作者：剑影流心  https://www.jxpoe.cn   —— 内容未改，只做版本换算。"


# 客户端过滤器**不认**的类（写进去就报「未找到匹配 X 的基础类型」）：
# - 类名是空的：RemovedItem / GhostItem / DONOTUSE* / GiftBox / Unarmed —— 退役占位类；
# - 'Hidden Items'：内部隐藏物品（Random Minor Idol、Random Bow、Hidden Item …）；
# - 'Instance Local Items'：只在实例内存在的本地物品（不可掉进地面）。
# 这三类正好是「直接继承 Metadata/Items/Item、没有专用基类」的 96 个占位物品的全部归宿
# （56 + 22 + 18），客户端不给它们分配过滤器条目。
# 佐证：文子「全部雕像」块里写了 6 个 Random XXX Idol，报
# 「行 3512: 未找到匹配 Random Minor Idol 的基础类型」。
# 这些物品在 client.db 的 BaseItemTypes 里是**存在**的，光看名字查得到不算数，得看类。
DEAD_CLASS_NAMES = {"Hidden Items", "Instance Local Items"}
# 同样不会出现在物品栏、但暂时没证据说客户端不认的类：命中只提示，不删。
SUSPECT_CLASS_NAMES = {"Microtransactions", "Hideout Doodads", "Archnemesis Mods"}


def load_client():
    """返回 (客户端认得的基础类型名, 类名集合, 客户端不认的物品名)。

    客户端解析过滤器时，名字得能在「过滤可用的基础类型」名单里查到：BaseItemTypes 里挂在
    空类或 Hidden Items 类下面的物品不在名单里，写进过滤就报
    「行 N: 规则 BaseType 无法解析的参数: 未找到匹配 X 的基础类型」，整份过滤器都用不了。
    实例：Growing Wombgift 挂在 RemovedItem 下（同组另外四个 Wombgift 挂在 Wombgifts 下，是好的），
    Random Minor Idol 挂在 Hidden Items 下（同块里其他 Idol 挂在 Idols 下，也是好的）——
    所以只删这几个名字，别把整组都删了。
    """
    db = sqlite3.connect(DB)
    class_names = {r[0]: (r[1] or "") for r in db.execute("select _row, Name from ItemClasses")}
    classes = {name.strip() for name in class_names.values() if name.strip()}
    names, retired = [], set()
    for name, class_row in db.execute("select Name, ItemClassesKey from BaseItemTypes"):
        if not name:
            continue
        class_name = class_names.get(class_row, "").strip()
        if class_name and class_name not in DEAD_CLASS_NAMES:
            names.append(name)
        else:
            retired.add(name)
    return names, classes, retired, class_names


def expand(token, names):
    """作者的「不完整名」当通配用；这里按 3.29 的真实物品名展开。None = 不是通配写法。
    作者常把通配写成带空格的 `" Essence of "`、`" Charm"`、`"Contract: "`，所以先去掉首尾空格再比。"""
    key = token.strip()
    # 地图类写的是短名（"Citadel" 想要 "Citadel Map"，而不是 "Citadel Bow"），精确拼出来最稳。
    if key + " Map" in names:
        return [key + " Map"]
    rules = [
        ("Astrolabe", lambda n: n.endswith(" Astrolabe")),
        ("of Rage", lambda n: n.endswith(" of Rage")),
        ("of Doubt", lambda n: n.endswith(" of Doubt")),
        ("Talisman", lambda n: n.endswith(" Talisman")),
        ("Awakened", lambda n: n.startswith("Awakened ")),
        ("Omen of", lambda n: n.startswith("Omen of ")),
        ("Allflame Ember of", lambda n: n.startswith("Allflame Ember of ")),
        ("Ducat of", lambda n: n.startswith("Ducat of ")),
        ("'s Ducat", lambda n: n.endswith("'s Ducat")),
        ("Enshrouding Crystal", lambda n: n.endswith(" Enshrouding Crystal")),
        ("Vaal", lambda n: n.startswith("Vaal ")),
        ("Coin", lambda n: n.startswith("Coin of ")),
        ("Coin of", lambda n: n.startswith("Coin of ")),
        ("Stone of", lambda n: n.startswith("Stone of ")),
        ("Shard", lambda n: n.endswith(" Shard")),
        # --- 文子那套（S30，老版写法）用的 ---
        ("Breachstone", lambda n: n.endswith(" Breachstone")),
        ("Flawless Breachstone", lambda n: n.endswith(" Flawless Breachstone")),
        ("Charm", lambda n: n.endswith(" Charm")),
        ("Emblem", lambda n: n.endswith(" Emblem")),
        ("Idol", lambda n: n.endswith(" Idol")),
        ("Tincture", lambda n: n.endswith(" Tincture")),
        ("Wombgift", lambda n: n.endswith(" Wombgift")),
        ("Recombinator", lambda n: n.endswith(" Recombinator")),
        ("Apex Sentinel", lambda n: n.endswith(" Apex Sentinel")),
        ("Pandemonium Sentinel", lambda n: n.endswith(" Pandemonium Sentinel")),
        ("Stalker Sentinel", lambda n: n.endswith(" Stalker Sentinel")),
        ("'s Exalted Orb", lambda n: n.endswith("'s Exalted Orb")),
        ("Cartographer's Seal", lambda n: n.endswith("Cartographer's Seal")),
        ("Maraketh Splinter", lambda n: n.endswith(" Maraketh Splinter")),
        ("Scarab", lambda n: n.endswith(" Scarab")),
        ("Contract", lambda n: n.startswith("Contract:")),
        ("Lycia's Invocation", lambda n: n.startswith("Lycia's Invocation")),
        ("Sacrifice at", lambda n: n.startswith("Sacrifice at ")),
        ("Tattoo of", lambda n: n.startswith("Tattoo of ")),
        ("Vial of", lambda n: n.startswith("Vial of ")),
        ("Runegraft of", lambda n: n.startswith("Runegraft of ")),
        # 精华：3.29 的八个等级都叫「<等级> Essence of X」，文子按等级各写一条，一乐只写 "Essence of"。
        ("Essence of", lambda n: "Essence of " in n),
        ("Deafening Essence of", lambda n: n.startswith("Deafening Essence of ")),
        ("Shrieking Essence of", lambda n: n.startswith("Shrieking Essence of ")),
        ("Perfect", lambda n: n.startswith("Perfect ")),
        # 作者把 Imperfect 写成了 ImPerfect（3.29 里是 Imperfect Hydra 这种写法）。
        ("ImPerfect", lambda n: n.startswith("Imperfect ")),
    ]
    for rule, test in rules:
        if key == rule:
            return [n for n in names if test(n)]
    # 按等级开头的精华（"Muttering Essence of" 之类）：命中就按前缀展开。
    if "Essence of" in key:
        return [n for n in names if n.startswith(key)]
    # 名字被写断了（"Perfect Druidic Alchemis"）：只有唯一一个以它开头时才认。
    if len(key) >= 10:
        prefix = [n for n in names if n.lower().startswith(key.lower())]
        if len(prefix) == 1:
            return prefix
    # 大小写写错的名字（"large Cluster Jewel"）：3.29 里只有一种写法时按它改回来。
    folded = [n for n in names if n.lower() == key.lower()]
    if len(folded) == 1:
        return folded
    return None


def rewrite_line(line, names, classes, retired, report):
    """一行 Class / BaseType 的改写；返回新行（可能为 None = 这一行没条件了）。"""
    match = re.match(r"^(\s*)(Class|BaseType)\s+(.+?)\s*$", line)
    if not match:
        return line
    indent, keyword, rest = match.group(1), match.group(2), match.group(3)
    values = re.findall(r'"([^"]*)"', rest)
    if not values:
        return line
    out = []
    for value in values:
        if keyword == "Class":
            mapped = CLASS_MAP.get(value)
            if mapped is None:
                out.append(value)
            elif not mapped:
                report["class_dropped"].append(value)
            else:
                report["class_fixed"] += 1
                out += mapped
            continue
        # BaseType：先按真名（去首尾空格）认，认不出才当通配展开——否则每个真名都会被「大小写还原」
        # 当成一次展开，报告里看不出哪些才是真的通配。
        stripped = value.strip()
        if stripped in retired:
            # 客户端认不出来的退役物品：留着会让整个过滤器读不出来。
            report["item_retired"].append(stripped)
            continue
        if stripped in names:
            out.append(stripped)
            continue
        bucket = expand(value, names)
        if bucket is not None:
            report["expanded"][value] = len(bucket)
            out += bucket
        else:
            report["item_dropped"].append(value)
    seen, uniq = set(), []
    for value in out:
        if value not in seen:
            seen.add(value)
            uniq.append(value)
    return f'{indent}{keyword} ' + " ".join(f'"{v}"' for v in uniq) if uniq else None


def blocks(lines):
    """按 Show/Hide 切块；返回 [(head_text, body_lines)]，第一段是文件头。"""
    starts = [i for i, l in enumerate(lines) if re.match(r"^\s*(Show|Hide)\b", l)]
    if not starts:
        return [("", lines)]
    parts = []
    if starts[0] > 0:
        parts.append(("", lines[:starts[0]]))
    for n, start in enumerate(starts):
        end = starts[n + 1] if n + 1 < len(starts) else len(lines)
        parts.append((lines[start], lines[start + 1:end]))
    return parts


def conditions(body):
    for line in body:
        text = line.strip()
        if not text or text.startswith("#"):
            continue
        head = text.split()[0].rstrip(":")
        if head in ACTIONS:
            continue
        return True
    return False


def adapt(path, target, names, classes, retired, report):
    text = open(path, encoding="utf-8").read().replace("\r\n", "\n")
    lines = text.split("\n")
    parts = blocks(lines)
    output = [parts[0][0]] if parts[0][0] else []
    head = parts[0][1]
    # 文件头：保留原注释，插到第一条注释之后
    if head:
        output += HEADER + [AUTHOR, ""] + head
    else:
        output += HEADER + [AUTHOR, ""]
    for show, body in parts[1:]:
        # 客户端遇到不认识的块内关键字会直接判「读取物品过滤失败」，整个过滤器都用不了，
        # 所以这种块必须整块删掉（删单行会把条件放宽成匹配全部物品）。
        unknown = []
        for line in body:
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            head = text.split()[0]
            if head not in KNOWN and head not in unknown:
                unknown.append(head)
        if unknown:
            report["blocks_unknown"].append((show.strip(), unknown))
            continue
        new_body, dropped_by_value = [], False
        # 原块是不是按「具体物品」写的：是的话，物品名被删空就意味着这一块没意义了，
        # 剩下的 Class 会变成「匹配所有该类物品」，把整类都染成同一颜色。
        had_items = any(re.match(r"^\s*BaseType\b", line) for line in body)
        kept_items = False
        for line in body:
            new = rewrite_line(line, names, classes, retired, report)
            if new is None:
                dropped_by_value = True
                continue
            if re.match(r"^\s*BaseType\b", new):
                kept_items = True
            new_body.append(new)
        if dropped_by_value and (had_items and not kept_items or not conditions(new_body)):
            report["blocks_dropped"].append(show.strip())
            continue
        output.append(show)
        output += new_body
    open(target, "w", encoding="utf-8", newline="\r\n").write("\n".join(output))
    verify(target, names, classes)


def verify(target, names, classes):
    """把写出去的文件再读回来核一遍：客户端解析不了的名字一个都不许留，
    否则进游戏就是「读取物品过滤失败」，整份过滤器都用不了。"""
    name_set, class_set = set(names), set(classes)
    bad_items, bad_classes = [], []
    for line in open(target, encoding="utf-8"):
        match = re.match(r"^\s*(Class|BaseType)\s+(.+?)\s*$", line)
        if not match:
            continue
        values = re.findall(r'"([^"]*)"', match.group(2))
        if match.group(1) == "Class":
            bad_classes += [v for v in values if v not in class_set]
        else:
            bad_items += [v for v in values if v not in name_set]
    if bad_items or bad_classes:
        raise SystemExit(f"{os.path.basename(target)}: 还有客户端认不出的名字 "
                         f"Class={sorted(set(bad_classes))[:5]} BaseType={sorted(set(bad_items))[:5]}")


def main():
    global AUTHOR
    src, dst = sys.argv[1], sys.argv[2]
    if len(sys.argv) > 3:
        AUTHOR = sys.argv[3]
    names, classes, retired, class_names = load_client()
    if retired:
        print(f"客户端认不出来的物品 {len(retired)} 个（挂在空类 / Hidden Items / Instance Local Items 下），"
              f"写进过滤器会报「无法解析的参数」：")
        print("  " + "、".join(sorted(retired)))
        print()
    os.makedirs(dst, exist_ok=True)
    totals = {"files": 0, "class_fixed": 0, "class_dropped": [], "item_dropped": [],
              "item_retired": [], "expanded": {}, "blocks_dropped": [], "blocks_unknown": []}
    for name in sorted(os.listdir(src)):
        if not name.endswith(".filter"):
            continue
        report = {"class_fixed": 0, "class_dropped": [], "item_dropped": [], "item_retired": [],
                  "expanded": {}, "blocks_dropped": [], "blocks_unknown": []}
        adapt(os.path.join(src, name), os.path.join(dst, name), names, classes, retired, report)
        totals["files"] += 1
        totals["class_fixed"] += report["class_fixed"]
        for key in ("class_dropped", "item_dropped", "item_retired", "blocks_dropped"):
            totals[key] += report[key]
        totals["blocks_unknown"] += report["blocks_unknown"]
        for key, value in report["expanded"].items():
            totals["expanded"].setdefault(key, value)
        print(f"{name}: Class 修正 {report['class_fixed']} 处，"
              f"物品名展开 {len(report['expanded'])} 种，删名字 {len(report['item_dropped'])} 个，"
              f"删退役物品 {len(report['item_retired'])} 个，"
              f"删整块 {len(report['blocks_dropped'])} 个，"
              f"未知条件块 {len(report['blocks_unknown'])} 个")
    print("\n合计:", totals["files"], "个文件")
    print("  Class 修正:", totals["class_fixed"])
    print("  展开:", totals["expanded"])
    print("  删掉的物品名:", sorted(set(totals["item_dropped"])))
    print("  删掉的退役物品:", sorted(set(totals["item_retired"])))
    print("  删掉的块:", sorted(set(totals["blocks_dropped"])))
    for show, keys in totals["blocks_unknown"]:
        print(f"  未知条件块（{','.join(keys)}）: {show}")


if __name__ == "__main__":
    main()
