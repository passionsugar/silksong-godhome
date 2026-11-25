"""Search lang.json for EN terms (case-insensitive, whole entry shorter than 60 chars) and print EN | ZH.
Usage: python -I lookup.py <lang.json> term1 term2 ...
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
lang = json.load(open(sys.argv[1], encoding="utf-8"))
for term in sys.argv[2:]:
    seen = set()
    hits = []
    for sheet, entries in lang.items():
        for key, (en, zh) in entries.items():
            if term.lower() in en.lower() and len(en) <= 60 and (en, zh) not in seen:
                seen.add((en, zh))
                hits.append(f"   [{sheet}/{key}] {en} | {zh}")
    print(f"== {term}: {len(hits)}")
    print("\n".join(hits[:8]))
