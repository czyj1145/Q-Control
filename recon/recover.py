# -*- coding: utf-8 -*-
import sys, time; sys.path.insert(0,'.')
from gmwmi import GMWMI
g = GMWMI()
def st(tag):
    r = g.call(0xFA00, 0x0200)
    print(f"  [{tag}] CPU={GMWMI.get_u32(r,4)}C GPU={GMWMI.get_u32(r,8)}C "
          f"FAN1={GMWMI.get_u32(r,12)} FAN2={GMWMI.get_u32(r,16)} KB={GMWMI.get_u32(r,20)} "
          f"raw={' '.join(f'{b:02X}' for b in r[:16])}")
    return r

print("=== 连续读取 3 次（间隔2秒），确认是否稳定 ===")
for i in range(3):
    st(f"read{i+1}"); time.sleep(2)

print("\n=== 恢复尝试1: UFA1/2/3 全部置 0 ===")
g.call(0xFA00, 0x0205, d0=0, d1=0, d2=0)
time.sleep(4); st("after UFA=0")

print("\n=== 恢复尝试2: HSRE 置 0 (0x0204, DAT2=1 -> ECWT 0) ===")
g.call(0xFA00, 0x0204, d0=1)
time.sleep(4); st("after HSRE=0")
