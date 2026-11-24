# -*- coding: utf-8 -*-
import re, sys
d = open("acpi/DSDT_ALASKA_A_M_I__01072009_00000000_DSDT.aml",'rb').read()

def pkg_len(data, pos):
    lead = data[pos]; bc = lead >> 6; p = pos+1
    if bc == 0: return lead & 0x3F, p
    ln = lead & 0x0F
    for i in range(bc):
        ln |= data[p] << (4 + 8*i); p += 1
    return ln, p

targets = [b'WQAA', b'WSAA', b'GWSS', b'GWF0', b'GWF1', b'GWD0', b'GWD1', b'GWD2', b'GWD3']
for t in targets:
    for m in re.finditer(re.escape(t), d):
        off = m.start()
        # 往前找 MethodOp 0x14
        for back in range(1, 9):
            if off-back >= 0 and d[off-back] == 0x14:
                pl_start = off-back+1
                ln, p = pkg_len(d, pl_start)
                # MethodOp: 0x14 + PkgLength + NameString(4) + flags(1)
                body_start = p + 5
                end = pl_start + ln
                if end > len(d) or body_start > end: continue
                print(f"{t.decode()} @0x{off:X}  MethodOp@0x{off-back:X} len={ln} body=0x{body_start:X}..0x{end:X} flags=0x{d[p+4]:02X}")
                break
