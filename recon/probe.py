# -*- coding: utf-8 -*-
import sys; sys.path.insert(0,'.')
from gmwmi import GMWMI, dump
g = GMWMI()
print("基准（0xFA00/0x0200 状态）:")
base = g.call(0xFA00, 0x0200); dump(base, "base")

subs = [0x0100,0x0200,0x0201,0x0202,0x0203,0x0204,0x0205,0x0206,0x0207,0x0300,0x0400]
for cmd in (0xFA00, 0xFB00):
    print(f"\n########## GWF0=0x{cmd:04X} ##########")
    for s in subs:
        try:
            r = g.call(cmd, s)
            d0,d1 = GMWMI.get_u16(r,0), GMWMI.get_u16(r,2)
            d2,d3,d4,d5,d6 = (GMWMI.get_u32(r,4),GMWMI.get_u32(r,8),
                              GMWMI.get_u32(r,12),GMWMI.get_u32(r,16),GMWMI.get_u32(r,20))
            mark = ""
            if (d2,d3,d4,d5,d6) != (0,0,0,0,0): mark = "  <<< 有数据"
            print(f"  GWF1=0x{s:04X} -> DAT0=0x{d0:04X} DAT1=0x{d1:04X} "
                  f"DAT2=0x{d2:08X}({d2}) DAT3=0x{d3:04X}({d3}) "
                  f"DAT4=0x{d4:04X}({d4}) DAT5=0x{d5:04X}({d5}) DAT6=0x{d6:04X}({d6}){mark}")
        except Exception as e:
            print(f"  GWF1=0x{s:04X} -> 失败 {e}")
