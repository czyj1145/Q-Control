# -*- coding: utf-8 -*-
"""查 / 测 HWiNFO 的「露脸」情况：可见窗口 + 托盘图标。

实现全部在 HWiNFO 读取层（枚举窗口 / 托盘图标 / 隐藏），
本脚本只是它的命令行外壳，方便手动复核。

## 判据说明

`Shell_NotifyIconGetRect(&identifier, &rect)` **只在图标真实存在时返回 S_OK**，
identifier = (hWnd, uID)，而 hWnd 必是 HWiNFO 自己的某个顶层窗口（EnumWindows
连不可见的也能枚举到）。所以「扫 (hWnd, uID) 全部落空」= 它确实没注册托盘图标。

⚠️ 这条链路**不需要管理员**：GetRect / NIM_DELETE 都是「跟外壳（explorer）说话」，
不碰 HWiNFO 进程，因此不受 UIPI 限制。但 HWiNFO 必须**已经在跑**
（启动控制中心即可，或双击 `_probe_hwi_all.cmd` 自提权拉起）。

## 用法

    python _probe_hwinfo_tray.py report      # 只报告
    python _probe_hwinfo_tray.py hide        # 收掉可见窗口 + 托盘图标，观察 30s
    python _probe_hwinfo_tray.py watch 60    # 只观察 60 秒
"""
import os
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "ControlCenter"))
import hwinfo                                              # noqa: E402

OUT = os.path.join(HERE, "_probe_hwinfo_tray.out")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "report"
    tail = float(sys.argv[2]) if len(sys.argv) > 2 else 30.0
    L = []

    def log(s):
        print(s)
        L.append(s)

    log("HWiNFO64.exe PID = %s" % (hwinfo.pids() or
                                 "（没在跑！先启动控制中心，或双击 _probe_hwi_all.cmd）"))
    wins = hwinfo.windows()
    log("")
    log("=== 顶层窗口（%d 个，可见 %d 个）==="
        % (len(wins), sum(1 for w in wins if w[3])))
    for hwnd, cls, title, vis, _i in wins:
        log("  hwnd=0x%08X class=%-46s visible=%-5s title=%r"
            % (hwnd, cls, vis, title))
    icons = hwinfo.tray_icons()
    log("")
    log("=== 托盘图标（%d 个）===" % len(icons))
    for hwnd, uid, cls, rect in icons:
        log("  hwnd=0x%08X uID=%-3d class=%s 屏幕矩形=%s" % (hwnd, uid, cls, rect))
    if not icons:
        log("  （没有 —— 正是期望的状态）")

    if mode == "hide":
        log("")
        log("=== 执行 hide_ui()（**显式传入全部实例**：这是手动测试工具，"
            "默认只管控制中心自己拉起的那只）===")
        log("  ours_pid=%s ours_running=%s"
            % (hwinfo.stats()["ours_pid"], hwinfo.stats()["ours_running"]))
        log("  结果：%s" % hwinfo.hide_ui(pids_filter=set(hwinfo.pids())))
        if tail > 0:
            log("")
            log("=== 观察 %.0f 秒，看它会不会自己冒出来 ===" % tail)
            t0 = time.time()
            while time.time() - t0 < tail:
                time.sleep(5.0)
                log("  +%4.1fs：可见窗口 %d、托盘图标 %d"
                    % (time.time() - t0, sum(1 for w in hwinfo.windows() if w[3]),
                       len(hwinfo.tray_icons())))

    log("")
    try:
        d = hwinfo.all_data()
        log("共享内存复查（隐藏 UI 不该影响取数）：PCH=%s CPU功耗=%s CPU电压=%s "
            "显存结温=%s 虚拟内存=%s"
            % (d["pch"], d["cpu_power"][0], d["cpu_voltage"][0],
               d["gpu_mem_temp"][0], d["vmem"]))
    except Exception as e:                                   # noqa
        log("共享内存复查失败：%r" % (e,))

    try:
        with open(OUT, "w", encoding="utf-8") as f:
            f.write("\n".join(L))
        print("\n已写入 %s" % OUT)
    except Exception:                                        # noqa
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
