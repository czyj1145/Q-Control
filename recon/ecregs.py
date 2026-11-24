# -*- coding: utf-8 -*-
import re
d = open('acpi/DSDT_ALASKA_A_M_I__01072009_00000000_DSDT.aml','rb').read()
PATH = re.escape(b'\x5E\x5E\x2F\x04PC00LPCBEC0_')   # ^^/PC00LPCBEC0_
# 分支边界
bounds = [(0x0200,0x88B7F),(0x0201,0x88D41),(0x0202,0x88DBF),(0x0203,0x88E79),
          (0x0204,0x88F5E),(0x0205,0x89011),(0x0206,0x89032),(0x0207,0x890B1),
          (0x0300,0x89135),(0x0400,0x8916F),(0x0100,0x89194)]
end_all = 0x89A8A
bounds.sort(key=lambda x: x[1])
for i,(v,off) in enumerate(bounds):
    nxt = bounds[i+1][1] if i+1 < len(bounds) else end_all
    seg = d[off:nxt]
    regs = []
    for m in re.finditer(PATH, seg):
        nm = seg[m.end():m.end()+4]
        if re.fullmatch(rb'[A-Z0-9_]{4}', nm):
            regs.append(nm.decode())
    # 也找 EC 字段的 4 字名（不带路径前缀的，如 FS1H）
    freq = {}
    for r in regs: freq[r] = freq.get(r,0)+1
    print(f"GWF1=0x{v:04X}  @{off:X} ({nxt-off}B): " + ", ".join(f"{k}({c})" for k,c in sorted(freq.items(), key=lambda kv:-kv[1])))
