"""Merge EN/ZH localisation sheets into one JSON: {sheet: {key: [en, zh]}}.
Also print the title-card table (Titles sheet) grouped by key stem.
Usage: python -I lang_table.py <langdir> <out.json>
"""
import html
import json
import os
import re
import sys

ENTRY = re.compile(r'<entry name="([^"]+)">(.*?)</entry>', re.S)


def load(path):
    with open(path, encoding="utf-8") as f:
        return {k: html.unescape(v) for k, v in ENTRY.findall(f.read())}


def main():
    d, out = sys.argv[1], sys.argv[2]
    result = {}
    for fn in sorted(os.listdir(d)):
        if not fn.startswith("EN_"):
            continue
        sheet = fn[3:-4]
        en = load(os.path.join(d, fn))
        zh_path = os.path.join(d, "ZH_" + sheet + ".txt")
        zh = load(zh_path) if os.path.exists(zh_path) else {}
        result[sheet] = {k: [en.get(k, ""), zh.get(k, "")] for k in sorted(set(en) | set(zh))}
    with open(out, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    print("sheets:", {k: len(v) for k, v in result.items()})


if __name__ == "__main__":
    main()
