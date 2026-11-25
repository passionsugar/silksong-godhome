"""Dump EnemyJournalRecord assets (typetree) from journalrecords.bundle.
Usage: python -I journal.py <bundle> <lang.json> <out.json>
"""
import json
import sys

import UnityPy

sys.stdout.reconfigure(encoding="utf-8")
UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.50f1"
env = UnityPy.load(sys.argv[1])
lang = json.load(open(sys.argv[2], encoding="utf-8"))
out = []
first = True
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    try:
        tree = obj.read_typetree()
    except Exception as e:
        print("no typetree:", e)
        break
    if first:
        print("fields:", list(tree.keys()))
        first = False
    out.append(tree)
json.dump(out, open(sys.argv[3], "w", encoding="utf-8"), ensure_ascii=False, indent=1, default=str)
print("records:", len(out))
