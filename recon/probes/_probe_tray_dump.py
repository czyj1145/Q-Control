# -*- coding: utf-8 -*-
"""把托盘里**真实显示**的每个图标都列出来（真值，不靠猜）。

## 为什么还要这个探针

`Shell_NotifyIconGetRect` 那条路（见 `_probe_hwinfo_tray.py`）按 (hWnd,uID) 判存在，
但它有个盲区：**用 GUID 注册的图标**（`NIF_GUID`）不带 hWnd/uID 语义，扫不到。
这里是另一条完全独立的路 —— 直接读 **explorer 的托盘工具栏**：

    NotifyIconOverflowWindow → ToolbarWindow32   （「显示隐藏的图标」弹出层）
    Shell_TrayWnd → TrayNotifyWnd → SysPager → ToolbarWindow32（常驻区）

每个按钮都能拿到 (hWnd, uID, 提示文字)，文字就是你在托盘里看到的那个名字。
这是外壳**实际在显示什么**的真值，与图标用什么方式注册无关。

需要 OpenProcess(explorer) 读内存（同用户，一般不要求管理员）。

## 用法

    python _probe_tray_dump.py            # 列出全部托盘图标 + 归属进程
"""
import ctypes
import subprocess
import sys
from ctypes import wintypes

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
u32 = ctypes.WinDLL("user32", use_last_error=True)

u32.FindWindowExW.argtypes = [wintypes.HWND, wintypes.HWND,
                              wintypes.LPCWSTR, wintypes.LPCWSTR]
u32.FindWindowExW.restype = wintypes.HWND
u32.SendMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM,
                             wintypes.LPARAM]
u32.SendMessageW.restype = ctypes.c_long
u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND,
                                         ctypes.POINTER(wintypes.DWORD)]
u32.GetWindowThreadProcessId.restype = wintypes.DWORD

k32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
k32.OpenProcess.restype = wintypes.HANDLE
k32.VirtualAllocEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                               ctypes.c_size_t, wintypes.DWORD, wintypes.DWORD]
k32.VirtualAllocEx.restype = ctypes.c_void_p
k32.VirtualFreeEx.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                              ctypes.c_size_t, wintypes.DWORD]
k32.VirtualFreeEx.restype = wintypes.BOOL
k32.WriteProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                   ctypes.c_void_p, ctypes.c_size_t,
                                   ctypes.POINTER(ctypes.c_size_t)]
k32.WriteProcessMemory.restype = wintypes.BOOL
k32.ReadProcessMemory.argtypes = [wintypes.HANDLE, ctypes.c_void_p,
                                  ctypes.c_void_p, ctypes.c_size_t,
                                  ctypes.POINTER(ctypes.c_size_t)]
k32.ReadProcessMemory.restype = wintypes.BOOL
k32.CloseHandle.argtypes = [wintypes.HANDLE]
k32.CloseHandle.restype = wintypes.BOOL

PROCESS_VM_OPERATION = 0x0008
PROCESS_VM_READ = 0x0010
PROCESS_VM_WRITE = 0x0020
MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
MEM_RELEASE = 0x8000
PAGE_READWRITE = 0x04

TB_BUTTONCOUNT = 0x0418
TB_GETBUTTON = 0x0417


class TBBUTTON(ctypes.Structure):
    """x64：iBitmap@0 idCommand@4 fsState@8 fsStyle@9 bReserved[6]@10 dwData@16 iString@24。"""
    _fields_ = [("iBitmap", ctypes.c_int), ("idCommand", ctypes.c_int),
                ("fsState", ctypes.c_byte), ("fsStyle", ctypes.c_byte),
                ("bReserved", ctypes.c_byte * 6),
                ("dwData", ctypes.c_size_t), ("iString", ctypes.c_size_t)]


def proc_names():
    r = subprocess.run(["tasklist", "/NH", "/FO", "CSV"], capture_output=True,
                       text=True, encoding="gbk", errors="replace")
    out = {}
    for line in (r.stdout or "").splitlines():
        c = [x.strip('"') for x in line.split('","')]
        if len(c) > 1:
            try:
                out[int(c[1])] = c[0]
            except ValueError:
                pass
    return out


def hwnd_owner(hwnd):
    pid = wintypes.DWORD()
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return int(pid.value)


def toolbars():
    """两个托盘工具栏的 (名字, hwnd)。"""
    out = []
    ov = u32.FindWindowExW(None, None, "NotifyIconOverflowWindow", None)
    tb = u32.FindWindowExW(ov, None, "ToolbarWindow32", None) if ov else None
    if tb:
        out.append(("隐藏图标弹出层", tb))
    tray = u32.FindWindowExW(None, None, "Shell_TrayWnd", None)
    if tray:
        tn = u32.FindWindowExW(tray, None, "TrayNotifyWnd", None)
        pager = u32.FindWindowExW(tn, None, "SysPager", None) if tn else None
        tb2 = u32.FindWindowExW(pager, None, "ToolbarWindow32", None) if pager else None
        if tb2:
            out.append(("任务栏常驻区", tb2))
    return out


def dump(name, tb, explorer_pid, hproc, names, out):
    n = u32.SendMessageW(tb, TB_BUTTONCOUNT, 0, 0)
    if not n:
        return
    remote = k32.VirtualAllocEx(hproc, None, 1024, MEM_COMMIT | MEM_RESERVE,
                                PAGE_READWRITE)
    if not remote:
        out.append("  !! VirtualAllocEx 失败（explorer pid=%d）err=%d"
                   % (explorer_pid, ctypes.get_last_error()))
        return
    try:
        for i in range(n):
            btn = TBBUTTON()
            buf = ctypes.create_string_buffer(ctypes.sizeof(btn))
            k32.WriteProcessMemory(hproc, remote, buf, ctypes.sizeof(btn), None)
            if not u32.SendMessageW(tb, TB_GETBUTTON, i, remote):
                out.append("  [%d] TB_GETBUTTON 失败" % i)
                continue
            got = ctypes.c_size_t(0)
            raw = (ctypes.c_char * ctypes.sizeof(btn))()
            if not k32.ReadProcessMemory(hproc, remote, raw, ctypes.sizeof(btn),
                                         ctypes.byref(got)):
                out.append("  [%d] ReadProcessMemory(TBBUTTON) 失败" % i)
                continue
            btn = TBBUTTON.from_buffer_copy(bytes(raw))
            hwnd = uid = 0
            tip = ""
            if btn.dwData:
                d = (ctypes.c_size_t * 4)()
                if k32.ReadProcessMemory(hproc, ctypes.c_void_p(btn.dwData), d,
                                         ctypes.sizeof(d), ctypes.byref(got)):
                    hwnd, uid = int(d[0]), int(d[1])
            if btn.iString and btn.iString > 0x10000:
                s = (ctypes.c_wchar * 256)()
                if k32.ReadProcessMemory(hproc, ctypes.c_void_p(btn.iString), s,
                                         512, ctypes.byref(got)):
                    tip = str(s.value or "")
            proc = names.get(hwnd_owner(hwnd), "?") if hwnd else "?"
            out.append("  [%2d] %-8s %-26s hwnd=0x%08X uID=%-4d owner=%s  tip=%r"
                       % (i, name, "", hwnd, uid, proc, tip[:60]))
    finally:
        k32.VirtualFreeEx(hproc, remote, 0, MEM_RELEASE)


def main():
    names = proc_names()
    expids = [p for p, n in names.items() if n.lower() == "explorer.exe"]
    out = ["explorer pid = %s" % expids]
    if not expids:
        print("\n".join(out))
        return 1
    hproc = k32.OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ |
                            PROCESS_VM_WRITE, False, expids[0])
    if not hproc:
        out.append("!! OpenProcess(explorer) 失败 err=%d —— 沙箱拦了的话"
                   "请双击一个自提权的 .cmd 再跑" % ctypes.get_last_error())
        print("\n".join(out))
        return 1
    try:
        tbs = toolbars()
        out.append("找到托盘工具栏：%s" % ([n for n, _ in tbs] or "无"))
        for name, tb in tbs:
            out.append("")
            out.append("=== %s ===" % name)
            dump(name, tb, expids[0], hproc, names, out)
    finally:
        k32.CloseHandle(hproc)
    txt = "\n".join(out)
    print(txt)
    try:
        with open("_probe_tray_dump.out", "w", encoding="utf-8") as f:
            f.write(txt)
    except Exception:                                        # noqa
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
