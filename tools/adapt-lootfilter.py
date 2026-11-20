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

# 3.29 的类名映射：filter 里的写法 -> 3.29 的 ItemClasses.Name（空列表 = 3.29 没有这个类）
CLASS_MAP = {
    "Atlas Upgrade Item": ["Atlas Upgrade Items"],
    "Flasks": ["Life Flasks", "Mana Flasks", "Hybrid Flasks", "Utility Flasks"],
    "Gems": ["Skill Gems", "Support Gems"],
    "Heist Target": ["Heist Targets"],
    "Incursion Item": ["Incursion Items"],
    "Relic": ["Relics", "Large Relics", "Medium Relics", "Small Relics", "Sanctified Relics"],
    "Skill Gem": ["Skill Gems"],
    "Support Gem": ["Support Gems"],
    "Tincture": ["Tinctures"],
    "Trinket": ["Trinkets"],
    "Viscera": [],
}

# 动作行：判断一个块还剩没剩「条件」
ACTIONS = {
    "SetTextColor", "SetBorderColor", "SetBackgroundColor", "SetFontSize",
    "MinimapIcon", "PlayEffect", "PlayEffectColor",
    "CustomAlertSound", "CustomAlertSoundOptional",
    "DisableDropSound", "EnableDropSound", "Continue",
}

HEADER = [
    "# 由 lootfilter 模组按 3.29 客户端数据适配（Class 名、通配展开、3.30 新物品剔除）。",
]

# 第二行注释写原作者；第三个参数可以换掉（一乐那套就不是剑心）。
AUTHOR = "# 原作者：剑影流心  https://www.jxpoe.cn   —— 内容未改，只做版本换算。"


def load_client():
    db = sqlite3.connect(DB)
    names = [r[0] for r in db.execute("select Name from BaseItemTypes") if r and r[0]]
    classes = {r[0] for r in db.execute("select Name from ItemClasses") if r and r[0]}
    return names, classes


def expand(token, names):
    """作者的「不完整名」当通配用；这里按 3.29 的真实物品名展开。None = 不是通配写法。
    作者常把通配写成带空格的 `" Essence of "`、`"Contract: "`，所以先去掉首尾空格再比。"""
    key = token.strip()
    rules = [
        ("Astrolabe", lambda n: n.endswith(" Astrolabe")),
        ("of Rage", lambda n: n.endswith(" of Rage")),
        ("of Doubt", lambda n: n.endswith(" of Doubt")),
        ("Talisman", lambda n: n.endswith(" Talisman")),
        ("Awakened", lambda n: n.startswith("Awakened ")),
        # 精华：3.29 的八个等级都叫「<等级> Essence of X」，只按前缀 "Essence of" 找会漏掉大多数，
        # 所以按「含有」找（Whispering / Screaming / Deafening … Essence of Hatred 都算）。
        ("Essence of", lambda n: "Essence of " in n),
        ("Deafening Essence of", lambda n: n.startswith("Deafening Essence of ")),
        ("Shrieking Essence of", lambda n: n.startswith("Shrieking Essence of ")),
        ("Omen of", lambda n: n.startswith("Omen of ")),
        ("Allflame Ember of", lambda n: n.startswith("Allflame Ember of ")),
        ("Ducat of", lambda n: n.startswith("Ducat of ")),
        ("'s Ducat", lambda n: n.endswith("'s Ducat")),
        ("Enshrouding Crystal", lambda n: n.endswith(" Enshrouding Crystal")),
        ("Vaal", lambda n: n.startswith("Vaal ")),
        ("Coin", lambda n: n.startswith("Coin of ")),
        # 一乐那套还用了这几种：契约按「Contract: …」开头，灵柩按「Perfect …」开头，
        # 活动通货按「Stone of …」开头。
        ("Contract:", lambda n: n.startswith("Contract:")),
        ("Perfect", lambda n: n.startswith("Perfect ")),
        # 作者把 Imperfect 写成了 ImPerfect（3.29 里是 Imperfect Hydra 这种写法）。
        ("ImPerfect", lambda n: n.startswith("Imperfect ")),
        ("Stone of", lambda n: n.startswith("Stone of ")),
        ("Shard", lambda n: n.endswith(" Shard")),
    ]
    for rule, test in rules:
        if key == rule:
            return [n for n in names if test(n)]
    # 大小写写错的名字（"large Cluster Jewel"）：3.29 里只有一种写法时按它改回来。
    folded = [n for n in names if n.lower() == key.lower()]
    if len(folded) == 1:
        return folded
    return None


def rewrite_line(line, names, classes, report):
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


def adapt(path, target, names, classes, report):
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
        new_body, dropped_by_value = [], False
        # 原块是不是按「具体物品」写的：是的话，物品名被删空就意味着这一块没意义了，
        # 剩下的 Class 会变成「匹配所有该类物品」，把整类都染成同一颜色。
        had_items = any(re.match(r"^\s*BaseType\b", line) for line in body)
        kept_items = False
        for line in body:
            new = rewrite_line(line, names, classes, report)
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


def main():
    global AUTHOR
    src, dst = sys.argv[1], sys.argv[2]
    if len(sys.argv) > 3:
        AUTHOR = sys.argv[3]
    names, classes = load_client()
    os.makedirs(dst, exist_ok=True)
    totals = {"files": 0, "class_fixed": 0, "class_dropped": [], "item_dropped": [],
              "expanded": {}, "blocks_dropped": []}
    for name in sorted(os.listdir(src)):
        if not name.endswith(".filter"):
            continue
        report = {"class_fixed": 0, "class_dropped": [], "item_dropped": [],
                  "expanded": {}, "blocks_dropped": []}
        adapt(os.path.join(src, name), os.path.join(dst, name), names, classes, report)
        totals["files"] += 1
        totals["class_fixed"] += report["class_fixed"]
        for key in ("class_dropped", "item_dropped", "blocks_dropped"):
            totals[key] += report[key]
        for key, value in report["expanded"].items():
            totals["expanded"].setdefault(key, value)
        print(f"{name}: Class 修正 {report['class_fixed']} 处，"
              f"物品名展开 {len(report['expanded'])} 种，删名字 {len(report['item_dropped'])} 个，"
              f"删整块 {len(report['blocks_dropped'])} 个")
    print("\n合计:", totals["files"], "个文件")
    print("  Class 修正:", totals["class_fixed"])
    print("  展开:", totals["expanded"])
    print("  删掉的物品名:", sorted(set(totals["item_dropped"])))
    print("  删掉的块:", sorted(set(totals["blocks_dropped"])))


if __name__ == "__main__":
    main()
