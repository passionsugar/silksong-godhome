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
    # 表名取 EN_ 和 ZH_ 两边的并集，只认普通的 .txt 文件（Codex 审计 0.10.3 PT06：以前只看 EN_，只有中文的表整张漏掉）。
    sheets = set()
    for fn in os.listdir(d):
        if fn.endswith(".txt") and fn[:3] in ("EN_", "ZH_") and os.path.isfile(os.path.join(d, fn)):
            sheets.add(fn[3:-4])
    if os.path.abspath(out) in {os.path.abspath(os.path.join(d, p + s + ".txt")) for s in sheets for p in ("EN_", "ZH_")}:
        sys.exit("[Godhome] 输出文件不能是输入的语言表 / output must not be one of the input sheets")
    result = {}
    only = {"EN": [], "ZH": []}
    for sheet in sorted(sheets):
        en_path = os.path.join(d, "EN_" + sheet + ".txt")
        zh_path = os.path.join(d, "ZH_" + sheet + ".txt")
        en = load(en_path) if os.path.isfile(en_path) else {}
        zh = load(zh_path) if os.path.isfile(zh_path) else {}
        if not os.path.isfile(zh_path):
            only["EN"].append(sheet)
        if not os.path.isfile(en_path):
            only["ZH"].append(sheet)
        result[sheet] = {k: [en.get(k, ""), zh.get(k, "")] for k in sorted(set(en) | set(zh))}
    tmp = out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    os.replace(tmp, out)
    print("sheets:", {k: len(v) for k, v in result.items()})
    for lang, names in only.items():
        if names:
            print(f"only {lang}: {', '.join(names)}")


if __name__ == "__main__":
    main()
