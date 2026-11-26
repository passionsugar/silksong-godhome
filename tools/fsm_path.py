"""[Godhome] 读 dump_fsm.py 的输出，从某个 FSM 的起始状态沿跳转走几层，看它会停在哪个状态、等什么事件。

用法: python -I fsm_path.py <dump.txt> <FSM 标题正则> [层数=6] [每个动作最多字符=220]
"""
import os
import re
import sys
from collections import deque

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from dumpfmt import parse_fsm_head  # noqa: E402


def parse(path, header_re):
    lines = open(path, encoding="utf-8").read().splitlines()
    rx = re.compile(header_re)
    i = next((k for k, l in enumerate(lines) if l.startswith("=== FSM") and rx.search(l)), None)
    if i is None:
        sys.exit(f"no FSM matches {header_re!r}")
    header = lines[i]
    # 起始状态按导出格式解（两种引号、转义都认；读不懂就报错，Codex 审计 0.10.3 PT04）。
    start = parse_fsm_head(header, f"{path}:{i + 1}")[2]
    states, glob, cur, extra = {}, [], None, []
    for l in lines[i + 1:]:
        if l.startswith("=== FSM") or l.startswith("## "):
            break
        m = re.match(r"^  \[(.*)\]$", l)
        if m:
            cur = {"name": m.group(1), "tr": [], "act": []}
            states[cur["name"]] = cur
            continue
        m = re.match(r"^  global: (.*) -> (.*)$", l)
        if m:
            glob.append((m.group(1), m.group(2)))
            continue
        if cur is None:
            extra.append(l)
            continue
        m = re.match(r"^     -> (.*) => (.*)$", l)
        if m:
            cur["tr"].append((m.group(1), m.group(2)))
        elif l.startswith("      "):
            cur["act"].append(l.strip())
    return header, start, states, glob, extra


def main():
    path, header_re = sys.argv[1:3]
    depth = int(sys.argv[3]) if len(sys.argv) > 3 else 6
    width = int(sys.argv[4]) if len(sys.argv) > 4 else 220
    header, start, states, glob, extra = parse(path, header_re)
    if len(sys.argv) > 5:
        start = sys.argv[5]  # 从指定状态开始走
        extra = []
    print(header)
    for e in extra:
        print(e[:600])
    for ev, to in glob:
        print(f"  global {ev} -> {to}")
    seen, q = {start: 0}, deque([start])
    while q:
        s = q.popleft()
        d = seen[s]
        st = states.get(s)
        if st is None:
            print(f"{'  ' * d}[{s}] (missing)")
            continue
        print(f"{'  ' * d}[{s}]  d={d}")
        for a in st["act"]:
            print(f"{'  ' * d}    . {a[:width]}")
        for ev, to in st["tr"]:
            mark = "" if to not in seen else " (seen)"
            print(f"{'  ' * d}    -> {ev} => {to}{mark}")
            if to not in seen and d + 1 <= depth:
                seen[to] = d + 1
                q.append(to)


if __name__ == "__main__":
    main()
