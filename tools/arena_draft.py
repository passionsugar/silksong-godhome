"""[Godhome] 离线工具：给 bosses.json 的 arena 段出草稿（见 docs/arena.md 第 3 节）。运行时不用。

输入：
  dump_dir      tools/dump_fsm.py 导出的场景文本（一个场景一个 .txt）
  bosses.json   Boss 表（读 id、名字、场景、bossObjects、击败标记）
输出：
  <out>.md      每个 Boss 一节：依据（血量组件、设击败标记的状态、ZERO HP、触发区、门）和草稿
  <out>.json    { id: { "arena": {...}, "review": ["要人看的地方", ...] } }

草稿规则（都要人核对，自检验证）：
  door  开战触发区的中心（导出里的世界范围；旧格式的导出只认 BoxCollider2D，按物体位置加 offset 算）
  face  朝 Boss
  wake  第一个触发区 > BattleScene
  end   Boss 有特殊死亡（hasSpecialDeath）或没有血量组件时：设击败标记的状态；没有击败标记就用 ZERO HP 进的状态
  enter / patch / keep / challenge  不出草稿：要实打跟踪 FSM 以后手写（见 docs/arena.md）

用法：python -I arena_draft.py <dump_dir> <bosses.json> <out 不带扩展名>
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dumpfmt import action_name, parse_fsm_head  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

WORLD = {"Black Thread States", "Black Thread States Thread Only Variant", "Area_States"}
TRIGGER_ACTIONS = ("Trigger2dEvent", "SendTrigger2DEvent", "CheckAlertRange", "CheckAlertRangeByName",
                   "CheckTrackTriggerCount", "TriggerEnterEventSubscribe")
# 和开战无关、但同样等玩家进入的触发区（旧版自动挑错过的：音效、雾、音乐；还有对话、环境小虫、特效、路人）。
NOISE = re.compile(r"audio|music|fog|atmos|sound|camera|cam lock|title|dialogue|ambient|effect|npc|voice|silkfly|pilgrim|"
                   r"fan |fan$|flower|bench|bool setter|blocker|bump|clasher|spitter|summon|tiny|small", re.I)
# 等玩家进入的那种状态名。
WAIT_STATE = re.compile(r"dormant|idle|sleep|ready|init|wait|out of range|unencountered|rest|detect", re.I)

OBJ_RE = re.compile(r"^  (\S.*?)  @\(([-\d.]+),([-\d.]+)\)(.*?)\{(.*)\}\s*$")
# 新版导出（dump_fsm.py 的 world_bounds）：size 和 off 之间插了 points=、world x a..b y c..d、顶点表。
WORLD_RE = re.compile(r"world x ([-\d.]+)\.\.([-\d.]+) y ([-\d.]+)\.\.([-\d.]+)")
BOX_RE = re.compile(r"BoxCollider2D size=\(([-\d.]+),([-\d.]+)\).*? off=\(([-\d.]+),([-\d.]+)\)")


def root_for(path):
    parts = path.split("/")
    n = min(3, len(parts)) if parts[0] in WORLD else 1
    return "/".join(parts[:n])


def parse(path):
    objs, fsms = {}, []
    with open(path, encoding="utf-8") as f:
        lines = f.read().splitlines()
    section = cur_obj = cur_fsm = cur_state = None
    for no, ln in enumerate(lines, 1):
        if ln.startswith("## "):
            section = "fsm" if ln.startswith("## FSM") else "obj"
            continue
        if section == "obj":
            m = OBJ_RE.match(ln)
            if m:
                comps = [c.strip() for c in m.group(5).split(",")]
                cur_obj = objs[m.group(1)] = {"pos": (float(m.group(2)), float(m.group(3))), "flags": m.group(4).strip(),
                                              "comps": comps, "details": []}
            elif cur_obj is not None and ln.startswith("      "):
                cur_obj["details"].append(ln.strip())
        elif section == "fsm":
            # 标题行读不懂直接报错（dumpfmt），不当成没有这个 FSM、也不把它的状态记到上一个 FSM 里。
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
            if ln.startswith("  vars:"):
                cur_fsm["vars"] = dict(re.findall(r"gameObject ([^=;]+)=GO:([^;]+)", s))
            elif ln.startswith("  global:"):
                mm = re.match(r"global:\s*(.*?)\s*->\s*(.*)$", s)
                if mm:
                    cur_fsm["globals"].append((mm.group(1), mm.group(2)))
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


SPECIAL_RE = re.compile(r"\bhasSpecialDeath=(True|1)\b")


def hm_info(o):
    """血量组件：hp 和有没有特殊死亡。dump_fsm 的布尔写成 True（False 不写），旧导出可能是 1，两种都认（Codex 审计 0.10.3 PT03）。"""
    for d in o["details"]:
        if d.startswith("HealthManager:"):
            hp = re.search(r"\bhp=(\d+)", d)
            return {"hp": int(hp.group(1)) if hp else None, "special": bool(SPECIAL_RE.search(d))}
    return None


def trigger_box(o):
    """物体上第一个触发碰撞体的世界坐标中心、大小，和是不是精确的。
    新版导出有世界范围（按完整的位置、旋转、缩放矩阵换算了所有角点），直接用；
    旧格式只认 BoxCollider2D，按物体位置加 offset（不管缩放、旋转），不精确，草稿里会提醒重新导出。"""
    for d in o["details"]:
        if not d.startswith("trigger:"):
            continue
        m = WORLD_RE.search(d)
        if m:
            x0, x1, y0, y1 = map(float, m.groups())
            return ((x0 + x1) / 2, (y0 + y1) / 2), (x1 - x0, y1 - y0), True
        m = BOX_RE.search(d)
        if m:
            w, h, ox, oy = map(float, m.groups())
            return (o["pos"][0] + ox, o["pos"][1] + oy), (w, h), False
    return None, None, False


def r1(v):
    return round(v, 1)


def main():
    dump_dir, bosses_path, out = sys.argv[1:4]
    bosses = json.load(open(bosses_path, encoding="utf-8"))["bosses"]
    cache = {}

    def load(scene):
        if scene not in cache:
            p = os.path.join(dump_dir, scene + ".txt")
            cache[scene] = parse(p) if os.path.exists(p) else None
        return cache[scene]

    md = ["# arena 段草稿（离线，要人核对）", ""]
    drafts = {}
    for b in bosses:
        bid = b["id"]
        review = []
        scenes = [s for s in [b.get("scene"), b.get("hostScene")] if s]
        objs, fsms, owner = {}, [], {}
        for s in scenes:
            got = load(s)
            if got is None:
                review.append(f"没有 {s} 的导出")
                continue
            o, f = got
            for p in o:
                owner[p] = s
            objs.update(o)
            fsms.extend(f)
        names = set(b.get("bossObjects") or [])
        boss_paths = [p for p in objs if p.split("/")[-1].replace("(Clone)", "").strip() in names]
        roots = sorted({root_for(p) for p in boss_paths})
        hms = {p: hm_info(objs[p]) for p in boss_paths}
        has_hm = {p: h for p, h in hms.items() if h}
        special = any(h["special"] for h in has_hm.values()) or not has_hm
        boss_x = sum(objs[p]["pos"][0] for p in boss_paths) / len(boss_paths) if boss_paths else None

        # 设击败标记的状态（游戏在这里认定 Boss 被打败）
        defeated = (b.get("flags") or {}).get("defeated") or []
        setters = []
        for f in fsms:
            for sn, st in f["states"].items():
                for a in st["acts"]:
                    if a.startswith("(off)"):
                        continue
                    for flag in defeated:
                        if re.search(r"^SetPlayerData\w*\(.*boolName='%s'" % re.escape(flag), a):
                            setters.append({"path": f["path"], "fsm": f["name"], "state": sn, "flag": flag})
        # Boss 自己的 FSM 收到 ZERO HP 进的状态
        zero = []
        for f in fsms:
            if not any(f["path"] == p or f["path"].startswith(p + "/") for p in boss_paths):
                continue
            for sn, st in f["states"].items():
                for ev, to in st["tr"]:
                    if ev == "ZERO HP":
                        zero.append({"path": f["path"], "fsm": f["name"], "from": sn, "state": to})
            for ev, to in f["globals"]:
                if ev == "ZERO HP":
                    zero.append({"path": f["path"], "fsm": f["name"], "from": "*", "state": to})
        # 等玩家进入的触发区（只认“进来就发事件”的：AlertRange 要有 InRangeEvent / sendEvent）
        wakes = []
        for f in fsms:
            if not in_scope(f["path"], roots):
                continue
            for sn, st in f["states"].items():
                for a in st["acts"]:
                    if a.startswith("(off)"):
                        continue
                    an = action_name(a)
                    if an not in TRIGGER_ACTIONS or "Exit" in a.split("trigger=", 1)[-1][:20]:
                        continue
                    range_path = f["path"]
                    if an == "CheckAlertRange":
                        if "InRangeEvent=!''" in a:
                            continue
                        mv = re.search(r"alertRange=\$([^,)]+)", a)
                        range_path = (f.get("vars") or {}).get(mv.group(1).strip(), "") if mv else ""
                    elif an == "CheckAlertRangeByName":
                        if "sendEvent=''" in a and "storeResult=0" in a:
                            continue
                        mn = re.search(r"alertRangeName='([^']*)'", a)
                        want = (mn.group(1) if mn else "") or "Alert Range"
                        range_path = next((p for p in objs if p.startswith(f["path"] + "/") and p.split("/")[-1] == want), "")
                    o = objs.get(range_path)
                    c, size, exact = trigger_box(o) if o else (None, None, False)
                    on_boss = any(f["path"] == p for p in boss_paths)
                    above_boss = any(p.startswith(f["path"] + "/") for p in boss_paths)
                    noise = bool(NOISE.search(f["path"].split("/")[-1] + " " + f["name"]))
                    score = (3 if on_boss else 2 if above_boss else 0) + (1 if WAIT_STATE.search(sn) else 0) \
                        + (1 if f["name"] in ("Control", "Battle Start", "Challenge") else 0) + (1 if c else 0) - (6 if noise else 0)
                    wakes.append({"path": f["path"], "fsm": f["name"], "state": sn, "action": an, "range": range_path,
                                  "center": c, "size": size, "exact": exact, "noise": noise, "score": score})
        wakes.sort(key=lambda w: -w["score"])
        battles = [p for p in objs if in_scope(p, roots) and "BattleScene" in objs[p]["comps"]]
        gates = {p: objs[p]["pos"] for p in objs if any(d.startswith("TransitionPoint:") for d in objs[p]["details"])}

        a = {}
        # door：开战触发区的中心
        pick = next((w for w in wakes if w["center"] and not w["noise"]), None)
        if pick:
            a["door"] = [r1(pick["center"][0]), r1(pick["center"][1])]
            door_from = f"触发区 {pick['path']}::{pick['fsm']} [{pick['state']}] 的中心"
            review.append("door 取的是触发区中心，没实测过")
            if not pick["exact"]:
                review.append("触发区是旧格式导出（没算缩放、旋转），door 可能偏：用新版 dump_fsm.py 重新导出")
        else:
            door_from = "（没有）"
            review.append("没有 door 的候选")
        if "door" in a and boss_x is not None:
            a["face"] = "right" if boss_x >= a["door"][0] else "left"
        # wake
        pick = next((w for w in wakes if not w["noise"]), None)
        if pick:
            a["wake"] = {"path": pick["path"], "fsm": pick["fsm"], "state": pick["state"]}
            review.append("wake 取的是第一个触发区，没实测过；等玩家的状态有几个（来回切换）要写成 A|B")
        elif battles:
            a["wake"] = {"battle": battles[0]}
            review.append("wake 取的是 BattleScene，没实测过")
        else:
            review.append("没有 wake 的候选")
        # end
        if special:
            if setters:
                seen = set()
                a["end"] = []
                for s_ in setters:
                    k = (s_["path"], s_["fsm"], s_["state"])
                    if k not in seen:
                        seen.add(k)
                        a["end"].append({"path": s_["path"], "fsm": s_["fsm"], "state": s_["state"]})
                if len(a["end"]) > 1:
                    review.append(f"end 有 {len(a['end'])} 个设击败标记的状态，看看是不是都在打赢的路上")
            elif zero:
                a["end"] = [{"path": z["path"], "fsm": z["fsm"], "state": z["state"]} for z in zero]
                review.append("没有击败标记，end 取的是 ZERO HP 进的状态：多阶段的 Boss 要确认是最后一段")
            else:
                review.append("特殊死亡 / 没有血量组件，但找不到 end 的候选：要实打跟踪 FSM")
        drafts[bid] = {"arena": a, "review": review}

        # 报告
        md.append(f"## {bid}（{b['name']['zh']}）— {', '.join(scenes)}")
        md.append("")
        md.append("- Boss：" + ("；".join(f"`{p}` " + (f"hp={h['hp']}{' 特殊死亡' if h['special'] else ''}" if h else "没有血量组件") for p, h in hms.items()) or "（没找到）"))
        md.append(f"- 击败标记：{', '.join(defeated) or '（没有）'}")
        for s_ in setters:
            md.append(f"  - 设 `{s_['flag']}`：`{s_['path']}::{s_['fsm']} [{s_['state']}]`")
        for z in zero:
            md.append(f"  - ZERO HP：`{z['path']}::{z['fsm']} [{z['from']}] → [{z['state']}]`")
        if wakes:
            md.append(f"- 触发区（{len(wakes)}）：")
            for w in wakes[:10]:
                c = f" 中心 ({w['center'][0]:.1f}, {w['center'][1]:.1f}) 大小 {w['size'][0]:.1f}×{w['size'][1]:.1f}" if w["center"] else ""
                rng = f" 范围 `{w['range']}`" if w["range"] and w["range"] != w["path"] else ""
                md.append(f"  - [{w['score']}] `{w['path']}::{w['fsm']} [{w['state']}]` {w['action']}{rng}{c}{' （无关，跳过）' if w['noise'] else ''}")
        templ = sorted({f"{f['path']}::{f['name']}" for f in fsms if "template=" in f["flags"]
                        and any(f["path"] == p for p in boss_paths)})
        if templ:
            md.append(f"- Boss 身上用模板的 FSM（导出里没有状态）：{', '.join('`' + t + '`' for t in templ)}")
        if battles:
            md.append(f"- BattleScene：{', '.join('`' + x + '`' for x in battles)}")
        md.append(f"- 场景里的门：{', '.join(f'{k} ({v[0]:.1f}, {v[1]:.1f})' for k, v in sorted(gates.items()))}（神居版都换成隐形墙）")
        md.append(f"- door 来自：{door_from}")
        md.append("- 草稿：")
        md.append("```json")
        md.append(json.dumps(a, ensure_ascii=False, indent=1))
        md.append("```")
        for r in review:
            md.append(f"- **要看**：{r}")
        md.append("")

    with open(out + ".md", "w", encoding="utf-8") as f:
        f.write("\n".join(md))
    with open(out + ".json", "w", encoding="utf-8") as f:
        json.dump(drafts, f, ensure_ascii=False, indent=1)
    n_review = sum(1 for d in drafts.values() if d["review"])
    print(f"bosses: {len(drafts)}; with door: {sum(1 for d in drafts.values() if 'door' in d['arena'])}; "
          f"with end: {sum(1 for d in drafts.values() if 'end' in d['arena'])}; need review: {n_review}")


if __name__ == "__main__":
    main()
