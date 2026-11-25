"""Build names.json for scan_scenes.py: PlayerData bool/int field names + Titles stems.
Usage: python -I make_names.py <PlayerData.cs> <lang.json> <out.json>
"""
import json
import re
import sys

src = open(sys.argv[1], encoding="utf-8").read()
fields = sorted(set(re.findall(r"public (?:bool|int|string) (\w+);", src)))
# keep progress-ish fields only (too-generic names would create noise)
keep = [f for f in fields if re.search(r"defeat|encounter|killed|boss|Boss|Defeated|Killed|Encountered|met|Met|seen|Seen|completed|Completed|visited|Visited|unlocked|Unlocked|has[A-Z]|act3|Act3|blackThread|BlackThread", f)]
lang = json.load(open(sys.argv[2], encoding="utf-8"))
stems = sorted({re.sub(r"_(SUPER|MAIN|SUB)$", "", k) for k in lang["Titles"]})
json.dump({"flags": keep, "titles": stems}, open(sys.argv[3], "w", encoding="utf-8"), indent=0)
print("flags", len(keep), "of", len(fields), "titles", len(stems))
