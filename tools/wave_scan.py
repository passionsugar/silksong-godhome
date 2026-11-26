"""[Godhome] 离线工具：列出每个 Boss 场景里 BattleScene 的波次，找出 Boss 前面的小怪波次（给 arena.skipWaves 出草稿）。运行时不用。

输入：
  dump_dir      tools/dump_fsm.py 导出的场景文本（一个场景一个 .txt）
  bosses.json   Boss 表
输出（stdout，Markdown）：每个有 BattleScene 的 Boss 一节，每一波：路径、开始延迟、里面的敌人（血量），
  标出 Boss 在哪一波、哪些波在 Boss 前面（草稿：skipWaves 写这些）；
  再列出波次以外、不是 Boss 的敌人（可能是 FSM 自己管的前置小怪，要人看）。

用法：python -I wave_scan.py <dump_dir> <bosses.json>
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from arena_draft import parse, hm_info  # noqa: E402

sys.stdout.reconfigure(encoding="utf-8")

WAVES_RE = re.compile(r"waves=\[(.*?)\]")


def main():
    dump_dir, bosses_path = sys.argv[1:3]
    bosses = json.load(open(bosses_path, encoding="utf-8"))["bosses"]
    cache = {}

    def load(scene):
        if scene not in cache:
            p = os.path.join(dump_dir, scene + ".txt")
            cache[scene] = parse(p) if os.path.exists(p) else None
        return cache[scene]

    draft = {}
    bad = set()
    out = ["# BattleScene 波次（离线，要人核对）", ""]
    for b in bosses:
        bid = b["id"]
        scenes = []
        for s in [b.get("scene"), b.get("hostScene"), (b.get("entry") or {}).get("scene")]:
            if s and s not in scenes:
                scenes.append(s)
        names = set(b.get("bossObjects") or [])
        lines = []
        for s in scenes:
            got = load(s)
            if got is None:
                continue
            objs, _ = got
            # 不在任何波次里、也不是 Boss 的敌人（血量组件）：可能是 FSM 自己管的前置小怪，要人看
            boss_paths = [p for p in objs if p.split("/")[-1].replace("(Clone)", "").strip() in names]
            wave_roots = []
            for o in objs.values():
                for d in o["details"]:
                    m = WAVES_RE.search(d) if d.startswith("BattleScene:") else None
                    if m:
                        wave_roots += [w.split("<")[0].strip() for w in m.group(1).split(",") if w.strip()]
            others = []
            for p, o in objs.items():
                h = hm_info(o)
                if not h or any(p == q or p.startswith(q + "/") for q in boss_paths + wave_roots):
                    continue
                others.append(f"{p}{' hp=' + str(h['hp']) if h['hp'] is not None else ''}{' [inactive]' if 'inactive' in o['flags'] else ''}")
            if others:
                lines.append(f"- [{s}] 波次以外的非 Boss 敌人 {len(others)} 个：")
                lines += [f"  - `{x}`" for x in others[:25]]
                if len(others) > 25:
                    lines.append(f"  - …还有 {len(others) - 25} 个")
            for path, o in objs.items():
                if "BattleScene" not in o["comps"]:
                    continue
                waves = []
                incomplete = False
                for d in o["details"]:
                    if d.startswith("BattleScene:"):
                        m = WAVES_RE.search(d)
                        if m:
                            waves = [w.split("<")[0].strip() for w in m.group(1).split(",") if w.strip()]
                        # 旧版导出会把长数组截成 12 个加 …(+N)、长行截断：波次不全，不能出 skipWaves 草稿
                        # （Codex 审计 0.10.3 PT05：以前把 …(+1) 当成一波，真正的最后一波丢了）。
                        incomplete = (m is None and "waves=[" in d) or any("…(+" in w for w in waves) or "truncated" in d
                        break
                if incomplete:
                    lines.append(f"- [{s}] `{path}`：**波次列表不完整（导出被截断），不出草稿**，用新版 dump_fsm.py 重新导出")
                    bad.add(bid)
                    continue
                if not waves:
                    continue
                boss_wave = None
                rows = []
                for i, w in enumerate(waves):
                    wd = next((d for d in objs.get(w, {"details": []})["details"] if d.startswith("BattleWave:")), "")
                    kids = [p for p in objs if p.startswith(w + "/") and "/" not in p[len(w) + 1:]]
                    under = [p for p in objs if p.startswith(w + "/")]
                    has_boss = any(p.split("/")[-1].replace("(Clone)", "").strip() in names for p in under)
                    if has_boss and boss_wave is None:
                        boss_wave = i
                    enemies = []
                    for k in kids:
                        h = hm_info(objs[k])
                        tag = k.split("/")[-1]
                        enemies.append(f"{tag}" + (f" hp={h['hp']}" if h else ""))
                    rows.append((i, w, wd, has_boss, enemies))
                lines.append(f"- [{s}] `{path}`（{len(waves)} 波，Boss 在第 {boss_wave + 1 if boss_wave is not None else '?'} 波）")
                for i, w, wd, has_boss, enemies in rows:
                    mark = "Boss" if has_boss else ("**Boss 前**" if boss_wave is not None and i < boss_wave else ("Boss 后" if boss_wave is not None else "?"))
                    lines.append(f"  - 第 {i + 1} 波 {mark} `{w}` {wd[len('BattleWave:'):].strip() if wd else ''}")
                    lines.append(f"    - 敌人：{'、'.join(enemies) if enemies else '（无）'}")
                if boss_wave:
                    draft.setdefault(bid, []).extend(w for i, w, *_ in rows if i < boss_wave)
        if lines:
            out.append(f"## {bid}（{'、'.join(scenes)}）")
            out.extend(lines)
            out.append("")
    for bid in bad:
        draft.pop(bid, None)
    out.append("## 草稿：skipWaves")
    if bad:
        out.append(f"（波次不完整、没出草稿 / incomplete, no draft：{', '.join(sorted(bad))}）")
    out.append("```json")
    out.append(json.dumps(draft, ensure_ascii=False, indent=2))
    out.append("```")
    print("\n".join(out))
    if bad:
        sys.exit(1)


if __name__ == "__main__":
    main()
