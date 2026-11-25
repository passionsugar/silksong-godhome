"""List journal records: asset name, recordType, killsRequired, EN/ZH display name.
Usage: python -I journal_list.py <journal.json> <lang.json>
"""
import json
import sys

sys.stdout.reconfigure(encoding="utf-8")
recs = json.load(open(sys.argv[1], encoding="utf-8"))
lang = json.load(open(sys.argv[2], encoding="utf-8"))


def text(ls):
    sheet, key = ls.get("Sheet", ""), ls.get("Key", "")
    en, zh = lang.get(sheet, {}).get(key, ["", ""])
    return f"{key}\t{en}\t{zh}"


for r in sorted(recs, key=lambda r: (r["recordType"], r["m_Name"])):
    print(f'{r["recordType"]}\t{r["killsRequired"]}\t{r["requiredType"]}\t{r["m_Name"]}\t{text(r["displayName"])}')
