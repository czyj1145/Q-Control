# -*- coding: utf-8 -*-
r"""干净复测：UFA1/2/3 写 0x7F / 0xFF / 0x80 分别被 EC 解释成什么。

背景（2026-10-13）：
  DSDT 写组 9 个分支全枚举，只有 0x0205 碰风扇（UFA1/2/3 = 三风扇占空比），
  没有独立 auto/max 命令。但第一轮干净实测（_fan_special_test.py）出现：
      0x80 → 爬到 5000 稳住（= 钳成 100%，"最大"）
      0xFF → 无变化
      0x7F → 先爬到 5000，随后随温度回落到 2894@59°C ← 疑似 EC 自动曲线
  第二轮被 fork 炸弹污染，数据作废。本脚本用干净方法复测 0x7F（重点）。

负载：subprocess.Popen 独立进程（⚠️ 绝不用 multiprocessing——
      无 __main__ 守卫时 Windows spawn 会重跑整个脚本导致无限分裂，已闯祸一次）。

安全：温度 > 92°C 立即回滚安全值并中止；结束必回滚。

用法：python _fan_auto2.py
"""
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
SAFE = 60
TEMP_GUARD = 92
NCORES = min(12, os.cpu_count() or 8)

BURNER = r'''
import sys, time
x = 0
t0 = time.time()
while time.time() - t0 < float(sys.argv[1]):
    x = (x * 1103515245 + 12345) & 0x7FFFFFFF
print(x & 1)
'''


def post(path, payload):
    req = urllib.request.Request(
        BASE + path, data=json.dumps(payload).encode(),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=10) as r:
        return json.loads(r.read().decode())


def get(path):
    with urllib.request.urlopen(BASE + path, timeout=10) as r:
        return json.loads(r.read().decode())


def st():
    d = get("/api/status")
    ec = d.get("ec") or {}
    return ec.get("cpu_temp"), ec.get("gpu_temp"), ec.get("fan1_rpm"), ec.get("fan2_rpm")


def fans_raw(d0, d1, d2):
    """绕过 clamp：raw 下发 0x0205，UFA1=d0, UFA2=buf[8], UFA3=d2"""
    return post("/api/ec/raw", {"sub": "0x0205", "group": "0xFB00",
                                "args": [d0, 0, d2, 0, d1, 0, 0, 0]})


def watch(seconds, tag):
    """采样打印；返回是否触发过温度保护"""
    hot = False
    for i in range(int(seconds / 2)):
        time.sleep(2)
        c, g, f1, f2 = st()
        print("   [%s] t+%03ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s"
              % (tag, (i + 1) * 2, c, g, f1, f2), flush=True)
        if (c or 0) >= TEMP_GUARD:
            print("   !! 温度 %s°C 超阈值，立即回滚" % c, flush=True)
            hot = True
            break
    return hot


def burn(seconds):
    """独立子进程造 CPU 满载，返回进程列表"""
    fd, path = tempfile.mkstemp(suffix=".py", prefix="_burn_")
    with os.fdopen(fd, "w") as f:
        f.write(BURNER)
    ps = []
    for _ in range(NCORES):
        ps.append(subprocess.Popen([sys.executable, path, str(seconds + 30)],
                                   stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL))
    return ps, path


def kill(ps, path):
    for p in ps:
        try:
            p.kill()
        except Exception:
            pass
    for p in ps:
        try:
            p.wait(timeout=5)
        except Exception:
            pass
    try:
        os.unlink(path)
    except Exception:
        pass


def main():
    print("=== 0. 准备：停软件曲线（改手动，保持当前转速）===")
    print("  ", post("/api/fan/mode", {"mode": "manual"}))
    time.sleep(2)
    print("   当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())

    print("\n=== 1. 基线 %d%% ===" % SAFE)
    print("  ", fans_raw(SAFE, SAFE, SAFE))
    watch(8, "base")

    for val in (0x7F, 0x80, 0xFF):
        print("\n" + "#" * 62)
        print("############ UFA = 0x%02X (%d) ############" % (val, val))
        print("#" * 62)
        print("--- 写 0x%02X ---" % val)
        print("  ", fans_raw(val, val, val))
        print("--- 空载观察 30 秒（机器在降温，看风扇是否跟着降）---")
        if watch(30, "idle"):
            break

        print("--- 加负载（%d 进程）30 秒 ---" % NCORES)
        ps, path = burn(30)
        try:
            if watch(30, "load"):
                break
        finally:
            kill(ps, path)
        print("--- 卸载 35 秒（看风扇是否跟着温度回落）---")
        if watch(35, "cool"):
            break

    print("\n=== 回滚安全值 %d%% ===" % SAFE)
    print("  ", fans_raw(SAFE, SAFE, SAFE))
    watch(10, "rollback")
    print("\n完成。当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())


if __name__ == "__main__":
    main()
