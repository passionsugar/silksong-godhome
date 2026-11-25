"""Render the boss table for docs/boss-inventory.md from data/bosses.json.
Usage: python -I inventory_table.py <bosses.json>
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
doc = json.load(open(sys.argv[1], encoding="utf-8"))
tiers = {"boss": "Boss", "mini": "小 Boss", "spar": "切磋"}
for act in (1, 2, 3):
    print(f"\n### 第{'一二三'[act - 1]}幕（推定）\n")
    print("| id | 名称 | 英文名 | 类型 | 地区 | 场景 | 入场门 door_dreamEnter | 血量 | 击败标记 |")
    print("|---|---|---|---|---|---|---|---|---|")
    for b in doc["bosses"]:
        if b["act"] != act:
            continue
        name = b["name"]["zh"] + (f"（{b['variant']['zh']}）" if b.get("variant") else "")
        en = b["name"]["en"] + (f" ({b['variant']['en']})" if b.get("variant") else "")
        scene = b["scene"] + (f"<br>← {b['hostScene']}" if b.get("hostScene") else "")
        hp = " / ".join(f"{v}" for v in b["hp"].values() if v) or "?"
        door = (b.get("arena") or {}).get("door")
        entry = f"{b['entry']['scene']} @ ({door[0]}, {door[1]})" if door else f"{b['entry']['scene']} @ 待定"
        flags = ", ".join(b["flags"]["defeated"]) or "待查"
        print(f"| `{b['id']}` | {name} | {en} | {tiers[b['tier']]} | {b['region']['zh']} | `{scene}` | `{entry}` | {hp} | `{flags}` |")
