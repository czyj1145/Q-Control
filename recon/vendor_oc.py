# -*- coding: utf-8 -*-
"""
原厂控制中心 · GPU 超频相关静态扫描（纯只读：不执行 OEM 任何代码、不碰硬件）

用户确认「原厂控制中心就有 GPU 超频」，所以要在 OEM 安装目录里找出它到底走的哪条路：
厂区自带的 dll / exe 名称、导入表里的 NVAPI / NVML / nvidia-smi 线索、
以及中英文关键词（核心频率/显存频率/超频/OC/Offset/Pstate/TGP/PowerTarget…）。

用法：python recon/vendor_oc.py
明细落盘 recon/_vendor_oc.txt
"""
import os
import re
import sys
import traceback

VENDOR_DIR = r"C:\Program Files (x86)\NL5\ControlCenter"

KEYWORDS = [
    # 中英文 UI / 功能词
    "超频", "OC", "overclock", "OverClock", "offset", "Offset",
    "CoreClock", "MemClock", "MemoryClock", "clock", "Clock",
    "Pstate", "PState", "Performance", "TGP", "PowerTarget", "TempTarget",
    "DynamicBoost", "Boost", "FanCurve", "FanSpeed",
    # 依赖线索
    "nvapi", "NvAPI", "nvapi64", "nvml", "NVML", "nvidia-smi",
    "NVIDIA", "nvapiud的名字_DIRTable".replace("的名字_DIRTable", ""),
]
# 单独处理的、非常短的词（避免误伤）
SHORT = ["OC", "OC模式"]
IMPORT_HINTS = ["nvapi", "NVAPI", "nvml", "NVML", "nvapi64", "nvmlQuery",
                "SetPstate", "Overclock", "OC_", "GpuOc", "GPUOC", "ClkOffset"]


def strings(data, minlen=4):
    for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data):
        yield m.start(), m.group().decode("ascii", "ignore")
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){%d,}" % minlen, data):
        yield m.start(), m.group().decode("utf-16-le", "ignore")
    # 中文 GBK/UTF8 混合（UTF-8 多字节字串也一并收）
    for m in re.finditer(rb"[\x20-\x7e\xc2-\xef][\x80-\xbf\x20-\x7e]{3,}", data):
        try:
            t = m.group().decode("utf-8")
        except UnicodeDecodeError:
            continue
        if any("一" <= ch <= "鿿" for ch in t):
            yield m.start(), t


# 中文 GBK 串单独提取（OEM 中文 UI 大概率是 GBK/UTF-16LE）
def strings_gbk(data, minlen=3):
    pat = rb"(?:[\xa1-\xfe][\x40-\xfe]){%d,}" % minlen
    for m in re.finditer(pat, data):
        try:
            t = m.group().decode("gbk")
        except UnicodeDecodeError:
            continue
        yield m.start(), t


def list_targets():
    out = []
    for name in sorted(os.listdir(VENDOR_DIR)):
        p = os.path.join(VENDOR_DIR, name)
        if os.path.isfile(p) and name.lower().endswith((".exe", ".dll", ".sys", ".config")):
            out.append(p)
    return out


def pe_imports(data):
    """粗提取 PE 的导入 DLL 名（够用即可：只要 ASCII 里的 .dll/.exe 引用）"""
    names = set()
    for m in re.finditer(rb"[\x20-\x7e]{4,}\.(?:dll|DLL|exe|EXE)", data):
        names.add(m.group().decode("ascii", "ignore"))
    return sorted(names)


def main():
    if not os.path.isdir(VENDOR_DIR):
        print("未找到原厂目录：%s" % VENDOR_DIR)
        return 1
    targets = list_targets()
    lines = []
    print("=== 原厂目录（%d 个文件）===" % len(targets))
    imported_all = {}
    for p in targets:
        name = os.path.basename(p)
        try:
            with open(p, "rb") as f:
                data = f.read()
        except Exception as e:
            print("  [跳过] %s: %s" % (name, e))
            continue
        size = len(data)
        imp = pe_imports(data)
        imported_all[name] = imp
        hits = []
        seen = set()
        for off, s in strings(data):
            low = s.lower()
            for k in KEYWORDS:
                if k.lower() in low and k not in SHORT:
                    if off in seen:
                        continue
                    seen.add(off)
                    hits.append((off, "A", s[:200]))
                    break
            else:
                for k in SHORT:
                    if k in s:
                        if off in seen:
                            break
                        seen.add(off)
                        hits.append((off, "A", s[:200]))
                        break
        for off, s in strings_gbk(data):
            if off in seen:
                continue
            if any(k in s for k in ("超频", "频率", "显卡", "功耗", "风扇", "性能", "显存", "核心")):
                seen.add(off)
                hits.append((off, "GBK", s[:200]))
        if size:
            print("\n--- %s  (%d 字节, 命中 %d 条) ---" % (name, size, len(hits)))
            lines.append("\n=== %s (%d 字节) ===" % (name, size))
        for off, kind, s in hits[:25]:
            print("  +0x%X [%s] %s" % (off, kind, s))
            lines.append("  +0x%X [%s] %s" % (off, kind, s))
        for off, kind, s in hits[25:]:
            lines.append("  +0x%X [%s] %s" % (off, kind, s))

    print("\n=== 可能的 OC / NVIDIAs 依赖（每个二进制里提到的 dll）===")
    lines.append("\n=== dll 依赖线索 ===")
    for name, imp in imported_all.items():
        keep = [x for x in imp
                if any(h.lower() in x.lower() for h in
                       ("nvapi", "nvml", "nvidia", "nvvd", "physx", "cuda"))]
        if keep:
            print("  %-28s %s" % (name, ", ".join(keep[:12])))
            lines.append("  %-28s %s" % (name, ", ".join(keep[:12])))

    dump = os.path.join(os.path.dirname(os.path.abspath(__file__)), "_vendor_oc.txt")
    with open(dump, "w", encoding="utf-8") as f:
        f.write("\n".join(lines))
    print("\n明细已写入：%s" % dump)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        traceback.print_exc()
        sys.exit(2)
