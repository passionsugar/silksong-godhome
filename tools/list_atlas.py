"""[Godhome] 列出游戏图集包里的精灵名（只读游戏文件，不改）。
用法: python -I list_atlas.py <bundles_dir> <out_dir> <atlas> [<atlas> ...] [--png <name_substring> ...]
每个图集输出 <out_dir>/<atlas>.txt（精灵名 宽x高）；--png 后面的子串匹配到的精灵另存为 PNG 到 <out_dir>/png/。
"""
import os
import sys

import UnityPy

UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"


def main():
    bdir, out = sys.argv[1], sys.argv[2]
    args = sys.argv[3:]
    pngs = []
    if "--png" in args:
        i = args.index("--png")
        pngs = [a.lower() for a in args[i + 1:]]
        args = args[:i]
    os.makedirs(out, exist_ok=True)
    os.makedirs(os.path.join(out, "png"), exist_ok=True)
    sub = os.path.join(bdir, "atlases_assets_assets", "sprites", "_atlases")
    for atlas in args:
        path = None
        for f in os.listdir(sub):
            if f.lower().startswith(atlas.lower() + ".spriteatlas"):
                path = os.path.join(sub, f)
                break
        if path is None:
            print(f"{atlas}: not found")
            continue
        env = UnityPy.load(path)
        rows = []
        for obj in env.objects:
            if obj.type.name != "Sprite":
                continue
            try:
                d = obj.read()
                name = d.m_Name
                r = d.m_Rect
                rows.append((name, int(r.width), int(r.height)))
                if pngs and any(p in name.lower() for p in pngs):
                    try:
                        d.image.save(os.path.join(out, "png", f"{atlas}__{name}.png"))
                    except Exception as e:  # noqa: BLE001
                        print(f"  png fail {name}: {e}")
            except Exception as e:  # noqa: BLE001
                print(f"  read fail: {e}")
        rows.sort()
        with open(os.path.join(out, atlas + ".txt"), "w", encoding="utf-8") as fh:
            for n, w, h in rows:
                fh.write(f"{n}\t{w}x{h}\n")
        print(f"{atlas}: {len(rows)} sprites")


if __name__ == "__main__":
    main()
