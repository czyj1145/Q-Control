# -*- coding: utf-8 -*-
"""拉起 HWiNFO 并 dump 共享内存里的**全部**传感器分组与读数。

用途：确定 HWiNFO 各数据的真实 label / 单位 / type，供 HWiNFO 读取层的
find_cpu_power / find_cpu_voltage / find_gpu_mem_temp / find_batt_rate 做映射。
⚠️ 必须**管理员**运行（HWiNFO64.exe 清单是 requireAdministrator + uiAccess，
   非管理员起不来；已在管理员态则 ShellExecute 不弹 UAC）。
只读，不写任何系统状态；输出落 `_probe_hwi_all.out`。
"""
import ctypes
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "ControlCenter"))
import hwinfo                                             # noqa: E402

OUT = os.path.join(HERE, "_probe_hwi_all.out")


def main():
    out = []
    out.append("IsUserAnAdmin = %s" % bool(ctypes.windll.shell32.IsUserAnAdmin()))
    ok, detail = hwinfo.ensure_running(wait=90)
    out.append("ensure_running -> ok=%s detail=%s" % (ok, detail))
    out.append("is_running     -> %s" % hwinfo.is_running())

    d = hwinfo._read_raw()
    if not d:
        out.append("")
        out.append("!! 读不到共享内存（段不存在 / 签名不对 / 未就绪）。")
        out.append("   段名候选：%s" % (hwinfo.SHM_NAMES,))
        _write(out)
        return

    out.append("layout=%s s_elem=%s r_elem=%s ver=%s rev=%s poll=%s"
               % (d["layout"], d["s_elem"], d["r_elem"], d["version"],
                  d["revision"], d["poll"]))
    out.append("传感器组 = %d，读数 = %d" % (len(d["sensors"]), len(d["readings"])))

    # ---- 按分组列出全部读数（这是做 label 映射的主要依据）----
    by_group = {}
    for r in d["readings"]:
        by_group.setdefault(r["sidx"], []).append(r)
    out.append("")
    out.append("=" * 78)
    out.append("按传感器组列出全部读数")
    out.append("=" * 78)
    for idx, (sid, inst, name) in enumerate(d["sensors"]):
        rows = by_group.get(idx)
        out.append("")
        out.append("[%2d] id=0x%08X inst=%d  %s   (%d 条)"
                   % (idx, sid, inst, name, len(rows or [])))
        for r in (rows or []):
            out.append("      type=%-2s %-44s %-4s = %s"
                       % (r["type"], r["label"], r["unit"], r["value"]))

    # ---- 关键候选：按单位筛，看看各数据落在哪 ----
    out.append("")
    out.append("=" * 78)
    out.append("候选速查（单位 W / V / % / MB，用于映射 cpu_power / cpu_voltage /"
               " gpu_mem_temp / batt_rate）")
    out.append("=" * 78)
    want_units = ("W", "mW", "V", "mV", "°C", "%", "MB")
    for r in d["readings"]:
        u = (r["unit"] or "").strip()
        if u not in want_units:
            continue
        gname = (d["sensors"][r["sidx"]][2]
                 if 0 <= r["sidx"] < len(d["sensors"]) else "?")
        if not any(k in gname.upper() for k in
                   ("CPU", "GPU", "BATTERY", "MEMORY", "SYSTEM", "PCH")):
            continue
        out.append("  %-46s %-30s %-3s = %s"
                   % (gname, r["label"], u, r["value"]))

    _write(out)


def _write(lines):
    txt = "\n".join(lines)
    with open(OUT, "w", encoding="utf-8") as f:
        f.write(txt)
    print(txt)
    print()
    print("已写入 %s" % OUT)


if __name__ == "__main__":
    main()
