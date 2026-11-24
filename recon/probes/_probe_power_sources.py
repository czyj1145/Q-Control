# -*- coding: utf-8 -*-
"""CPU 功耗两条来源对照 + 采样各阶段计时（只读探针，不写 EC、不改配置）。

2026-10-16 排查「功耗突然下降」时用的诊断脚本，留档理由有两条实测结论：

1. **两条来源必须对照着看**：`\\Energy Meter(RAPL_Package0_PKG)\\Power`（PDH，
   界面首选）与 HWiNFO 的 `CPU Package Power` 是同一个物理量（MSR 的 RAPL 计数器），
   同量级但**不是同一时刻的值** —— HWiNFO 自己 1~2 秒才刷一轮（本探针里能看到
   hw_W 连续两轮完全相同，就是踩在 HWiNFO 的刷新间隔里）。
2. ⚠️ **PDH 那个 Power 计数器是"速率计数器"：读的是两次 `PdhCollectQueryData`
   之间的平均功耗**。所以**同一秒里读两次必然拿到垃圾**（窗口 ~0.2s，而底层计数器
   的更新粒度比它粗）：本探针的 `cpu.power` 一列会打出 21.7 / 106.7 / 17.6 这种
   离谱值 —— 那是短窗口的量化误差，不是真值。**界面上 `cpu_power` 一栏每轮只读
   一次（一轮 2 秒），别在任何地方加第二处 `_rapl.value()` 调用**，否则读数就废了。

用法（真机、控制中心不用在跑）：
    python recon/probes/_probe_power_sources.py [轮数]
输出每轮：pdh_W / hw_W / pl1 / pl2 + 各阶段耗时（ms）。
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "..", "..", "ControlCenter"))

import psutil                      # noqa: E402
import sensors                     # noqa: E402
import hwinfo                      # noqa: E402

N = int(sys.argv[1]) if len(sys.argv) > 1 else 20
S = sensors.Sensors()


def timed(fn):
    t = time.perf_counter()
    try:
        v = fn()
    except Exception as e:                        # noqa
        v = "EXC:%r" % (e,)
    return v, (time.perf_counter() - t) * 1000.0


print("%3s %8s %8s %6s %6s %7s %7s %7s %7s" % (
    "it", "pdh_W", "hw_W", "pl1", "pl2", "cpu_ms", "gpu_ms", "hw_ms", "sum_ms"))
for i in range(N):
    t0 = time.perf_counter()
    pdh, _ = timed(sensors._rapl.value)
    hw, m_hw = timed(lambda: hwinfo.find_cpu_power()[0])
    lim, _ = timed(lambda: (hwinfo.find_cpu_power_limits()[0] or {}))
    cpu, m_cpu = timed(S.cpu)
    gpu, m_gpu = timed(S.gpu)
    tot = (time.perf_counter() - t0) * 1000.0
    c = cpu if isinstance(cpu, dict) else {}
    g = gpu if isinstance(gpu, dict) else {}
    print("%3d %8s %8s %6s %6s %7.1f %7.1f %7.1f %7.1f" % (
        i, pdh, hw, (lim or {}).get("pl1"), (lim or {}).get("pl2"),
        m_cpu, m_gpu, m_hw, tot))
    print("      cpu.power=%s cpu.usage=%s | gpu.power=%s" % (
        c.get("power"), c.get("usage"), g.get("power")))
    time.sleep(1.0)
