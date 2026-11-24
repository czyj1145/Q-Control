# -*- coding: utf-8 -*-
"""判别实验：单独重放 powercfg /setactive（当前方案 → 自己），不碰 EC。

2026-10-16 23:42 用户报「功耗突然下降」时的一次性判别探针
（结论：**复现不出来** —— CPU 功耗 45~82W 无变化、采样周期 2.05→2.02s，
所以「点档位按钮 → 前端顺手重设电源计划」这条不是功耗骤降的原因）。

用途：区分「点档位按钮后 CPU 功耗骤降」到底是
  (A) 无条件重写 EC 0x0300(APMD) 引起的，还是
  (B) 前端随后执行的 powercfg /setactive 重设电源方案引起的。
本脚本只做 (B)：语义上是空操作（切到已经生效的同一个方案），不改任何配置。

只读 /api/status 与 /api/history，不做任何 EC 写入。
只用标准库 + HTTP 打运行中的控制中心，**不需要** sys.path 指回 ControlCenter。
"""
import json
import subprocess
import sys
import time
import urllib.request

GUID = sys.argv[1] if len(sys.argv) > 1 else None
BEFORE = 30.0      # 基线观察秒数
AFTER = 95.0       # 触发后观察秒数


def api(path):
    with urllib.request.urlopen("http://127.0.0.1:8765" + path, timeout=5) as r:
        return json.load(r)


def active_guid():
    out = subprocess.run(["powercfg", "/getactivescheme"], capture_output=True,
                         text=True, errors="ignore").stdout
    for tok in out.replace("(", " ").split():
        if len(tok) == 36 and tok.count("-") == 4:
            return tok
    return None


def snap():
    try:
        s = api("/api/status")
    except Exception as e:
        return {"cpu_p": None, "gpu_p": None, "err": str(e)}
    return {"cpu_p": (s.get("cpu") or {}).get("power"),
            "gpu_p": (s.get("gpu") or {}).get("power"),
            "ec_t": (s.get("ec") or {}).get("cpu_temp"),
            "mode": (s.get("perf") or {}).get("mode")}


def period_stats():
    try:
        pts = (api("/api/history") or {}).get("points") or []
    except Exception:
        return "history 不可用"
    if len(pts) < 5:
        return "样本太少"
    d = [round(pts[i]["t"] - pts[i - 1]["t"], 2) for i in range(1, len(pts))]
    recent = d[-20:]
    return "最近 20 个周期 %.2f~%.2f s（均值 %.2f）" % (
        min(recent), max(recent), sum(recent) / len(recent))


g = GUID or active_guid()
print("当前生效方案 = %s" % g)
print("【基线】%s" % period_stats())
t0 = time.time()
while time.time() - t0 < BEFORE:
    s = snap()
    print("  %5.1fs  cpu=%-6s gpu=%-6s ec_t=%s" % (
        time.time() - t0, s["cpu_p"], s["gpu_p"], s["ec_t"]))
    time.sleep(2.0)

print("\n>>> 执行 powercfg /setactive %s（切到当前已生效的同一方案，不含任何 EC 写入）" % g)
r = subprocess.run(["powercfg", "/setactive", g], capture_output=True, text=True,
                   errors="ignore")
print("    返回码 = %s %s" % (r.returncode, (r.stdout or r.stderr or "").strip()))
t1 = time.time()
while time.time() - t1 < AFTER:
    s = snap()
    print("  %5.1fs  cpu=%-6s gpu=%-6s ec_t=%s" % (
        time.time() - t1, s["cpu_p"], s["gpu_p"], s["ec_t"]))
    time.sleep(2.0)

print("\n【触发后】%s" % period_stats())
print("模式 = %s（应仍为 %s）" % (snap()["mode"], snap()["mode"]))
