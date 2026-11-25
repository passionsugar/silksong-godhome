import re, sys
p, pat = sys.argv[1], sys.argv[2].encode()
raw = open(p, "rb").read()
seen = set()
for m in re.finditer(re.escape(pat), raw):
    a = max(0, m.start() - 60); b = m.end() + 80
    s = re.sub(rb"[^ -~]", b".", raw[a:b]).decode()
    k = s[40:120]
    if k in seen: continue
    seen.add(k); print(s)
    if len(seen) >= 25: break
