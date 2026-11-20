"""部署前自检：把 filters 目录下所有 .filter 里的 Class / BaseType 拿客户端数据核一遍。

客户端解析过滤器是「一条不合就整份读取失败」，报错长这样：
    读取物品过滤失败。原因:行 3495: 规则 BaseType 无法解析的参数: 未找到匹配 "Growing Wombgift" 的基础类型
所以每次打包/改过滤器前跑这个，比进游戏踩雷快得多。

能查出来的三类问题：
1. Class 名不存在（老版单数写法漏改：Amulet / Bow / Flask …）。
2. BaseType 名不存在（3.30 新物品、作者笔误、名字写断）。
3. BaseType 名存在但挂在空类下（RemovedItem 之类），客户端同样认不出来
   —— 实例：Growing Wombgift，同一组里另外四个 Wombgift 是好的。

用法：python check-lootfilter.py [filters 目录，默认 mods/lootfilter/filters]
"""
import os
import re
import sqlite3
import sys

DB = r"G:/POE1.1/data/client.db"


def load_client():
    db = sqlite3.connect(DB)
    class_names = {r[0]: (r[1] or "") for r in db.execute("select _row, Name from ItemClasses")}
    classes = {name.strip() for name in class_names.values() if name.strip()}
    names, retired = set(), set()
    for name, class_row in db.execute("select Name, ItemClassesKey from BaseItemTypes"):
        if not name:
            continue
        (names if class_names.get(class_row, "").strip() else retired).add(name)
    return names, classes, retired


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join("mods", "lootfilter", "filters")
    names, classes, retired = load_client()
    problems = 0
    for sub in sorted(os.listdir(root)):
        folder = os.path.join(root, sub)
        if not os.path.isdir(folder):
            continue
        bad_class, bad_item, dead_item = {}, {}, {}
        files = [f for f in sorted(os.listdir(folder)) if f.endswith(".filter")]
        for name in files:
            with open(os.path.join(folder, name), encoding="utf-8", errors="replace") as handle:
                for number, line in enumerate(handle, 1):
                    match = re.match(r"^\s*(Class|BaseType)\s+(.+?)\s*$", line)
                    if not match:
                        continue
                    keyword, values = match.group(1), re.findall(r'"([^"]*)"', match.group(2))
                    if keyword == "Class":
                        bucket = bad_class
                        valid = classes
                    else:
                        bucket = bad_item
                        valid = names
                    for value in values:
                        if value in valid:
                            continue
                        if keyword == "BaseType" and value in retired:
                            dead_item.setdefault(value, []).append((name, number))
                        else:
                            bucket.setdefault(value, []).append((name, number))
        total = len(bad_class) + len(bad_item) + len(dead_item)
        problems += total
        print(f"=== {sub}：{len(files)} 个文件，问题 {total} 处 ===")
        for value, hits in sorted(bad_class.items()):
            print(f"  类名不存在   {value}  ← {hits[0][0]} 第 {hits[0][1]} 行（共 {len(hits)} 处）")
        for value, hits in sorted(bad_item.items()):
            print(f"  物品名不存在 {value}  ← {hits[0][0]} 第 {hits[0][1]} 行（共 {len(hits)} 处）")
        for value, hits in sorted(dead_item.items()):
            print(f"  物品已退役   {value}  ← {hits[0][0]} 第 {hits[0][1]} 行（共 {len(hits)} 处）")
    if problems:
        print(f"\n合计 {problems} 处会让客户端报「读取物品过滤失败」，先跑 adapt-lootfilter.py 修掉。")
        sys.exit(1)
    print("\n全部通过：Class / BaseType 都能被客户端解析。")


if __name__ == "__main__":
    main()
