# -*- coding: utf-8 -*-
import sys
sys.path.insert(0, '.')
from aml_disasm import Disasm
d = open("acpi/DSDT_ALASKA_A_M_I__01072009_00000000_DSDT.aml",'rb').read()

def show(name, start, end):
    body = d[start:end]
    print(f"\n{'='*66}\n{name}  body 0x{start:X}..0x{end:X}  ({len(body)} bytes)\n{'='*66}")
    # hex + ascii
    for i in range(0, len(body), 16):
        chunk = body[i:i+16]
        h = ' '.join(f'{b:02X}' for b in chunk)
        a = ''.join(chr(b) if 32 <= b < 127 else '.' for b in chunk)
        print(f"  {start+i:06X}  {h:<48}  {a}")
    print("  --- 反编译 ---")
    dis = Disasm(body); dis.run(len(body))
    print("\n".join("  " + l for l in dis.out))

show("WQAA", 0x88ABA, 0x88AD9)
