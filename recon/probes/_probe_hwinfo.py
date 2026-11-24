# -*- coding: utf-8 -*-
"""拉起项目自带的 HWiNFO64 并解析其共享内存（找 PCH 温度）。

集成思路：HWiNFO 便携版随项目分发（tools/HWiNFO/），控制中心启动时自动拉起，
用户不需要自己安装/配置任何东西。读取走官方共享内存接口
`Global\\HWiNFO_SENSORS_SHARED_MEM2`（HWiNFO SDK 公开格式）。

配置由同目录 `HWiNFO64.INI` 预置（便携版不写注册表）：
  SensorsOnly=1  只跑传感器模式
  SensorsSM=1    导出共享内存  ← 关键
  SMMemSize=262144
  StartMinimized=1 / MinimizeSensors=1
  UpdateCheckEnabled=0  别联网检查更新

本探针：启动进程 → 等共享内存出现 → 解析全部读数 → 标出 PCH 相关项。
只读。沙箱非管理员下 HWiNFO 可能读不到部分传感器（驱动受限），但能验证接口是否通。
"""
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

HERE = os.path.dirname(os.path.abspath(__file__))
HWI_DIR = os.path.join(HERE, "..", "..", "tools", "HWiNFO")
EXE = os.path.join(HWI_DIR, "HWiNFO64.exe")
SHM_NAME = "Global\\HWiNFO_SENSORS_SHARED_MEM2"
STR = 128
UNIT = 16

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenFileMappingW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
k32.OpenFileMappingW.restype = wintypes.HANDLE
k32.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD,
                              wintypes.DWORD, ctypes.c_size_t]
k32.MapViewOfFile.restype = ctypes.c_void_p
k32.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
k32.CloseHandle.argtypes = [wintypes.HANDLE]
FILE_MAP_READ = 0x0004


class HEADER(ctypes.Structure):
    _fields_ = [("dwSignature", wintypes.DWORD),
                ("dwVersion", wintypes.DWORD),
                ("dwRevision", wintypes.DWORD),
                ("poll_time", ctypes.c_int64),
                ("dwOffsetOfSensorSection", wintypes.DWORD),
                ("dwSizeOfSensorElement", wintypes.DWORD),
                ("dwNumSensorElements", wintypes.DWORD),
                ("dwOffsetOfReadingSection", wintypes.DWORD),
                ("dwSizeOfReadingElement", wintypes.DWORD),
                ("dwNumReadingElements", wintypes.DWORD)]


class SENSOR(ctypes.Structure):
    _fields_ = [("dwSensorID", wintypes.DWORD),
                ("dwSensorInst", wintypes.DWORD),
                ("szSensorNameOrig", ctypes.c_char * STR),
                ("szSensorNameUser", ctypes.c_char * STR)]


class READING(ctypes.Structure):
    _fields_ = [("dwReadingID", wintypes.DWORD),
                ("dwSensorIndex", wintypes.DWORD),
                ("dwReadingType", wintypes.DWORD),
                ("szLabelOrig", ctypes.c_char * STR),
                ("szLabelUser", ctypes.c_char * STR),
                ("szUnit", ctypes.c_char * UNIT),
                ("Value", ctypes.c_double),
                ("ValueMin", ctypes.c_double),
                ("ValueMax", ctypes.c_double),
                ("ValueAvg", ctypes.c_double)]


def start_hwinfo():
    """没在跑就拉起（用预置 INI 静默启动）。"""
    try:
        r = subprocess.run(["tasklist", "/NH"], capture_output=True,
                           text=True, encoding="gbk", errors="replace")
        if "hwinfo64.exe" in (r.stdout or "").lower():
            return "已在运行"
    except Exception:                                        # noqa
        pass
    if not os.path.exists(EXE):
        return "缺少 %s" % EXE
    subprocess.Popen([EXE], cwd=HWI_DIR,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return "已启动"


def read_shm():
    h = k32.OpenFileMappingW(FILE_MAP_READ, False, SHM_NAME)
    if not h:
        return None
    try:
        p = k32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, 0)
        if not p:
            return None
        try:
            hdr = HEADER.from_address(p)
            if hdr.dwSignature != 0x53695748:      # "HWiS"
                return ("bad-signature", hdr.dwSignature,
                        hdr.dwVersion, hdr.dwRevision)
            out = {"sensors": [], "readings": []}
            for i in range(hdr.dwNumSensorElements):
                addr = p + hdr.dwOffsetOfSensorSection + i * hdr.dwSizeOfSensorElement
                s = SENSOR.from_address(addr)
                out["sensors"].append(
                    (s.dwSensorID, s.dwSensorInst,
                     s.szSensorNameOrig.decode("latin1", "replace").rstrip("\x00")))
            for i in range(hdr.dwNumReadingElements):
                addr = p + hdr.dwOffsetOfReadingSection + i * hdr.dwSizeOfReadingElement
                r = READING.from_address(addr)
                out["readings"].append({
                    "id": r.dwReadingID, "sidx": r.dwSensorIndex,
                    "type": r.dwReadingType,
                    "label": r.szLabelOrig.decode("latin1", "replace").rstrip("\x00"),
                    "unit": r.szUnit.decode("latin1", "replace").rstrip("\x00"),
                    "value": r.Value})
            out["meta"] = (hdr.dwVersion, hdr.dwRevision,
                           hdr.dwSizeOfSensorElement, hdr.dwSizeOfReadingElement,
                           int(hdr.dwNumSensorElements), int(hdr.dwNumReadingElements))
            return out
        finally:
            k32.UnmapViewOfFile(p)
    finally:
        k32.CloseHandle(h)


def main():
    print("=" * 60)
    print("IsUserAnAdmin =", ctypes.windll.shell32.IsUserAnAdmin())
    print("启动状态:", start_hwinfo())
    print("等待共享内存", SHM_NAME, "（最多 60 秒）...")
    data = None
    t0 = time.time()
    while time.time() - t0 < 60:
        info = read_shm()
        if isinstance(info, dict):
            data = info
            break
        if isinstance(info, tuple):
            print("  段存在但签名不对:", info)
            break
        time.sleep(2)
    if data is None:
        print("!! 拿不到共享内存。可能原因：")
        print("   1) HWiNFO 弹了首启对话框（INI 没生效）")
        print("   2) HWiNFO 没开 Shared Memory（SensorsSM）")
        print("   3) 需要管理员权限")
        return
    print("共享内存 OK: 版本/修订=%s  sensor elem=%s  reading elem=%s  传感器=%s  读数=%s"
          % data["meta"])
    print()
    print("--- 传感器（硬件分组）---")
    for sid, sinst, name in data["sensors"]:
        print("  [%d/%d] %s" % (sid, sinst, name))
    print()
    print("--- 全部读数 ---")
    pch = []
    for r in data["readings"]:
        u = r["unit"]
        mark = ""
        up = r["label"].upper()
        if any(k in up for k in ("PCH", "CHIPSET", "SOUTHBRIDGE", "DTS")):
            mark = "   <<<<<< PCH 相关"
            pch.append(r)
        print("  #%-4s s%-3s type=%-2s %-38s %-6s = %s%s"
              % (r["id"], r["sidx"], r["type"], r["label"], u, r["value"], mark))
    print()
    print("=== PCH 相关汇总 ===")
    for r in pch:
        print("  %s = %s %s" % (r["label"], r["value"], r["unit"]))
    if not pch:
        print("  （未找到 PCH 相关读数）")


main()
