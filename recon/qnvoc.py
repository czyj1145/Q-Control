# -*- coding: utf-8 -*-
"""
QNVOC.dll 解析（纯只读）：把它的**导出表函数名**和 NVML 相关字符串挖出来。

为什么它重要：ControlCenter.exe 里出现了 `InitialOCDLL` / `GPUOC_SET` / `GPUOC_RESET`
/ `NVML_CLOCK_*`，说明 GPU 超频是它实现的。我们**不用**它（它是 OEM 私有的，
不能随我们的软件分发），但要看清楚它调用 NVML 的哪些 API、参数怎么走，
然后在自己的代码里直连系统自带的 `nvml.dll` 复刻同样的语义。

用法：python recon/qnvoc.py
"""
import os
import re
import struct
import sys

DLL = r"C:\Program Files (x86)\NL5\ControlCenter\QNVOC.dll"


def rva_to_off(data, pe_off, sections, rva):
    for vsize, vaddr, rsize, raddr in sections:
        if vaddr <= rva < vaddr + max(vsize, rsize):
            return raddr + (rva - vaddr)
    # 没落到任何节：RVA == 文件偏移（很少见，但兜底）
    return rva


def parse_exports(data):
    if data[:2] != b"MZ":
        return None, "不是 PE 文件"
    pe_off = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe_off:pe_off + 4] != b"PE\0\0":
        return None, "PE 头不对"
    coff = pe_off + 4
    machine, nsec = struct.unpack_from("<HH", data, coff)
    opt = coff + 20
    magic = struct.unpack_from("<H", data, opt)[0]
    is64 = magic == 0x20B
    # DataDirectory 起始位置
    dd_off = opt + (112 if is64 else 96)
    ndd = struct.unpack_from("<I", data, dd_off - 4)[0] if False else 16
    dirs = [struct.unpack_from("<II", data, dd_off + 8 * i) for i in range(min(ndd, 16))]
    export_rva, export_size = dirs[0]

    sections = []
    sec_off = opt + struct.unpack_from("<H", data, coff + 16)[0]
    for i in range(nsec):
        name, vsize, vaddr, rsize, raddr = struct.unpack_from(
            "<8sIIII", data, sec_off + 40 * i)
        sections.append((vsize, vaddr, rsize, raddr))

    if not export_rva:
        return None, "没有导出表（大概是 .NET 程序集，方法在元数据里而不是导出表）"

    e = rva_to_off(data, pe_off, sections, export_rva)
    n_fun = struct.unpack_from("<I", data, e + 20)[0]
    n_names = struct.unpack_from("<I", data, e + 24)[0]
    addr_rva = struct.unpack_from("<I", data, e + 28)[0]
    name_rva = struct.unpack_from("<I", data, e + 32)[0]
    ord_rva = struct.unpack_from("<I", data, e + 36)[0]

    names_off = rva_to_off(data, pe_off, sections, name_rva)
    out = []
    for i in range(n_names):
        nm_rva = struct.unpack_from("<I", data, names_off + 4 * i)[0]
        nm_off = rva_to_off(data, pe_off, sections, nm_rva)
        end = data.find(b"\x00", nm_off)
        out.append(data[nm_off:end].decode("ascii", "replace"))
    return (machine, is64, n_fun, n_names, sorted(out)), None


def strings(data, minlen=5):
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data):
        yield m.start(), m.group().decode("ascii", "ignore")
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % minlen, data):
        yield m.start(), m.group().decode("utf-16-le", "ignore")


NVML_KEY = ["ApplicationsClock", "nvml", "NVML", "SetClock", "GraphicsClock",
            "MemoryClock", "SupportedMemoryClocks", "SupportedGraphicsClocks",
            "ResetApplicationsClocks", "nvmlInit", "nvmlDevice",
            "Overclock", "offset", "Offset", "OC_", "GpuOc", "GPUOC",
            "PowerLimit", "PerfPolicy", "nvmlSystemGetNVMLVersion"]


def main():
    if not os.path.isfile(DLL):
        print("找不到 %s" % DLL)
        return 1
    with open(DLL, "rb") as f:
        data = f.read()
    print("=== %s  (%d 字节) ===" % (os.path.basename(DLL), len(data)))

    info, err = parse_exports(data)
    if err:
        print("导出表：%s" % err)
    else:
        machine, is64, n_fun, n_names, names = info
        print("machine=0x%X  64位=%s  导出函数=%d  具名=%d" % (machine, is64, n_fun, n_names))
        print("\n=== 导出函数名（%d 个）===" % len(names))
        for n in names:
            print("  " + n)

    print("\n=== NVML / OC 相关字符串 ===")
    seen = set()
    rows = []
    for off, s in strings(data):
        if any(k in s for k in NVML_KEY):
            key = s[:120]
            if key in seen:
                continue
            seen.add(key)
            rows.append((off, s[:200]))
    for off, s in rows[:120]:
        print("  +0x%X  %s" % (off, s))
    print("  (共 %d 条)" % len(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
