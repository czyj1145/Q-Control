# -*- coding: utf-8 -*-
"""
探测 CPU 功耗 / CPU 电压 的数据源（只读，不碰 EC 写）。

试三类：
  A. powrprof!CallNtPowerInformation(EnergyEstimationEngine=45) —— Win10+ 的
     能耗估算引擎（E3），能拿到各组件的累计能耗（mJ），两次采样差分即功耗。
  B. root\\WMI 下的 ProcessorPerformance / MSAcpi_ThermalZoneTemperature 等类。
  C. EC 只读命令 0x0207 / 0x0400 的原始返回（看有没有额外字段）。

输出 JSON，便于和上一次采样做差分。
"""
import ctypes
import json
import os
import sys
import time
from ctypes import wintypes

powrprof = ctypes.WinDLL("powrprof.dll")
powrprof.CallNtPowerInformation.restype = ctypes.c_long
powrprof.CallNtPowerInformation.argtypes = [
    ctypes.c_int, ctypes.c_void_p, wintypes.ULONG,
    ctypes.c_void_p, wintypes.ULONG]


def e3_query(level, size=16384):
    buf = ctypes.create_string_buffer(size)
    st = powrprof.CallNtPowerInformation(
        level, None, 0, ctypes.cast(buf, ctypes.c_void_p), size)
    return st, buf.raw


def probe_e3():
    out = {}
    for lvl in (43, 44, 45, 46, 47, 48):
        try:
            st, raw = e3_query(lvl)
            out[str(lvl)] = {"status": st, "first96": raw[:96].hex()}
        except Exception as e:      # noqa
            out[str(lvl)] = {"err": str(e)}
    return out


def probe_e3_diff():
    """同一 level 连续采两次，看哪些字节在增长（累加器 = 能耗）"""
    out = {}
    for lvl in (45,):
        try:
            _, a = e3_query(lvl)
            time.sleep(2.0)
            _, b = e3_query(lvl)
            diff = {}
            for i in range(0, min(len(a), 2048) - 4, 4):
                va = int.from_bytes(a[i:i + 4], "little")
                vb = int.from_bytes(b[i:i + 4], "little")
                if vb > va and vb - va < 10 ** 9:
                    diff[str(i)] = [va, vb, vb - va]
            out[str(lvl)] = diff
        except Exception as e:      # noqa
            out[str(lvl)] = {"err": str(e)}
    return out


def probe_wmi():
    out = {}
    try:
        import pythoncom
        import win32com.client
        pythoncom.CoInitialize()
        svc = win32com.client.GetObject("winmgmts:root\\WMI")
        for cls in ("ProcessorPerformance", "MSAcpi_ThermalZoneTemperature",
                    "ProcessorStatus", "MSPower"):
            try:
                rows = []
                for o in svc.InstancesOf(cls):
                    d = {}
                    for p in o.Properties_:
                        try:
                            d[p.Name] = p.Value
                        except Exception:      # noqa
                            pass
                    rows.append(d)
                out[cls] = rows[:3]
            except Exception as e:              # noqa
                out[cls] = "ERR: %s" % str(e)[:80]
    except Exception as e:                      # noqa
        out["_err"] = str(e)
    return out


def probe_ec():
    """EC 只读命令的原始返回（0x0207 / 0x0400 在只读白名单里）"""
    out = {}
    try:
        sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "ControlCenter"))
        from hal import create_hal
        ec = create_hal()
        ok, err = ec.available()
        out["available"] = [ok, err]
        if ok:
            for cmd in (0x0200, 0x0202, 0x0203, 0x0207, 0x0400):
                try:
                    r = ec._raw(cmd)
                    out["0x%04X" % cmd] = " ".join("%02X" % x for x in r)
                except Exception as e:          # noqa
                    out["0x%04X" % cmd] = "ERR: %s" % str(e)[:60]
    except Exception as e:                      # noqa
        out["_err"] = str(e)
    return out


if __name__ == "__main__":
    res = {
        "e3_levels": probe_e3(),
        "e3_diff": probe_e3_diff(),
        "wmi": probe_wmi(),
        "ec": probe_ec(),
    }
    out = sys.argv[1] if len(sys.argv) > 1 else "probe_extra.json"
    with open(out, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    print("written:", out)
