# -*- coding: utf-8 -*-
"""
NVML GPU 超频 API 实测（先只读；写操作只做 0 增量这种 no-op）

已由 nvml.dll 导出表确证本机驱动提供了这些接口：
    nvmlDeviceGetGpcClkMinMaxVfOffset / GetMemClkMinMaxVfOffset   ← QNVGetFreqDeltaRange
    nvmlDeviceGetGpcClkVfOffset      / GetMemClkVfOffset          ← QNVGetCurFreqDelta
    nvmlDeviceSetGpcClkVfOffset      / SetMemClkVfOffset          ← QNVSetGPU/MEMClockDelta
    nvmlDeviceGetClockOffsets        / SetClockOffsets
    GetApplicationsClock / GetDefaultApplicationsClock / SetApplicationsClocks / ResetApplicationsClocks
    GetSupportedGraphicsClocks / GetSupportedMemoryClocks / GetMaxClockInfo / GetMinMaxClockOfPState

本脚本逐个试调用，打印返回码，用来判断哪条路在本机( RTX 5070 Ti Laptop + 当前驱动 )真可用。
写操作只传 0（等于恢复到出厂默认 offset），不会真的超频。

用法：python recon/nvml_oc_test.py
"""
import ctypes
import os
import struct
import sys

SYSTEM32 = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")
NVML = os.path.join(SYSTEM32, "nvml.dll")

ERR = {
    0: "SUCCESS", 1: "UNINITIALIZED", 2: "INVALID_ARGUMENT", 3: "NOT_SUPPORTED",
    4: "NO_PERMISSION", 5: "ALREADY_INITIALIZED", 6: "NOT_FOUND",
    7: "INSUFFICIENT_SIZE", 8: "INSUFFICIENT_POWER", 9: "DRIVER_NOT_LOADED",
    10: "TIMEOUT", 11: "IRQ_ISSUE", 12: "LIBRARY_NOT_FOUND",
    13: "FUNCTION_NOT_FOUND", 14: "CORRUPTED_INFOROM", 15: "GPU_IS_LOST",
    16: "RESET_REQUIRED", 17: "OPERATING_SYSTEM", 18: "LIB_RM_VERSION_MISMATCH",
    19: "IN_USE", 20: "MEMORY", 21: "NO_DATA", 22: "UNKNOWN",
}


def main():
    lib = ctypes.CDLL(NVML)
    print("已加载", NVML)

    # ---- init ----
    if hasattr(lib, "nvmlInit_v2"):
        r = lib.nvmlInit_v2()
    else:
        r = lib.nvmlInit()
    print("nvmlInit ->", ERR.get(r, r))
    if r:
        return 1

    ver = ctypes.create_string_buffer(80)
    if hasattr(lib, "nvmlSystemGetNVMLVersion"):
        r = lib.nvmlSystemGetNVMLVersion(ver, 80)
        print("NVML 版本 ->", ERR.get(r, r), ver.value)

    dev = ctypes.c_void_p()
    for fn in ("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex"):
        if hasattr(lib, fn):
            r = getattr(lib, fn)(0, ctypes.byref(dev))
            print("%s(0) -> %s" % (fn, ERR.get(r, r)))
            break
    if not dev.value:
        return 1

    name = ctypes.create_string_buffer(96)
    if hasattr(lib, "nvmlDeviceGetName"):
        r = lib.nvmlDeviceGetName(dev, name, 96)
        print("GPU ->", name.value.decode(errors="replace") if not r else ERR.get(r, r))

    # ---------- 只读：VF offset（最接近 OEM 的 delta 语义）----------
    print("\n=== VF offset 系列（OEM 的 GPU/MEC delta 应该就是它）===")
    for label, fminmax, fget in (
            ("GPC(core)", "nvmlDeviceGetGpcClkMinMaxVfOffset", "nvmlDeviceGetGpcClkVfOffset"),
            ("MEM(memory)", "nvmlDeviceGetMemClkMinMaxVfOffset", "nvmlDeviceGetMemClkVfOffset")):
        lo, hi = ctypes.c_int(0), ctypes.c_int(0)
        if hasattr(lib, fminmax):
            r = getattr(lib, fminmax)(dev, ctypes.byref(lo), ctypes.byref(hi))
            print("  %-11s MinMaxVF -> %-18s  min=%d max=%d"
                  % (label, ERR.get(r, r), lo.value, hi.value))
        else:
            print("  %-11s MinMaxVF 函数不存在" % label)
        if hasattr(lib, fget):
            cur = ctypes.c_int(0)
            r = getattr(lib, fget)(dev, ctypes.byref(cur))
            print("  %-11s CurVF    -> %-18s  cur=%d"
                  % (label, ERR.get(r, r), cur.value))

    # ---------- ClockOffsets 结构体版 ----------
    print("\n=== nvmlDeviceGetClockOffsets（结构体版）===")
    if hasattr(lib, "nvmlDeviceGetClockOffsets"):
        buf = (ctypes.c_int * 4)(0, 0, 0, 0)
        r = lib.nvmlDeviceGetClockOffsets(dev, ctypes.byref(buf))
        print("  -> %s   word0(version?)=%d word1=%d word2=%d word3=%d"
              % (ERR.get(r, r), buf[0], buf[1], buf[2], buf[3]))

    # ---------- Applications Clocks（另一条常见超频路）----------
    print("\n=== Applications Clocks ===")
    GRAPHICS, SM, MEM = 1, 2, 3
    for typ, nm in ((GRAPHICS, "GRAPHICS"), (MEM, "MEM"), (SM, "SM")):
        v = ctypes.c_uint(0)
        r = lib.nvmlDeviceGetApplicationsClock(dev, typ, ctypes.byref(v))
        d = ctypes.c_uint(0)
        r2 = lib.nvmlDeviceGetDefaultApplicationsClock(dev, typ, ctypes.byref(d))
        print("  GetApplicationsClock(%-8s) -> %-16s cur=%d MHz | default -> %-16s %d MHz"
              % (nm, ERR.get(r, r), v.value, ERR.get(r2, r2), d.value))

    # 支持的频率档位
    cnt = ctypes.c_uint(0)
    r = lib.nvmlDeviceGetSupportedMemoryClocks(dev, ctypes.byref(cnt))
    print("  GetSupportedMemoryClocks -> %s count=%d" % (ERR.get(r, r), cnt.value))
    if not r and cnt.value:
        arr = (ctypes.c_uint * min(cnt.value, 32))()
        arr[0] = 0
        try:
            # 先用 NULL 取数量，再按 NVML 惯例第二次调用填数组（部分版本要传 ≥count）
            big = (ctypes.c_uint * (cnt.value + 8))()
            r = lib.nvmlDeviceGetSupportedMemoryClocks(dev, ctypes.byref(cnt), big)
            vals = sorted({int(big[i]) for i in range(min(cnt.value, 32)) if big[i]})
            print("     档位:", vals[:16], "..." if len(vals) > 16 else "")
        except Exception as e:  # noqa
            print("     取数组失败:", e)

    print("\n=== 其它时钟读接口 ===")
    v = ctypes.c_uint(0)
    if hasattr(lib, "nvmlDeviceGetMaxClockInfo"):
        for typ, nm in ((GRAPHICS, "GRAPHICS"), (MEM, "MEM"), (SM, "SM")):
            r = lib.nvmlDeviceGetMaxClockInfo(dev, typ, ctypes.byref(v))
            print("  GetMaxClockInfo(%-8s) -> %-16s %d MHz" % (nm, ERR.get(r, r), v.value))
    if hasattr(lib, "nvmlDeviceGetMinMaxClockOfPState"):
        for typ, nm in ((GRAPHICS, "GRAPHICS"), (MEM, "MEM")):
            lo = ctypes.c_uint(0)
            hi = ctypes.c_uint(0)
            r = lib.nvmlDeviceGetMinMaxClockOfPState(dev, typ, ctypes.byref(lo), ctypes.byref(hi))
            print("  MinMaxClockOfPState(%-8s) -> %-16s %d~%d MHz"
                  % (nm, ERR.get(r, r), lo.value, hi.value))

    print("\n=== 写操作探路（只传 0 = 回到默认 offset，不做真超频）===")
    for fn, args, desc in (
            ("nvmlDeviceSetGpcClkVfOffset", (ctypes.c_int(0),), "SetGpcClkVfOffset(0)"),
            ("nvmlDeviceSetMemClkVfOffset", (ctypes.c_int(0),), "SetMemClkVfOffset(0)"),
            ("nvmlDeviceSetClockOffsets", None, "SetClockOffsets(0,0)"),
            ("nvmlDeviceSetApplicationsClocks", (ctypes.c_uint(0), ctypes.c_uint(0)),
             "SetApplicationsClocks(0,0)"),
            ("nvmlDeviceResetApplicationsClocks", (), "ResetApplicationsClocks()"),
    ):
        if not hasattr(lib, fn):
            print("  %-34s 函数不存在" % desc)
            continue
        try:
            if fn == "nvmlDeviceSetClockOffsets":
                buf = (ctypes.c_int * 3)(struct.unpack("<I", struct.pack("<I", 0))[0], 0, 0)
                r = getattr(lib, fn)(dev, ctypes.byref(buf))
            else:
                r = getattr(lib, fn)(dev, *args)
        except Exception as e:  # noqa
            print("  %-34s 异常: %s" % (desc, e))
            continue
        print("  %-34s -> %s" % (desc, ERR.get(r, r)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
