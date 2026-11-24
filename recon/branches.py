# -*- coding: utf-8 -*-
import re
d = open('acpi/DSDT_ALASKA_A_M_I__01072009_00000000_DSDT.aml','rb').read()
S, E = 0x88AE1, 0x89A8A
ws = d[S:E]

# 定位每个 GWF1 分支起点
marks = []
for m in re.finditer(re.escape(b'\x93GWF1'), ws):
    o = m.start() + 5
    if ws[o] == 0x0B:
        v = int.from_bytes(ws[o+1:o+3], 'little')
        marks.append((S + m.start(), v))
    elif ws[o] == 0x0A:
        marks.append((S + m.start(), ws[o+1]))
marks.sort()
seen = {}
for off, v in marks:
    if v in seen: continue
    seen[v] = off

keys = sorted(seen.items(), key=lambda kv: kv[1])
for i, (v, off) in enumerate(keys):
    nxt = keys[i+1][1] if i+1 < len(keys) else E
    seg = d[off:min(nxt, off+1400)]
    names = re.findall(rb'\b([A-Z][A-Z0-9_]{3})\b', seg)
    freq = {}
    for n in names:
        s = n.decode()
        freq[s] = freq.get(s, 0) + 1
    top = sorted(freq.items(), key=lambda kv: -kv[1])[:16]
    print(f"\n--- GWF1 = 0x{v:04X}  (0x{off:X}, {nxt-off} bytes) ---")
    print("    " + ", ".join(f"{k}({c})" for k, c in top))
