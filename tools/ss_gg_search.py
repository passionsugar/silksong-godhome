"""[Godhome] 只读：在丝之歌的数据文件里找前作神居的预制体 / 资源名还在不在。

不解包资源包内容，只搜：
1. Data 目录下未压缩的文件（globalgamemanagers、resources.assets、sharedassets*.assets）的原始字节；
2. Addressables 目录（catalog、settings）的原始字节；
3. 场景资源包的文件名（aa 目录下的 .bundle 文件名带场景名）。

用法: python -I ss_gg_search.py <Hollow Knight Silksong_Data> <out.md>
"""
import os
import re
import sys

DATA, OUT = sys.argv[1], sys.argv[2]
PATS = [b"door_dreamEnter", b"Dream Enter Next Scene", b"Boss Scene Controller", b"GG_Arena", b"Godseeker",
        b"BossChallengeUI", b"BossDoorChallengeUI", b"BossSceneController", b"BossStatue", b"GG_Workshop",
        b"GG_Atrium", b"GG_End_Sequence", b"bossSceneToLoad", b"Dream Return", b"GG/", b"gg_"]
lines = []


def out(s=""):
    lines.append(s)


files = []
for root, dirs, fns in os.walk(DATA):
    for fn in fns:
        p = os.path.join(root, fn)
        files.append(p)
bundles = [p for p in files if p.endswith(".bundle")]
plain = [p for p in files if not p.endswith(".bundle") and os.path.getsize(p) < 600 * 1024 * 1024
         and not p.endswith((".resS", ".resource", ".dll", ".wav", ".bank"))]
out(f"# 丝之歌数据里有没有前作神居的东西（只读搜索）\n")
out(f"- Data 目录文件 {len(files)} 个，其中资源包 {len(bundles)} 个（LZ4 压缩，只看文件名），其余未压缩文件搜了 {len(plain)} 个\n")
hits = {}
for p in plain:
    try:
        raw = open(p, "rb").read()
    except Exception:
        continue
    for pat in PATS:
        c = raw.count(pat)
        if c:
            hits.setdefault(pat, []).append((os.path.relpath(p, DATA), c))
for pat in PATS:
    h = hits.get(pat, [])
    out(f"- `{pat.decode()}`：" + ("，".join(f"`{f}`×{c}" for f, c in h[:12]) if h else "没有"))
out("")
gg_b = [os.path.basename(p) for p in bundles if re.search(r"(^|[_/\\])gg_|godhome|bossrush|boss_rush|gods_glory", os.path.basename(p), re.I)]
out(f"- 资源包文件名里带 gg_ / godhome / bossrush / gods_glory 的：{len(gg_b)} 个 " + "，".join(f"`{b}`" for b in gg_b[:20]))
scene_b = [os.path.basename(p) for p in bundles if "scenes_scenes" in os.path.basename(p).lower()]
out(f"- 场景资源包 {len(scene_b)} 个")
with open(OUT, "w", encoding="utf-8") as f:
    f.write("\n".join(lines) + "\n")
print("written", OUT)
