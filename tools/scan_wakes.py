"""[Godhome] 离线工具：扫描全部 Boss 场景，列出每个 Boss 是怎么开战的（给 bosses.json 的 arena.wake / arena.patch 出草稿，运行时不用）。

输入：tools/dump_fsm.py 导出的场景文本（一个场景一个 .txt）和 data/bosses.json。
输出：Markdown 报告，每个 Boss 一节：
  - 战斗范围（Boss 物体所在的根）里“等玩家进入”的触发器：FSM 动作、会发 FSM 事件的脚本、BattleScene；
  - Boss 的 FSM 有没有“重打”分支（测 encountered* 的状态）；
  - 范围里有没有 NPC 对话（PlayMakerNPC / Dialogue FSM）。
另外统计：所有场景里，挂在触发器物体上、字段里引用了 PlayMakerFSM 的脚本类型（找“进入就发事件”的脚本）。

用法：python -I scan_wakes.py <dump 目录> <bosses.json> <输出.md>
"""
import json
import os
import re
import sys
from collections import Counter, defaultdict

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dumpfmt import action_name, parse_fsm_head  # noqa: E402

WORLD ={"Black Thread States", "Black Thread States Thread Only Variant", "Area_States"}
TRIGGER_ACTIONS = ("Trigger2dEvent", "SendTrigger2DEvent", "CheckAlertRange", "CheckAlertRangeByName",
                   "CheckTrackTriggerCount", "TriggerEnterEventSubscribe")

OBJ_RE = re.compile(r"^  (\S.*?)  @\(([-\d.]+),([-\d.]+)\)(.*?)\{(.*)\}\s*$")


def root_for(path):
    parts = path.split("/")
    n = min(3, len(parts)) if parts[0] in WORLD else 1
    return "/".join(parts[:n])


def parse(path):
    objs = {}       # path -> dict(pos, flags, comps, details[])
    fsms = []       # dict(path, name, start, flags, states{name: {"tr": [(ev, to)], "acts": [str]}}, globals[])
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    section = None
    cur_obj = None
    cur_fsm = None
    cur_state = None
    for no, ln in enumerate(lines, 1):
        if ln.startswith("## "):
            section = "fsm" if ln.startswith("## FSM") else "obj"
            continue
        if section == "obj":
            m = OBJ_RE.match(ln)
            if m:
                p = m.group(1)
                comps = [c.strip() for c in m.group(5).split(",")]
                cur_obj = objs[p] = {"pos": (float(m.group(2)), float(m.group(3))), "flags": m.group(4).strip(),
                                     "comps": comps, "details": []}
            elif cur_obj is not None and ln.startswith("      "):
                cur_obj["details"].append(ln.strip())
        elif section == "fsm":
            # 标题行读不懂直接报错（dumpfmt），不当成没有这个 FSM。
            head = parse_fsm_head(ln, f"{path}:{no}")
            if head:
                fpath, fname, fstart, fflags = head
                cur_fsm = {"path": fpath, "name": fname, "start": fstart, "flags": fflags,
                           "states": {}, "globals": []}
                fsms.append(cur_fsm)
                cur_state = None
                continue
            if cur_fsm is None:
                continue
            s = ln.strip()
            if ln.startswith("  global:"):
                cur_fsm["globals"].append(s[len("global:"):].strip())
            elif re.match(r"^  \[.*\]$", ln):
                cur_state = cur_fsm["states"].setdefault(s[1:-1], {"tr": [], "acts": []})
            elif cur_state is not None and s.startswith("->"):
                mm = re.match(r"->\s*(.*?)\s*=>\s*(.*)$", s)
                if mm:
                    cur_state["tr"].append((mm.group(1), mm.group(2)))
            elif cur_state is not None and ln.startswith("      "):
                cur_state["acts"].append(s)
    return objs, fsms


def in_scope(path, roots):
    return any(path == r or path.startswith(r + "/") for r in roots)


def main():
    dump_dir, bosses_path, out_path = sys.argv[1:4]
    bosses = json.load(open(bosses_path, encoding="utf-8"))["bosses"]
    cache = {}

    def load(scene):
        if scene not in cache:
            p = os.path.join(dump_dir, scene + ".txt")
            cache[scene] = parse(p) if os.path.exists(p) else ({}, [])
        return cache[scene]

    # 1. 统计：挂在触发器物体上、字段里引用了 PlayMakerFSM 的脚本类型
    census = Counter()
    examples = defaultdict(list)
    for fn in sorted(os.listdir(dump_dir)):
        if not fn.endswith(".txt"):
            continue
        objs, _ = load(fn[:-4])
        for p, o in objs.items():
            if not any("(trigger)" in c for c in o["comps"]):
                continue
            for d in o["details"]:
                t = d.split(":", 1)[0]
                if "<PlayMakerFSM>" in d and t not in ("PlayMakerTriggerEnter2D", "PlayMakerTriggerStay2D",
                                                       "PlayMakerTriggerExit2D", "PlayMakerCollisionEnter2D",
                                                       "PlayMakerCollisionExit2D", "PlayMakerFixedUpdate",
                                                       "PlayMakerCollisionStay2D", "EventRegister"):
                    census[t] += 1
                    if len(examples[t]) < 2:
                        examples[t].append(f"{fn[:-4]}: {p} — {d[:160]}")

    out = ["# 全部 Boss 的开战方式（静态扫描，给 arena 段出草稿）", ""]
    out.append("## 触发器物体上、会给 FSM 发事件的脚本类型（全部场景）")
    out.append("")
    for t, n in census.most_common():
        out.append(f"- `{t}` × {n}")
        for e in examples[t]:
            out.append(f"  - {e}")
    out.append("")

    # 2. 每个 Boss
    summary = []
    for b in bosses:
        scenes = [s for s in [b.get("scene"), b.get("hostScene")] if s]
        names = set(b.get("bossObjects") or [])
        objs, fsms = {}, []
        for s in scenes:
            o, f = load(s)
            objs.update(o)
            fsms.extend(f)
        boss_paths = [p for p in objs if p.split("/")[-1].replace("(Clone)", "").strip() in names]
        roots = sorted({root_for(p) for p in boss_paths})
        enc = (b.get("flags") or {}).get("encountered") or []
        out.append(f"## {b['id']}（{b['name']['zh']}）— {', '.join(scenes)}")
        out.append("")
        out.append(f"- Boss 物体：{', '.join(boss_paths) or '（没找到）'}")
        out.append(f"- 战斗范围：{', '.join(roots) or '-'}；encountered 标记：{', '.join(enc) or '-'}")
        wakes, scripts, battles, npcs, refights = [], [], [], [], []
        for f in fsms:
            if not in_scope(f["path"], roots):
                continue
            for sn, st in f["states"].items():
                for a in st["acts"]:
                    # 类型名要去掉 «自定义显示名»（Codex 审计 0.10.3 PT04：以前 Trigger2dEvent «Wake trigger» 认不出）。
                    an = action_name(a)
                    if a.startswith("(off)"):
                        continue
                    if an in TRIGGER_ACTIONS and "Exit" not in a.split("trigger=", 1)[-1][:20]:
                        wakes.append(f"{f['path']}::{f['name']} [{sn}] {an} → {[t for t, _ in st['tr']]}")
                    if an in ("PlayerDataBoolTest", "PlayerDataVariableTest") and re.search(r"(boolName|VariableName)='encountered", a):
                        refights.append(f"{f['path']}::{f['name']} [{sn}] {a[:120]}")
            if "Dialogue" in f["name"] or "Convo" in f["name"]:
                npcs.append(f"FSM {f['path']}::{f['name']}")
        for p, o in objs.items():
            if not in_scope(p, roots):
                continue
            trig = any("(trigger)" in c for c in o["comps"])
            if "BattleScene" in o["comps"]:
                battles.append(p)
            if "PlayMakerNPC" in o["comps"]:
                npcs.append(f"NPC {p}")
            if trig:
                for d in o["details"]:
                    t = d.split(":", 1)[0]
                    if t in census and "<PlayMakerFSM>" in d:
                        scripts.append(f"{p}: {d[:170]}")
        kinds = []
        if wakes:
            kinds.append("FSM 触发区")
        if scripts:
            kinds.append("脚本触发区")
        if battles:
            kinds.append("BattleScene")
        if npcs:
            kinds.append("有 NPC/对话")
        if refights:
            kinds.append("有重打分支")
        summary.append((b["id"], b["name"]["zh"], ", ".join(kinds) or "（都没找到）"))
        for title, items in (("FSM 等玩家进入", wakes), ("脚本触发区", scripts), ("BattleScene", battles),
                             ("NPC / 对话", npcs), ("重打分支（测 encountered*）", refights)):
            if items:
                out.append(f"- {title}（{len(items)}）：")
                for x in items[:12]:
                    out.append(f"  - `{x}`")
                if len(items) > 12:
                    out.append(f"  - …还有 {len(items) - 12} 个")
        out.append("")

    head = ["## 总表", "", "| id | Boss | 找到的开战方式 |", "|---|---|---|"]
    head += [f"| {i} | {n} | {k} |" for i, n, k in summary]
    head.append("")
    text = "\n".join(out[:2] + head + out[2:])
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(text)
    print(f"bosses: {len(summary)}; script types: {len(census)}; written {out_path}")


if __name__ == "__main__":
    main()
