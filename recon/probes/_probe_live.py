"""直接读真机 HWiNFO 共享内存（只读，不需管理员）。"""
import os, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))
import hwinfo

out = []
d = hwinfo._read_raw()
out.append("_read_raw() -> %s" % ("None" if d is None else
    "sensors=%d readings=%d layout=%s s_elem=%s r_elem=%s ver=%s rev=%s poll=%s" % (
        len(d["sensors"]), len(d["readings"]), d["layout"], d["s_elem"],
        d["r_elem"], d["version"], d["revision"], d["poll"])))
v, src = hwinfo.find_pch()
out.append("find_pch() -> value=%s  source=%s" % (v, src))
out.append("read()      -> %s" % hwinfo.read())
out.append("")
out.append("温度读数（前 25）:")
for t in hwinfo.list_temps(25):
    out.append("   %-38s %-30s %s %s" % (t["group"], t["label"], t["value"], t["unit"]))
out.append("")
out.append("传感器组（全部）:")
if d:
    for i, s in enumerate(d["sensors"]):
        out.append("   [%2d] id=0x%08X inst=%d %s" % (i, s[0], s[1], s[2]))
open("_probe_live.out", "w", encoding="utf-8").write("\n".join(out))
print("\n".join(out[:4]))
