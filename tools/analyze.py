"""Cross-reference scan.jsonl: boss title stems / defeat flags -> scenes.
Usage: python -I analyze.py <scan.jsonl> <lang.json>
"""
import json
import sys
from collections import defaultdict

sys.stdout.reconfigure(encoding="utf-8")
scenes = [json.loads(l) for l in open(sys.argv[1], encoding="utf-8")]
lang = json.load(open(sys.argv[2], encoding="utf-8"))
titles = lang["Titles"]

BOSS_STEMS = """MOSSBONE_MOTHER BELLBEAST LACE SONG_GOLEM GIANT_BONE_FLYER SPLINTER_QUEEN SKULL_KING SPINNER DRILLERS
VAMPIRE_GNAT PHANTOM LAST_JUDGE DOCK_GUARD_DUO DOCK_GUARD_SOLO DOCK_GUARD_THROWER HUNTER_TRAPPER CHEF CROWFATHER WICKER_BUG SWAMP_SHAMAN
ZAPNEST_BOSS DRILLER_SOLO COGWORK_DANCERS TROBBIO TORMENTED_TROBBIO WARD_BOSS BROODMOTHER FIRST_WEAVER SONG_KNIGHT SILK
LOST_LACE CLOVER_DANCERS WHITE_CLOVERSTAG FLOWER_QUEEN CORAL_KING HUNTER_QUEEN SETH GARMOND GARMOND_BT BLUE_ASSISTANT
PINSTRESS GREY_CORAL_WARRIOR BIG_CENTIPEDE ABYSS_MASS MAPPER DICE_PILGRIM SNAIL_SHAMANS MOSSCREEP LARGE_COG_WORK
OLD_STATUE LAST_CONDUCTOR HUNTRESS STEEL_SENTINEL GREEN_PRINCE SHERMA ROOF_CRAB GIANT_FLEA""".split()


def name(stem):
    parts = [titles.get(f"{stem}_{s}", ["", ""]) for s in ("SUPER", "MAIN", "SUB")]
    return " ".join(p[0] for p in parts if p[0]), " ".join(p[1] for p in parts if p[1])


by_title = defaultdict(list)
by_flag = defaultdict(list)
for sc in scenes:
    for t in sc.get("titles", []):
        by_title[t].append(sc)
    for f in sc.get("flags", []):
        by_flag[f].append(sc["scene"] or sc["bundle"])

print("=== boss title stems -> scenes ===")
for stem in BOSS_STEMS:
    en, zh = name(stem)
    print(f"\n## {stem}  {en} / {zh}")
    for sc in by_title.get(stem, []):
        hp = {k: v for k, v in sc["health"].items()}
        gates = {k: f'{v["target"]}:{v["entry"]}' for k, v in sc["gates"].items()}
        dflags = [f for f in sc["flags"] if f.lower().startswith(("defeated", "encountered")) or "Defeated" in f or "Killed" in f]
        print(f'  {sc["scene"]}  hp={hp}  battle={sc["battle"]}')
        print(f'     gates={gates}')
        print(f'     flags={dflags}')
        if sc["fsms"]:
            print(f'     fsms={sc["fsms"][:12]}')

print("\n=== defeated*/encountered* flags -> scenes ===")
for f in sorted(by_flag):
    if f.lower().startswith(("defeated", "encountered")) or "Defeated" in f or "Killed" in f:
        print(f"{f}: {sorted(set(by_flag[f]))}")
