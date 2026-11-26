"""[Godhome] 把场景 AssetBundle 解成可读文本：物体层级、世界坐标、组件字段、触发器，以及每个 PlayMakerFSM 的
状态、跳转、动作和参数。用来查 Boss 是怎么被唤醒、怎么判定死亡的（游戏内部机制先查数据再下结论）。

参数解码按反编译的 PlayMaker ActionData（dataVersion 2：各类型参数分表存，FsmEvent/字符串在 stringParams，
基本类型在 byteData）。

用法: python -I dump_fsm.py <monoscripts.bundle> <out_dir> <scene.bundle> [...]
每个场景输出 <out_dir>/<场景名>.txt。有场景失败时退出码是 1。

文本格式（字符串引号、动作行）见 dumpfmt.py，读它的工具（arena_draft、scan_wakes、fsm_path、wave_scan）共用。
"""
import math
import os
import struct
import sys
import time
import traceback

import UnityPy

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dumpfmt import q  # noqa: E402

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"

# HutongGames.PlayMaker.ParamDataType
PDT = [
    "Integer", "Boolean", "Float", "String", "Color", "ObjectReference", "LayerMask", "Enum", "Vector2",
    "Vector3", "Vector4", "Rect", "Array", "Character", "AnimationCurve", "FsmFloat", "FsmInt", "FsmBool",
    "FsmString", "FsmGameObject", "FsmOwnerDefault", "FunctionCall", "FsmAnimationCurve", "FsmEvent",
    "FsmObject", "FsmColor", "Unsupported", "GameObject", "FsmVector3", "LayoutOption", "FsmRect",
    "FsmEventTarget", "FsmMaterial", "FsmTexture", "Quaternion", "FsmQuaternion", "FsmProperty",
    "FsmVector2", "FsmTemplateControl", "FsmVar", "CustomClass", "FsmArray", "FsmEnum",
]
LIST_OF = {
    "FsmFloat": "fsmFloatParams", "FsmInt": "fsmIntParams", "FsmBool": "fsmBoolParams",
    "FsmString": "fsmStringParams", "FsmGameObject": "fsmGameObjectParams",
    "FsmOwnerDefault": "fsmOwnerDefaultParams", "FunctionCall": "functionCallParams",
    "FsmAnimationCurve": "animationCurveParams", "AnimationCurve": "animationCurveParams",
    "FsmObject": "fsmObjectParams", "FsmMaterial": "fsmObjectParams", "FsmTexture": "fsmObjectParams",
    "FsmColor": "fsmColorParams", "FsmVector3": "fsmVector3Params", "FsmVector2": "fsmVector2Params",
    "FsmRect": "fsmRectParams", "FsmQuaternion": "fsmQuaternionParams", "FsmEventTarget": "fsmEventTargetParams",
    "FsmProperty": "fsmPropertyParams", "FsmTemplateControl": "fsmTemplateControlParams",
    "FsmVar": "fsmVarParams", "FsmArray": "fsmArrayParams", "FsmEnum": "fsmEnumParams",
    "LayoutOption": "layoutOptionParams", "ObjectReference": "unityObjectParams", "GameObject": "unityObjectParams",
}
EVENT_TARGET = ["Self", "GameObject", "GameObjectFSM", "FSMComponent", "BroadcastAll", "HostFSM", "ParentFSM", "SubFSMs"]
# 这些脚本只是装饰/渲染，物体清单里不展开字段
DECOR = ("Parallax", "Light", "Audio", "Sprite", "Particle", "Decoration", "tk2d", "Wind", "Mesh", "Shadow",
         "Renderer", "Camera", "Blur", "Fade", "Shake", "Tint", "Glow", "Flicker", "Sway", "Grass", "Vine",
         "Dust", "Ambient", "Fog", "Water", "Reflection", "Color", "Bloom", "Shader", "Material")


def short(t):
    return t.rsplit(".", 1)[-1] if t else t


_MONO_CACHE = {}


def load_mono(mono_path):
    """MonoScript path_id -> 类名。所有 MonoScript 都在同一个 monoscripts 包里，场景里的 m_Script 是指向它的外部引用。"""
    if mono_path not in _MONO_CACHE:
        m = {}
        for obj in UnityPy.load(mono_path).objects:
            if obj.type.name == "MonoScript":
                try:
                    t = obj.read_typetree()
                    ns = t.get("m_Namespace") or ""
                    m[obj.path_id] = (ns + "." if ns else "") + t.get("m_ClassName", "?")
                except Exception:
                    pass
        _MONO_CACHE[mono_path] = m
    return _MONO_CACHE[mono_path]


class Scene:
    def __init__(self, mono_path, bundle_path):
        self.mono = load_mono(mono_path)
        self.env = UnityPy.load(bundle_path)
        self.objs = {}  # path_id -> obj
        self.scene_name = os.path.splitext(os.path.basename(bundle_path))[0]
        for obj in self.env.objects:
            self.objs[obj.path_id] = obj
            if obj.type.name == "AssetBundle":
                try:
                    tree = obj.read_typetree()
                    for item in (tree.get("m_SceneHashes") or []) + (tree.get("m_Container") or []):
                        k = item[0] if isinstance(item, (list, tuple)) else item.get("first")
                        if k and k.endswith(".unity"):
                            self.scene_name = os.path.splitext(os.path.basename(k))[0]
                except Exception:
                    pass
        self.trees = {}
        self.go = {}        # go id -> {name, active, comps:[ids], tr}
        self.tr = {}        # transform id -> {go, father, pos, rot, scale}
        for pid, obj in self.objs.items():
            t = obj.type.name
            if t == "GameObject":
                tt = self.tree(pid)
                self.go[pid] = {"name": tt.get("m_Name", ""), "active": bool(tt.get("m_IsActive", 1)),
                                "comps": [c["component"]["m_PathID"] for c in tt.get("m_Component", [])], "tr": None}
            elif t in ("Transform", "RectTransform"):
                tt = self.tree(pid)
                p = tt.get("m_LocalPosition", {})
                r = tt.get("m_LocalRotation", {})
                s = tt.get("m_LocalScale", {})
                self.tr[pid] = {"go": tt["m_GameObject"]["m_PathID"], "father": tt.get("m_Father", {}).get("m_PathID", 0),
                                "pos": (p.get("x", 0), p.get("y", 0)), "rot": _zangle(r),
                                "scale": (s.get("x", 1), s.get("y", 1))}
        for tid, t in self.tr.items():
            g = self.go.get(t["go"])
            if g is not None:
                g["tr"] = tid
        self._world = {}
        self._path = {}

    def tree(self, pid):
        if pid not in self.trees:
            try:
                self.trees[pid] = self.objs[pid].read_typetree()
            except Exception as e:
                self.trees[pid] = {"__error__": repr(e)}
        return self.trees[pid]

    def script(self, pid):
        """MonoBehaviour 的脚本类名。"""
        t = self.tree(pid)
        ref = t.get("m_Script") or {}
        return self.mono.get(ref.get("m_PathID"), "?")

    def comp_name(self, pid):
        obj = self.objs.get(pid)
        if obj is None:
            return "?"
        if obj.type.name == "MonoBehaviour":
            return short(self.script(pid))
        return obj.type.name

    def world(self, tid):
        """物体局部坐标 → 世界坐标的 2D 仿射矩阵 (a, b, c, d, tx, ty)：x' = a·x + b·y + tx，y' = c·x + d·y + ty。
        逐级乘完整的 TRS 矩阵（Codex 审计 0.10.3 PT02：以前把角度相加、缩放分量相乘，父物体缩放不等比、子物体又转了的时候算错，
        例：父缩放 (2,1)、子位置 (1,0) 转 90°、孙局部 (1,0)，以前算 (2,2)，实际是 (2,1)）。只看 z 轴旋转（2D）。"""
        if tid in self._world:
            return self._world[tid]
        t = self.tr.get(tid)
        if t is None:
            return (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        c, s = math.cos(math.radians(t["rot"])), math.sin(math.radians(t["rot"]))
        sx, sy = t["scale"]
        local = (c * sx, -s * sy, s * sx, c * sy, t["pos"][0], t["pos"][1])
        if t["father"] and t["father"] in self.tr:
            res = _mat_mul(self.world(t["father"]), local)
        else:
            res = local
        self._world[tid] = res
        return res

    def world_pos(self, tid):
        """物体原点的世界坐标。"""
        m = self.world(tid) if tid else (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
        return m[4], m[5]

    def path(self, goid):
        if goid in self._path:
            return self._path[goid]
        g = self.go.get(goid)
        if g is None:
            return "?"
        t = self.tr.get(g["tr"]) if g["tr"] else None
        name = g["name"]
        if t and t["father"] and t["father"] in self.tr:
            name = self.path(self.tr[t["father"]]["go"]) + "/" + name
        self._path[goid] = name
        return name

    def active_in_hierarchy(self, goid):
        g = self.go.get(goid)
        while g is not None:
            if not g["active"]:
                return False
            t = self.tr.get(g["tr"]) if g["tr"] else None
            if not t or not t["father"] or t["father"] not in self.tr:
                return True
            g = self.go.get(self.tr[t["father"]]["go"])
        return True

    def ref(self, pptr):
        """把 PPtr 解成可读名字。"""
        if not isinstance(pptr, dict):
            return str(pptr)
        fid, pid = pptr.get("m_FileID", 0), pptr.get("m_PathID", 0)
        if pid == 0:
            return "null"
        if fid != 0:
            if pid in self.mono:
                return "script:" + short(self.mono[pid])
            return f"ext:{fid}:{pid}"
        obj = self.objs.get(pid)
        if obj is None:
            return f"?{pid}"
        if obj.type.name == "GameObject":
            return "GO:" + self.path(pid)
        t = self.tree(pid)
        go = (t.get("m_GameObject") or {}).get("m_PathID")
        if go:
            return f"{self.path(go)}<{self.comp_name(pid)}>"
        return f"{obj.type.name}:{t.get('m_Name', pid)}"


def _zangle(r):
    x, y, z, w = r.get("x", 0), r.get("y", 0), r.get("z", 0), r.get("w", 1)
    return math.degrees(math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z)))


def _mat_mul(p, l):
    """两个 2D 仿射矩阵相乘（先 l 后 p）。"""
    a1, b1, c1, d1, x1, y1 = p
    a2, b2, c2, d2, x2, y2 = l
    return (a1 * a2 + b1 * c2, a1 * b2 + b1 * d2, c1 * a2 + d1 * c2, c1 * b2 + d1 * d2,
            a1 * x2 + b1 * y2 + x1, c1 * x2 + d1 * y2 + y1)


def _is_ref(x):
    return isinstance(x, dict) and "m_PathID" in x and "m_FileID" in x


def fsm_var(sc, v):
    """FsmFloat/FsmBool/... 的显示：用变量就显示 $名字，否则显示值。"""
    if not isinstance(v, dict):
        return repr(v)
    if v.get("useVariable") and v.get("name"):
        return "$" + v["name"]
    if "value" in v:
        val = v["value"]
        if isinstance(val, dict) and "m_PathID" in val:
            return sc.ref(val)
        if isinstance(val, dict):
            return "(" + ",".join(f"{x:g}" if isinstance(x, float) else str(x) for x in val.values()) + ")"
        if isinstance(val, float):
            return f"{val:g}"
        if isinstance(val, str):
            return q(val)
        return str(val)
    return compact(sc, v)


def compact(sc, v, depth=0):
    if depth > 7:
        return "…"
    if isinstance(v, dict):
        if "m_PathID" in v and "m_FileID" in v:
            return sc.ref(v)
        parts = []
        for k, x in v.items():
            if k in ("tooltip", "showInInspector", "networkSync", "editorFlags"):
                continue
            s = compact(sc, x, depth + 1)
            if s in ("", "0", "[]", "{}", "null", "''", "0.0", "False"):
                continue
            parts.append(f"{k}={s}")
        return "{" + ", ".join(parts) + "}"
    if isinstance(v, list):
        # 物体引用的数组（BattleScene 的 waves 之类，工具要逐个读）不截断（Codex 审计 0.10.3 PT05：以前 13 波截成 12 个加 …(+1)）；
        # 只有数值之类的长数组截断，并标出还有几个。
        if len(v) > 12 and not all(_is_ref(x) for x in v):
            return "[" + ", ".join(compact(sc, x, depth + 1) for x in v[:12]) + f", …(+{len(v) - 12})]"
        return "[" + ", ".join(compact(sc, x, depth + 1) for x in v) + "]"
    if isinstance(v, float):
        return f"{v:g}"
    if isinstance(v, str):
        return q(v) if v else ""
    return str(v)


def decode_param(sc, ad, i, data_version):
    t = ad["paramDataType"][i]
    tname = PDT[t] if 0 <= t < len(PDT) else f"type{t}"
    pos = ad["paramDataPos"][i]
    size = ad["paramByteDataSize"][i] if i < len(ad.get("paramByteDataSize", [])) else 0
    bd = ad.get("_bytes")
    try:
        if tname in ("Integer", "LayerMask", "Enum"):
            return str(struct.unpack_from("<i", bd, pos)[0])
        if tname == "Boolean":
            return str(bool(bd[pos]))
        if tname == "Float":
            return f"{struct.unpack_from('<f', bd, pos)[0]:g}"
        if tname in ("String", "FsmEvent"):
            if data_version > 1 and ad.get("stringParams"):
                s = ad["stringParams"][pos] if 0 <= pos < len(ad["stringParams"]) else ""
            else:
                s = bd[pos:pos + size].decode("utf-8", "replace")
            return ("!" if tname == "FsmEvent" else "") + q(s)
        if tname in ("Vector2", "Vector3", "Vector4", "Rect", "Color", "Quaternion"):
            n = {"Vector2": 2, "Vector3": 3}.get(tname, 4)
            return "(" + ",".join(f"{x:g}" for x in struct.unpack_from("<" + "f" * n, bd, pos)) + ")"
        if tname == "Array":
            return f"array<{short(ad['arrayParamTypes'][pos])}>[{ad['arrayParamSizes'][pos]}]"
        if tname == "CustomClass":
            return f"class<{short(ad['customTypeNames'][pos])}>"
        lst = ad.get(LIST_OF.get(tname, ""), None)
        if lst is None or not (0 <= pos < len(lst)):
            return f"<{tname}?>"
        v = lst[pos]
        if tname in ("ObjectReference", "GameObject"):
            return sc.ref(v)
        if tname == "FsmOwnerDefault":
            return "owner" if v.get("ownerOption", 0) == 0 else fsm_var(sc, v.get("gameObject", {}))
        if tname == "FsmEventTarget":
            tgt = v.get("target", 0)
            s = EVENT_TARGET[tgt] if 0 <= tgt < len(EVENT_TARGET) else str(tgt)
            if tgt in (1, 2):
                s += " " + (("owner" if v["gameObject"].get("ownerOption", 0) == 0 else fsm_var(sc, v["gameObject"].get("gameObject", {}))))
            if tgt == 2:
                s += " fsm=" + fsm_var(sc, v.get("fsmName", {}))
            if tgt == 3:
                s += " " + sc.ref(v.get("fsmComponent", {}))
            if v.get("sendToChildren", {}).get("value"):
                s += " +children"
            return s
        if tname == "FsmEnum":
            if v.get("useVariable") and v.get("name"):
                return "$" + v["name"]
            return f"{short(v.get('enumName', ''))}:{v.get('intValue')}"
        if tname == "FsmVar":
            name = v.get("variableName", "")
            if name:
                return "$" + name
            return compact(sc, {k: x for k, x in v.items() if k not in ("objectType", "type")})
        if tname in ("FsmProperty", "FunctionCall", "FsmArray", "FsmTemplateControl", "LayoutOption", "FsmAnimationCurve", "AnimationCurve"):
            return compact(sc, v)
        return fsm_var(sc, v)
    except Exception as e:
        return f"<{tname} err {e!r}>"


def dump_fsm(sc, out, goid, fsm_tree, comp_tree):
    f = fsm_tree
    dv = f.get("dataVersion", 2)
    tmpl = comp_tree.get("fsmTemplate", {})
    tmpl_s = "" if not tmpl or tmpl.get("m_PathID", 0) == 0 else f"  template={sc.ref(tmpl)}"
    handles = [k[6:] for k, x in f.items() if k.startswith("handle") and x]
    out.append(f"\n=== FSM {sc.path(goid)} :: {q(f.get('name', ''))}  start={q(f.get('startState'))}{tmpl_s}"
               f"{'  handles=' + ','.join(handles) if handles else ''}"
               f"{'' if sc.active_in_hierarchy(goid) else '  [inactive]'}{'' if comp_tree.get('m_Enabled', 1) else '  [disabled]'}")
    ev = [("*" if e.get("isGlobal") else "") + e.get("name", "") for e in f.get("events", []) if not e.get("isSystemEvent")]
    if ev:
        out.append("  events: " + ", ".join(ev))
    vs = f.get("variables") or {}
    vparts = []
    for k, lst in vs.items():
        if not isinstance(lst, list) or k in ("categories", "variableCategoryIDs"):
            continue
        for v in lst:
            if isinstance(v, dict) and v.get("name"):
                val = v.get("value")
                if isinstance(val, dict) and "m_PathID" in val:
                    val = sc.ref(val)
                elif isinstance(val, float):
                    val = f"{val:g}"
                elif isinstance(val, dict):
                    val = compact(sc, val)
                vparts.append(f"{k.replace('Variables', '')} {v['name']}={val}")
    if vparts:
        out.append("  vars: " + "; ".join(vparts))
    for g in f.get("globalTransitions", []):
        out.append(f"  global: {g.get('fsmEvent', {}).get('name')} -> {g.get('toState')}")
    for st in f.get("states", []):
        out.append(f"  [{st.get('name')}]")
        for tr in st.get("transitions", []):
            out.append(f"     -> {tr.get('fsmEvent', {}).get('name')} => {tr.get('toState')}")
        ad = st.get("actionData") or {}
        ad["_bytes"] = bytes(ad.get("byteData") or [])
        names = ad.get("actionNames") or []
        starts = ad.get("actionStartIndex") or []
        nparams = len(ad.get("paramDataType") or [])
        for i, an in enumerate(names):
            a, b = starts[i], (starts[i + 1] if i + 1 < len(starts) else nparams)
            a, b = max(0, min(a, nparams)), max(0, min(b, nparams))
            ps = []
            for p in range(a, b):
                pn = ad["paramName"][p] if p < len(ad.get("paramName", [])) else f"p{p}"
                ps.append(f"{pn}={decode_param(sc, ad, p, dv)}")
            en = (ad.get("actionEnabled") or [1] * len(names))[i]
            cn = (ad.get("customNames") or [""] * len(names))[i]
            out.append(f"      {'' if en else '(off) '}{short(an)}{(' «' + cn + '»') if cn else ''}({', '.join(ps)})")


def collider_points(t):
    """2D 碰撞体在自身物体局部坐标里的外形点（已加 offset）：方框/胶囊取四角，圆取外接方框四角，多边形/边取全部顶点。"""
    off = t.get("m_Offset") or {}
    ox, oy = off.get("x", 0.0), off.get("y", 0.0)
    sz = t.get("m_Size") or {}
    if sz:
        hx, hy = sz.get("x", 0.0) / 2, sz.get("y", 0.0) / 2
        return [(ox + dx, oy + dy) for dx in (-hx, hx) for dy in (-hy, hy)]
    if "m_Radius" in t and "m_Points" not in t:
        r = t.get("m_Radius", 0.0)
        return [(ox + dx, oy + dy) for dx in (-r, r) for dy in (-r, r)]
    p = t.get("m_Points")
    paths = (p.get("m_Paths") or []) if isinstance(p, dict) else [p or []]
    return [(q.get("x", 0.0) + ox, q.get("y", 0.0) + oy) for path in paths for q in path]


def world_bounds(sc, trid, pts, list_max=0):
    """局部点（外形的所有角点）用 Scene.world 的矩阵换到世界坐标，给出包围盒；点数不超过 list_max 时把各点也列出来。"""
    a, b, c, d, tx, ty = sc.world(trid) if trid else (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
    xs, ys = [], []
    for px, py in pts:
        xs.append(a * px + b * py + tx)
        ys.append(c * px + d * py + ty)
    res = f" world x {min(xs):.1f}..{max(xs):.1f} y {min(ys):.1f}..{max(ys):.1f}"
    if len(pts) <= list_max:
        res += " [" + " ".join(f"({x:.1f},{y:.1f})" for x, y in zip(xs, ys)) + "]"
    return res


def dump(mono, bundle, out_dir):
    sc = Scene(mono, bundle)
    out = [f"# {sc.scene_name}  ({os.path.basename(bundle)})", ""]
    fsms = []
    out.append("## 物体（有脚本、碰撞体或 FSM 的）")
    for goid in sorted(sc.go, key=lambda g: sc.path(g)):
        g = sc.go[goid]
        comps, details, trig = [], [], []
        for cid in g["comps"]:
            obj = sc.objs.get(cid)
            if obj is None:
                continue
            tname = obj.type.name
            if tname in ("Transform", "RectTransform", "GameObject"):
                continue
            if tname == "MonoBehaviour":
                sname = short(sc.script(cid))
                comps.append(sname)
                t = sc.tree(cid)
                if sname == "PlayMakerFSM" and "fsm" in t:
                    fsms.append((goid, t["fsm"], t))
                elif not any(d.lower() in sname.lower() for d in DECOR):
                    body = {k: x for k, x in t.items() if k not in ("m_GameObject", "m_Script", "m_Name", "m_Enabled", "m_EditorHideFlags", "m_EditorClassIdentifier")}
                    s = compact(sc, body)
                    # 太长的截断要标出来（wave_scan 之类的工具看到就不出草稿）；BattleScene / BattleWave 工具要读全，不截。
                    if len(s) > 1500 and sname not in ("BattleScene", "BattleWave"):
                        s = s[:1500] + f" …(截断 / truncated +{len(s) - 1500})"
                    if s and s != "{}":
                        details.append(f"      {sname}{'' if t.get('m_Enabled', 1) else ' [disabled]'}: {s}")
            elif "Collider2D" in tname:
                t = sc.tree(cid)
                if t.get("m_IsTrigger"):
                    sz = t.get("m_Size") or {}
                    off = t.get("m_Offset") or {}
                    extra = f" size=({sz.get('x', 0):g},{sz.get('y', 0):g})" if sz else (f" r={t.get('m_Radius', 0):g}" if "m_Radius" in t else "")
                    pts = collider_points(t)
                    if "m_Points" in t:
                        extra += f" points={len(pts)}"
                    if pts:
                        extra += world_bounds(sc, g["tr"], pts, list_max=12 if "m_Points" in t else 0)
                    trig.append(f"{tname}{extra} off=({off.get('x', 0):g},{off.get('y', 0):g}){'' if t.get('m_Enabled', 1) else ' [disabled]'}")
                comps.append(tname + ("(trigger)" if t.get("m_IsTrigger") else ""))
            else:
                comps.append(tname)
        interesting = [c for c in comps if not any(d.lower() in c.lower() for d in DECOR)
                       and c not in ("SpriteRenderer", "MeshRenderer", "MeshFilter", "ParticleSystem", "ParticleSystemRenderer",
                                     "AudioSource", "Animator", "Rigidbody2D", "LineRenderer")]
        if not interesting and not trig:
            continue
        wx, wy = sc.world_pos(g["tr"])
        act = "" if sc.active_in_hierarchy(goid) else ("  [inactive]" if g["active"] else "  [self inactive]")
        out.append(f"  {sc.path(goid)}  @({wx:.1f},{wy:.1f}){act}  {{{', '.join(comps)}}}")
        for t in trig:
            out.append(f"      trigger: {t}")
        out.extend(details)
    out.append("")
    out.append(f"## FSM（{len(fsms)} 个）")
    for goid, f, t in fsms:
        dump_fsm(sc, out, goid, f, t)
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, sc.scene_name + ".txt")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    return path, len(fsms)


def main():
    import warnings
    warnings.filterwarnings("ignore")
    mono, out_dir = sys.argv[1:3]
    t0 = time.time()
    failed = []
    for b in sys.argv[3:]:
        try:
            p, n = dump(mono, b, out_dir)
            print(f"[Godhome] {os.path.basename(b)} -> {p} ({n} FSM, {time.time() - t0:.0f}s)", flush=True)
        except Exception:
            failed.append(os.path.basename(b))
            print(f"[Godhome] {os.path.basename(b)} FAILED\n{traceback.format_exc()[-1500:]}", flush=True)
    # 有失败的要让调用的人知道（Codex 审计 0.10.3：以前部分失败也正常退出）。
    print(f"[Godhome] done: {len(sys.argv) - 3 - len(failed)} ok, {len(failed)} failed{': ' + ', '.join(failed) if failed else ''}", flush=True)
    if failed:
        sys.exit(1)


if __name__ == "__main__":
    main()
