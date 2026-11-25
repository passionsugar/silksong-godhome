"""[Godhome] 扫描丝之歌场景 AssetBundle，提取 Boss 清单需要的信息。

对每个场景输出一行 JSON：
  scene            真实场景名（从 AssetBundle m_Container 里取，带大小写）
  gates            TransitionPoint：物体名 -> targetScene / entryPoint
  health           HealthManager：物体名 -> hp（只列 hp >= MIN_HP 的）
  battle           挂了 BattleScene 组件的物体名
  flags            FSM / 组件原始数据里出现的 PlayerData 字段名
  titles           FSM 原始数据里出现的 Boss 标题键（DisplayBossTitle 用）
  fsms             场景里 PlayMakerFSM 的 (物体名, FSM 名)，只列带 Boss 相关字样的

用法: python -I scan_scenes.py <scenes_dir> <monoscripts.bundle> <names.json> <out.jsonl> [scene_bundle ...]
names.json = {"flags": [...PlayerData 字段...], "titles": [...标题键...]}
"""
import json
import os
import re
import sys
import time
import traceback

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"
MIN_HP = 100
IDENT = re.compile(rb"[A-Za-z_][A-Za-z0-9_]{3,63}")
BOSSY = re.compile(r"boss|battle|arena|lace|judge|silk|title|challenge|gate|door|cutscene|intro", re.I)


def child_names(obj):
    try:
        node = obj.serialized_type.node
        return {c.m_Name for c in node.m_Children}
    except Exception:
        return set()


def scan(path, flags, titles):
    env = UnityPy.load(path)
    go_names = {}
    scene = None
    mbs = []
    for obj in env.objects:
        t = obj.type.name
        if t == "GameObject":
            try:
                go_names[obj.path_id] = obj.read_typetree().get("m_Name", "")
            except Exception:
                pass
        elif t == "AssetBundle":
            try:
                tree = obj.read_typetree()
                cont = tree.get("m_Container") or []
                for item in cont:
                    key = item[0] if isinstance(item, (list, tuple)) else item.get("first")
                    if key and key.endswith(".unity"):
                        scene = os.path.splitext(os.path.basename(key))[0]
                if not scene:
                    for k in (tree.get("m_SceneHashes") or []):
                        key = k[0] if isinstance(k, (list, tuple)) else k.get("first")
                        if key and key.endswith(".unity"):
                            scene = os.path.splitext(os.path.basename(key))[0]
            except Exception:
                pass
        elif t == "MonoBehaviour":
            mbs.append(obj)

    gates, health, battle, fsms, additive = {}, {}, [], [], []
    mapzone = None
    found_flags, found_titles = set(), set()
    for obj in mbs:
        names = child_names(obj)
        raw = None
        if "targetScene" in names and "entryPoint" in names:
            try:
                tr = obj.read_typetree()
                go = go_names.get(tr["m_GameObject"]["m_PathID"], "?")
                gates[go] = {"target": tr.get("targetScene", ""), "entry": tr.get("entryPoint", "")}
            except Exception:
                pass
        elif "hp" in names and "enemyType" in names:
            try:
                tr = obj.read_typetree()
                hp = tr.get("hp", 0)
                if hp >= MIN_HP:
                    go = go_names.get(tr["m_GameObject"]["m_PathID"], "?")
                    health[go] = hp
            except Exception:
                pass
        elif "waves" in names and "gates" in names:
            try:
                tr = obj.read_typetree()
                battle.append(go_names.get(tr["m_GameObject"]["m_PathID"], "?"))
            except Exception:
                pass
        elif "mapZone" in names and mapzone is None:
            try:
                mapzone = obj.read_typetree().get("mapZone")
            except Exception:
                pass
        elif "sceneNameToLoad" in names:
            try:
                tr = obj.read_typetree()
                additive.append({
                    "go": go_names.get(tr["m_GameObject"]["m_PathID"], "?"),
                    "scene": tr.get("sceneNameToLoad", ""),
                    "alt": tr.get("altSceneNameToLoad", ""),
                    "tests": compact(tr.get("tests")),
                    "quests": compact(tr.get("questTests")),
                    "doorWhite": tr.get("doorWhiteList", []),
                    "doorBlack": tr.get("doorBlackList", []),
                    "setPdBoolOnLoad": tr.get("setPdBoolOnLoad", ""),
                })
            except Exception as e:
                additive.append({"error": repr(e)})
        elif "fsm" in names:
            try:
                tr = obj.read_typetree()
                fname = (tr.get("fsm") or {}).get("name", "")
                go = go_names.get(tr["m_GameObject"]["m_PathID"], "?")
                if BOSSY.search(go) or BOSSY.search(fname):
                    fsms.append([go, fname])
            except Exception:
                pass
        try:
            raw = obj.get_raw_data()
        except Exception:
            raw = b""
        for m in IDENT.findall(raw):
            s = m.decode("ascii")
            if s in flags:
                found_flags.add(s)
            if s in titles:
                found_titles.add(s)
    return {
        "scene": scene,
        "gates": gates,
        "health": health,
        "battle": sorted(set(battle)),
        "flags": sorted(found_flags),
        "titles": sorted(found_titles),
        "fsms": sorted({tuple(x) for x in fsms}),
        "additive": additive,
        "mapZone": mapzone,
    }


def compact(v):
    """Drop empty / default entries from a serialized PlayerDataTest so the JSON stays readable."""
    if isinstance(v, dict):
        out = {k: compact(x) for k, x in v.items() if k not in ("m_FileID", "m_PathID")}
        return {k: x for k, x in out.items() if x not in ("", [], {}, None)}
    if isinstance(v, list):
        return [compact(x) for x in v if compact(x) not in ("", [], {}, None)]
    return v


def main():
    sdir, monos, names_path, out = sys.argv[1:5]
    only = sys.argv[5:]
    names = json.load(open(names_path, encoding="utf-8"))
    flags, titles = set(names["flags"]), set(names["titles"])
    files = only or sorted(f for f in os.listdir(sdir) if f.endswith(".bundle"))
    done = set()
    if os.path.exists(out):
        for line in open(out, encoding="utf-8"):
            try:
                done.add(json.loads(line)["bundle"])
            except Exception:
                pass
    t0 = time.time()
    with open(out, "a", encoding="utf-8") as f:
        for i, fn in enumerate(files):
            if fn in done:
                continue
            try:
                res = scan(os.path.join(sdir, fn), flags, titles)
            except Exception as e:
                res = {"error": repr(e), "trace": traceback.format_exc()[-800:]}
            res["bundle"] = fn
            f.write(json.dumps(res, ensure_ascii=False) + "\n")
            f.flush()
            print(f"[Godhome] {i + 1}/{len(files)} {fn} {time.time() - t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
