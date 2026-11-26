"""[Godhome] 扫描场景里的复活点、长椅、出入口，用来挑大厅场景和确认 Boss 入口。

每个场景输出一行 JSON：
  scene      真实场景名
  mapZone    CustomSceneManager.mapZone
  markers    RespawnMarker 物体名 -> customWakeUp
  benches    挂了 "Bench Control" FSM 的物体名
  gates      TransitionPoint 物体名 -> {target, entry, door, inactive}
  enemies    HealthManager 个数（不分血量）

用法: python -I scan_markers.py <scenes_dir> <out.jsonl> [scene_bundle ...]
"""
import json
import os
import sys
import time
import traceback

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"


def child_names(obj):
    try:
        return {c.m_Name for c in obj.serialized_type.node.m_Children}
    except Exception:
        return set()


def scene_name(obj):
    tree = obj.read_typetree()
    for field in ("m_Container", "m_SceneHashes"):
        for item in tree.get(field) or []:
            key = item[0] if isinstance(item, (list, tuple)) else item.get("first")
            if key and key.endswith(".unity"):
                return os.path.splitext(os.path.basename(key))[0]
    return None


def scan(path):
    env = UnityPy.load(path)
    go_names, mbs, scene = {}, [], None
    for obj in env.objects:
        t = obj.type.name
        if t == "GameObject":
            try:
                go_names[obj.path_id] = obj.read_typetree().get("m_Name", "")
            except Exception:
                pass
        elif t == "AssetBundle" and scene is None:
            try:
                scene = scene_name(obj)
            except Exception:
                pass
        elif t == "MonoBehaviour":
            mbs.append(obj)

    markers, benches, gates, enemies, mapzone = {}, set(), {}, 0, None
    for obj in mbs:
        names = child_names(obj)
        try:
            if "respawnFacingRight" in names and "customWakeUp" in names:
                tr = obj.read_typetree()
                go = go_names.get(tr["m_GameObject"]["m_PathID"], "?")
                markers[go] = bool(tr.get("customWakeUp"))
            elif "targetScene" in names and "entryPoint" in names:
                tr = obj.read_typetree()
                go = go_names.get(tr["m_GameObject"]["m_PathID"], "?")
                gates[go] = {
                    "target": tr.get("targetScene", ""),
                    "entry": tr.get("entryPoint", ""),
                    "door": bool(tr.get("isADoor")),
                    "inactive": bool(tr.get("isInactive")),
                }
            elif "hp" in names and "enemyType" in names:
                enemies += 1
            elif "mapZone" in names and mapzone is None:
                mapzone = obj.read_typetree().get("mapZone")
            elif "fsm" in names:
                tr = obj.read_typetree()
                if (tr.get("fsm") or {}).get("name", "") == "Bench Control":
                    benches.add(go_names.get(tr["m_GameObject"]["m_PathID"], "?"))
        except Exception:
            pass
    return {"scene": scene, "mapZone": mapzone, "markers": markers,
            "benches": sorted(benches), "gates": gates, "enemies": enemies}


def load_done(out):
    """已经扫成功的包（续跑时跳过）。上次出错的记录丢掉、下次重扫（Codex 审计 0.10.3 PT01：以前出错的也算扫过，临时读不了的包永远漏掉）。
    输出文件改写成只剩成功的记录（同一个包只留最后一条），先写临时文件再替换。"""
    if not os.path.exists(out):
        return set()
    keep = {}
    dropped = 0
    for line in open(out, encoding="utf-8"):
        try:
            rec = json.loads(line)
        except Exception:
            dropped += 1
            continue
        if not isinstance(rec, dict) or "bundle" not in rec or "error" in rec:
            dropped += 1
            continue
        keep[rec["bundle"]] = line.rstrip("\n")
    if dropped:
        tmp = out + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            for line in keep.values():
                f.write(line + "\n")
        os.replace(tmp, out)
        print(f"[Godhome] 去掉 {dropped} 条出错或读不了的记录，这些包会重扫 / dropped {dropped} failed records, they will be rescanned", flush=True)
    return set(keep)


def main():
    sdir, out = sys.argv[1:3]
    files = sys.argv[3:] or sorted(f for f in os.listdir(sdir) if f.endswith(".bundle"))
    done = load_done(out)
    t0 = time.time()
    with open(out, "a", encoding="utf-8") as f:
        for i, fn in enumerate(files):
            if fn in done:
                continue
            try:
                res = scan(os.path.join(sdir, fn))
            except Exception as e:
                res = {"error": repr(e), "trace": traceback.format_exc()[-800:]}
            res["bundle"] = fn
            f.write(json.dumps(res, ensure_ascii=False) + "\n")
            f.flush()
            if (i + 1) % 50 == 0:
                print(f"[Godhome] {i + 1}/{len(files)} {time.time() - t0:.0f}s", flush=True)


if __name__ == "__main__":
    main()
