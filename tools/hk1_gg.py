"""[Godhome] 只读分析前作（空洞骑士）神居场景是怎么搭的：只查数据结构，不导出、不使用任何前作素材（安全规则 5）。

1. globalgamemanagers 里的场景清单（按 build index）：GG_ 开头的有哪些。
2. 选几对“神居场景 / 原场景”，比较物体数、根物体、脚本组件、物体名重合度、门（TransitionPoint）。
3. 全部 level 文件里搜 FSM 动作名（GGCheckIfBossScene 等）：神居分支写在哪些场景的 FSM 里。
4. 同名 Boss 物体上的 PlayMakerFSM：神居版和原版的字符串（状态名 / 事件名 / 动作名）差异。

用法: python -I hk1_gg.py <hollow_knight_Data> <out.md>
"""
import os
import re
import sys
import time
from collections import Counter, defaultdict

import UnityPy

DATA = sys.argv[1]
OUT = sys.argv[2]
lines = []
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


def out(s=""):
    lines.append(s)
    print(s, flush=True)


# ───────────── 1. 场景清单 ─────────────
def scene_list():
    raw = open(os.path.join(DATA, "globalgamemanagers"), "rb").read()
    # BuildSettings.scenes：按顺序的 "Assets/....unity" 字符串
    names = [m.group(0).decode("utf-8", "replace") for m in re.finditer(rb"Assets/[ -~]{1,200}?\.unity", raw)]
    seen, ordered = set(), []
    for n in names:
        if n not in seen:
            seen.add(n)
            ordered.append(n)
    return ordered


scenes = scene_list()
name_of = [os.path.splitext(os.path.basename(p))[0] for p in scenes]
index_of = {n: i for i, n in enumerate(name_of)}
gg = [(i, p) for i, p in enumerate(scenes) if os.path.basename(p).startswith("GG_")]
out(f"# 前作神居场景分析（只读）\n")
out(f"## 1. 场景清单\n")
out(f"- build 场景共 {len(scenes)} 个，`GG_` 开头的 {len(gg)} 个")
folders = Counter(os.path.dirname(p) for _, p in gg)
out(f"- GG 场景所在文件夹：" + "，".join(f"`{k}` {v}" for k, v in folders.most_common()))
out("")
out("| index | 场景 | 路径 |")
out("|---|---|---|")
for i, p in gg:
    out(f"| {i} | {os.path.basename(p)[:-6]} | `{p}` |")
out("")


# ───────────── MonoScript 名字 ─────────────
def load_scripts():
    m = {}
    for fn in ("globalgamemanagers.assets",):
        env = UnityPy.load(os.path.join(DATA, fn))
        for obj in env.objects:
            if obj.type.name == "MonoScript":
                try:
                    d = obj.read()
                    m[(fn, obj.path_id)] = getattr(d, "m_ClassName", None) or getattr(d, "name", "?")
                except Exception:
                    pass
    return m


t0 = time.time()
SCRIPTS = load_scripts()
out(f"(MonoScript {len(SCRIPTS)} 个，{time.time() - t0:.1f}s)\n")


class Level:
    def __init__(self, idx):
        self.idx = idx
        self.name = name_of[idx]
        self.env = UnityPy.load(os.path.join(DATA, f"level{idx}"))
        self.file = list(self.env.files.values())[0]
        self.externals = [os.path.basename(e.path) for e in getattr(self.file, "externals", [])]
        self.go = {}        # path_id -> name
        self.parent = {}    # go path_id -> parent go path_id
        self.comps = defaultdict(list)  # go path_id -> [class name]
        self.mb_raw = defaultdict(list)  # go path_id -> [(script, raw bytes)]
        tr_go, tr_father = {}, {}
        for obj in self.env.objects:
            t = obj.type.name
            try:
                if t == "GameObject":
                    self.go[obj.path_id] = obj.read().m_Name
                elif t in ("Transform", "RectTransform"):
                    d = obj.read()
                    tr_go[obj.path_id] = d.m_GameObject.path_id
                    tr_father[obj.path_id] = d.m_Father.path_id
                elif t == "MonoBehaviour":
                    # 没有类型树：直接按 Unity 2017 的布局读开头的字段
                    # m_GameObject PPtr(int32 fileID, int64 pathID) / m_Enabled uint8 + 对齐 / m_Script PPtr
                    raw = obj.get_raw_data()
                    import struct as _st
                    g_fid, gid = _st.unpack_from("<iq", raw, 0)
                    s_fid, s_pid = _st.unpack_from("<iq", raw, 16)
                    fn = self.externals[s_fid - 1] if s_fid > 0 and s_fid - 1 < len(self.externals) else "(self)"
                    cls = SCRIPTS.get((fn, s_pid), f"?{fn}:{s_pid}")
                    self.comps[gid].append(cls)
                    if cls == "PlayMakerFSM":
                        self.mb_raw[gid].append(raw)
                elif t not in ("GameObject",):
                    d = obj.read() if t in ("BoxCollider2D", "PolygonCollider2D", "CircleCollider2D", "EdgeCollider2D") else None
                    if d is not None:
                        self.comps[d.m_GameObject.path_id].append(t)
            except Exception:
                pass
        for tid, gid in tr_go.items():
            f = tr_father.get(tid, 0)
            self.parent[gid] = tr_go.get(f, 0) if f else 0

    def path(self, gid):
        parts = []
        while gid and gid in self.go and len(parts) < 30:
            parts.append(self.go[gid])
            gid = self.parent.get(gid, 0)
        return "/".join(reversed(parts))

    def roots(self):
        return sorted(self.go[g] for g in self.go if not self.parent.get(g))

    def script_counts(self):
        c = Counter()
        for lst in self.comps.values():
            c.update(x for x in lst if not x.startswith("?"))
        return c

    def find(self, name):
        return [g for g, n in self.go.items() if n == name]


def strings(raw):
    return set(m.group(0).decode() for m in re.finditer(rb"[A-Za-z][ -~]{2,60}", raw))


PAIRS = [
    ("GG_Hornet_1", "Fungus1_04", "Hornet Boss 1"),
    ("GG_False_Knight", "Crossroads_10", "False Knight New"),
    ("GG_Mega_Moss_Charger", "Fungus1_29", "Mega Moss Charger"),
    ("GG_Mantis_Lords", "Fungus2_15", "Mantis Lord"),
    ("GG_Soul_Master", "Ruins1_24", "Mage Lord"),
    ("GG_Nosk", "Deepnest_32", "Mimic Spider"),
    ("GG_Broken_Vessel", "Abyss_19", "Infected Knight"),
    ("GG_Traitor_Lord", "Fungus3_23", "Mantis Traitor Lord"),
    ("GG_Dung_Defender", "Waterways_05", "Dung Defender"),
    ("GG_Hollow_Knight", "Room_Final_Boss_Core", "Hollow Knight Boss"),
]

GG_SCRIPTS = ("BossSceneController", "BossStatue", "BossSequenceDoor", "BossChallengeUI", "BossDoorChallengeUI",
              "BossSummaryBoard", "BossStatueLever", "BossStatueDreamToggle", "BossSequenceController")

def first_strings(raw, n=14):
    res = []
    for m in re.finditer(rb"[A-Za-z][ -~]{2,60}", raw):
        s = m.group(0).decode()
        if s not in res:
            res.append(s)
        if len(res) >= n:
            break
    return res


def describe_prefab(lv, root_name, depth=2):
    """列出某个根物体下面的子物体（两层）、各自的脚本，和 PlayMakerFSM 开头的字符串（FSM 名、状态名）。"""
    roots = [g for g in lv.go if lv.go[g] == root_name and not lv.parent.get(g)]
    if not roots:
        return
    kids = defaultdict(list)
    for g, p in lv.parent.items():
        kids[p].append(g)

    def walk(g, d):
        scripts = [c for c in lv.comps[g] if not c.startswith("?")]
        out(f"  {'  ' * d}- `{lv.go[g]}`" + (f"：{', '.join(scripts)}" if scripts else ""))
        for raw in lv.mb_raw.get(g, []):
            out(f"  {'  ' * d}  - FSM 字符串：" + "，".join(f"`{x}`" for x in first_strings(raw)))
        if d < depth:
            for c in sorted(kids.get(g, []), key=lambda x: lv.go.get(x, "")):
                walk(c, d + 1)

    walk(roots[0], 0)


out("## 2. 神居场景和原场景对比\n")
for ggn, orig, boss in PAIRS:
    if orig + "_boss" in index_of:
        orig = orig + "_boss"
    if ggn not in index_of or orig not in index_of:
        out(f"- 跳过 {ggn} / {orig}：清单里没有\n")
        continue
    t0 = time.time()
    a, b = Level(index_of[ggn]), Level(index_of[orig])
    an, bn = set(a.go.values()), set(b.go.values())
    common = an & bn
    out(f"### {ggn}（{len(a.go)} 个物体）↔ {orig}（{len(b.go)} 个物体）  [{time.time() - t0:.1f}s]\n")
    out(f"- 物体名重合：{len(common)} 个；神居场景里有、原场景没有的 {len(an - bn)} 个，原场景有、神居没有的 {len(bn - an)} 个")
    out(f"- 神居场景根物体（{len(a.roots())}）：" + "，".join(f"`{r}`" for r in a.roots()[:60]))
    out(f"- 原场景根物体（{len(b.roots())}）：" + "，".join(f"`{r}`" for r in b.roots()[:60]))
    sa, sb = a.script_counts(), b.script_counts()
    out(f"- 神居场景的神居组件：" + ("，".join(f"`{k}`×{sa[k]}" for k in GG_SCRIPTS if sa.get(k)) or "无"))
    for g in a.go:
        if "BossSceneController" in a.comps[g]:
            out(f"  - `BossSceneController` 在 `{a.path(g)}`")
    tp_a = [a.path(g) for g in a.go if "TransitionPoint" in a.comps[g]]
    tp_b = [b.path(g) for g in b.go if "TransitionPoint" in b.comps[g]]
    out(f"- 门（TransitionPoint）：神居 {len(tp_a)} 个 " + "，".join(f"`{p}`" for p in tp_a[:12]) + f"；原场景 {len(tp_b)} 个")
    only_a = sorted((k, v) for k, v in sa.items() if k not in sb)
    only_b = sorted((k, v) for k, v in sb.items() if k not in sa)
    out(f"- 只在神居场景里的脚本：" + ("，".join(f"`{k}`×{v}" for k, v in only_a[:40]) or "无"))
    out(f"- 只在原场景里的脚本：" + ("，".join(f"`{k}`×{v}" for k, v in only_b[:40]) or "无"))
    ga, gb = a.find(boss), b.find(boss)
    out(f"- Boss 物体 `{boss}`：神居 {len(ga)} 个（{', '.join(a.path(g) for g in ga[:3])}），原场景 {len(gb)} 个（{', '.join(b.path(g) for g in gb[:3])}）")
    if ga and gb:
        fa = [strings(r) for r in a.mb_raw[ga[0]]]
        fb = [strings(r) for r in b.mb_raw[gb[0]]]
        out(f"  - 身上的 PlayMakerFSM：神居 {len(fa)} 个，原版 {len(fb)} 个")
        sa_all = set().union(*fa) if fa else set()
        sb_all = set().union(*fb) if fb else set()
        add = sorted(x for x in sa_all - sb_all if len(x) < 50)
        rem = sorted(x for x in sb_all - sa_all if len(x) < 50)
        out(f"  - FSM 字符串只在神居版里的（{len(add)}）：" + "，".join(f"`{x}`" for x in add[:80]))
        out(f"  - FSM 字符串只在原版里的（{len(rem)}）：" + "，".join(f"`{x}`" for x in rem[:80]))
    if ggn in ("GG_Hornet_1", "GG_Mantis_Lords"):
        out(f"- `{ggn}` 里的共用预制体：")
        describe_prefab(a, "Boss Scene Controller", 1)
        describe_prefab(a, "GG_Arena_Prefab", 1)
    out("")
    del a, b

# ───────────── 3. GG 动作在哪些场景 ─────────────
out("## 3. 神居相关 FSM 动作 / 字样出现在哪些场景（原始字节搜索）\n")
PATS = [b"GGCheckIfBossScene", b"GGCheckIfBossSequence", b"GGCheckBoundSoul", b"GGCheckBoundNail", b"GGCheckBoundHeart",
        b"GGCheckBoundCharms", b"CheckGGBossLevel", b"EndGGBossScene", b"GGWaitForBossSceneTransitionIn",
        b"GGSetCanTransition", b"GGCheckIfFirstBossScene", b"GGCheckIfLastBossScene", b"GGCheckIsBossRushMode",
        b"bossRushMode", b"door_dreamEnter", b"GG_Workshop", b"GG_Atrium"]
hits = defaultdict(Counter)
t0 = time.time()
for i in range(len(scenes)):
    p = os.path.join(DATA, f"level{i}")
    if not os.path.exists(p):
        continue
    raw = open(p, "rb").read()
    for pat in PATS:
        c = raw.count(pat)
        if c:
            hits[pat][name_of[i]] += c
out(f"(搜了 {len(scenes)} 个 level 文件，{time.time() - t0:.1f}s)\n")
for pat in PATS:
    c = hits.get(pat, Counter())
    g = sum(1 for k in c if k.startswith("GG_"))
    o = [k for k in c if not k.startswith("GG_")]
    out(f"- `{pat.decode()}`：{len(c)} 个场景（GG_ {g} 个，其他 {len(o)} 个）" + ("；其他：" + "，".join(f"`{k}`" for k in sorted(o)[:30]) if o else ""))
out("")

with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print("written", OUT)
