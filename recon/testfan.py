# -*- coding: utf-8 -*-
import sys, time; sys.path.insert(0,'.')
from gmwmi import GMWMI
g = GMWMI()
def status(tag):
    r = g.call(0xFA00, 0x0200)
    print(f"  [{tag}] CPU={GMWMI.get_u32(r,4)}C GPU={GMWMI.get_u32(r,8)}C "
          f"FAN1={GMWMI.get_u32(r,12)}RPM FAN2={GMWMI.get_u32(r,16)}RPM KB={GMWMI.get_u32(r,20)}")
    return r

print("=== 基线 ===")
status("baseline")

print("\n=== 尝试读取性能/散热相关 ===")
for sub in (0x0202, 0x0203, 0x0204, 0x0206, 0x0207, 0x0300, 0x0400):
    try:
        r = g.call(0xFA00, sub)
        print(f"  0x{sub:04X}: DAT2={GMWMI.get_u32(r,4)} DAT3={GMWMI.get_u32(r,8)} "
              f"DT3A={r[8]} DT3B={r[9]} DT3C={r[10]} DT3D={r[11]} DAT4={GMWMI.get_u32(r,12)}")
    except Exception as e:
        print(f"  0x{sub:04X}: err {e}")

print("\n=== 测试风扇手动 60% (GWF1=0x0205, GWD0=60) ===")
try:
    r = g.call(0xFA00, 0x0205, d0=60, d1=60)
    print("  写入后:", " ".join(f"{b:02X}" for b in r[:16]))
except Exception as e:
    print("  失败:", e)
time.sleep(3)
status("after 60%")

print("\n=== 恢复：风扇 0 (交给 EC 自动) ===")
try:
    g.call(0xFA00, 0x0205, d0=0, d1=0)
except Exception as e:
    print("  失败:", e)
time.sleep(3)
status("restored")
