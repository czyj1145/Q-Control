# -*- coding: utf-8 -*-
"""
nvml.dll 可行性探针（只读安全 —— 只调用 get 类 API，不做任何 set）

目的：给「GPU 超频」找实现路径。原厂 QNVOC.dll 导出了
`QNVSetGPUClockDelta` / `QNVSetMEMClockDelta` / `QNVGetFreqDeltaRange`，
说明这块 NVIDIA 驱动确实开放了「频率 delta（offset）」接口。
我们不碰 OEM 的 dll（不能分发），要直连系统自带的 nvml.dll 复刻同样语义。

本脚本先把 nvml.dll 的**导出符号全表**拉出来看有没有 offset / applications clock 系列，
再对 get 类 API 试读一次，确认哪些在本机（RTX 5070 Ti Laptop）真的可用。

用法：python recon/nvml_probe.py
"""
import os
import re
import struct
import sys

SYSTEM32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
NVML = os.path.join(SYSTEM32, "nvml.dll")


def dump_export_names(path):
    """解析 PE 导出表，返回 [(name, ordinal)]"""
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"MZ":
        return None
    pe_off = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_off:pe_off + 4] != b"PE\0\0":
        return None
    coff = pe_off + 4
    nsec = struct.unpack_from("<H", data, coff + 2)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    nbytes_opt = struct.unpack_from("<H", data, coff + 16)[0]
    dd_off = opt + (112 if magic == 0x20B else 96)
    export_rva = struct.unpack_from("<I", data, dd_off)[0]

    sec_off = opt + nbytes_opt
    sections = []
    for i in range(nsec):
        name, vsize, vaddr, rsize, raddr = struct.unpack_from(
            "<8sIIII", data, sec_off + 40 * i)
        sections.append((vsize, vaddr, rsize, raddr))

    def r2o(rva):
        for vsize, vaddr, rsize, raddr in sections:
            if vaddr <= rva < vaddr + max(vsize, rsize):
                return raddr + (rva - vaddr)
        return rva

    e = r2o(export_rva)
    n_names = struct.unpack_from("<I", data, e + 24)[0]
    name_rva = struct.unpack_from("<I", data, e + 32)[0]
    ord_rva = struct.unpack_from("<I", data, e + 36)[0]
    no = r2o(name_rva)
    oo = r2o(ord_rva)
    names = []
    for i in range(n_names):
        nr = struct.unpack_from("<I", data, no + 4 * i)[0]
        o = struct.unpack_from("<H", data, oo + 2 * i)[0]
        off = r2o(nr)
        end = data.find(b"\x00", off)
        names.append((data[off:end].decode("ascii", "replace"), o))
    return sorted(names)


def main():
    if not os.path.isfile(NVML):
        print("找不到 nvml.dll：%s" % NVML)
        return 1
    names = dump_export_names(NVML)
    if not names:
        print("解析导出表失败")
        return 1
    print("=== %s  导出符号 %d 个 ===" % (NVML, len(names)))

    pats = [r"clock", r"offset", r"pstate", r"boost", r"power", r"limit",
            r"fan", r"temp", r"util", r"applications"]
    for p in pats:
        hit = [n for n, o in names if re.search(p, n, re.I)]
        print("\n--- 含 %r 的导出（%d）---" % (p, len(hit)))
        for n in hit:
            print("  " + n)
    return 0


if __name__ == "__main__":
    sys.exit(main())
