# -*- coding: utf-8 -*-
"""
原厂控制中心二进制 · 静态字符串扫描（纯只读，绝不执行、绝不碰硬件）

目的：用已装在本机的 `C:\\Program Files (x86)\\NL5\\ControlCenter` 交叉验证
      `ControlCenter/PROTOCOL.md` 里那些"据命名推断"的 EC 子命令语义。

手法：
  1. 提取 ASCII / UTF-16LE 可打印字符串
  2. 按 EC 寄存器名 / WMI 标识 / 命令字面量过滤
  3. 命中项连同文件偏移一起落盘到 `_vendor_strings.txt`，便于二次 grep

只做**佐证**，任何结论都必须回到 DSDT 或实测才能写进 PROTOCOL.md。
"""
import os
import re
import sys

VENDOR_DIR = r"C:\Program Files (x86)\NL5\ControlCenter"

TARGETS = [
    "ControlCenter.exe",
    "ControlCenterC64.dll",
    "ControlCenterC32.dll",
    "ControlCenterDaemon.exe",
    "ControlCenter64.sys",
    "ControlCenter.sys",
    "QNVOC.dll",
    "ProfileHelperModel.dll",
    "CDSOSrv.exe",
    "CTS.exe",
]

# EC 寄存器名（PROTOCOL.md 里出现过的，全部来自 DSDT）
EC_REGS = [
    "RTMP", "VGAT", "OMCC", "FS1H", "FS1L", "FS2H", "FS2L", "FS3H", "FS3L",
    "RGBK", "LKCL", "LKBC", "LKBA", "LKBB", "LKBG", "LKBR", "LKBL", "WKCL",
    "ENTP", "AWMI", "GFXT", "HSRE", "UFA1", "UFA2", "UFA3",
    "PERM", "APMD", "RPM5", "RPM1", "RPM2", "RPM3",
]

# WMI / 协议标识
WMI_KEYS = [
    "RW_GMWMI", "GMWMI", "WQAA", "WSAA", "WMIA", "GMC_WMIEvent",
    "644C5791", "74286D6E", "root\\WMI", "ROOT\\WMI",
]

# 通用线索
GENERIC = [
    "ECWT", "ECRD", "Acpi", "ACPI", "CreateField", "FanPercent",
    "CPUFan", "GPUFan", "SYSFan", "UserFan", "Backlight", "RGB",
    "PerformanceMode", "PerfMode", "GamingMode", "OfficeMode",
    "Silent", "Quiet", "Balance", "Performance", "Turbo",
    "GfxMode", "MuxSwitch", "Discrete", "Hybrid", "Optimus",
    "SetPwrPlan", "PowerPlan",
]

HITS = {k: [] for k in EC_REGS}
HITS.update({k: [] for k in WMI_KEYS})
HITS.update({k: [] for k in GENERIC})

# 命令字面量（源码里可能写成 "0x0201" / "FA00" / "0xFA00"）
CMD_LITERAL = re.compile(r"0x0?(?:FA00|0100|0200|0201|0202|0203|0204|0205|0206|0207|0300|0400)", re.I)


def strings(data, minlen=4):
    """产出 (offset, text)，ASCII 与 UTF-16LE 都收"""
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data):
        yield m.start(), m.group().decode("ascii", "ignore")
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % minlen, data):
        yield m.start(), m.group().decode("utf-16-le", "ignore")


def scan(path):
    with open(path, "rb") as f:
        data = f.read()
    name = os.path.basename(path)
    cmds = []
    local = 0
    for off, s in strings(data):
        for k in HITS:
            if k in s:
                if len(HITS[k]) < 40:
                    HITS[k].append((name, off, s[:160]))
                local += 1
        if CMD_LITERAL.search(s) and len(cmds) < 40:
            cmds.append((name, off, s[:160]))
    return len(data), local, cmds


def main():
    if not os.path.isdir(VENDOR_DIR):
        print("未找到原厂目录：%s" % VENDOR_DIR)
        return 1
    out = []
    all_cmds = []
    for t in TARGETS:
        p = os.path.join(VENDOR_DIR, t)
        if not os.path.isfile(p):
            out.append("%-26s (缺失)" % t)
            continue
        size, n, cmds = scan(p)
        all_cmds.extend(cmds)
        out.append("%-26s %10d 字节   命中 %d 条" % (t, size, n))

    print("=== 扫描概览 ===")
    for line in out:
        print("  " + line)

    print("\n=== EC 寄存器名命中 ===")
    for k in EC_REGS:
        v = HITS[k]
        print("  %-6s %s" % (k, ("%d 条" % len(v)) if v else "—"))
        for name, off, s in v[:4]:
            print("        [%s +0x%X] %s" % (name, off, s))

    print("\n=== WMI / 协议标识命中 ===")
    for k in WMI_KEYS:
        v = HITS[k]
        print("  %-14s %s" % (k, ("%d 条" % len(v)) if v else "—"))
        for name, off, s in v[:4]:
            print("        [%s +0x%X] %s" % (name, off, s))

    print("\n=== 通用线索命中（计数） ===")
    for k in GENERIC:
        print("  %-18s %d" % (k, len(HITS[k])))

    print("\n=== 命令字面量 ===")
    if not all_cmds:
        print("  (无)")
    for name, off, s in all_cmds[:40]:
        print("  [%s +0x%X] %s" % (name, off, s))

    # 落盘，便于二次 grep
    dump = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_vendor_strings.txt")
    with open(dump, "w", encoding="utf-8") as f:
        for k, v in HITS.items():
            if not v:
                continue
            f.write("\n### %s  (%d)\n" % (k, len(v)))
            for name, off, s in v:
                f.write("  [%s +0x%X] %s\n" % (name, off, s))
    print("\n明细已写入：%s" % dump)
    return 0


if __name__ == "__main__":
    sys.exit(main())
