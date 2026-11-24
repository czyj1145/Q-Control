# -*- coding: utf-8 -*-
r"""干净复测：UFA=0xFF（及 0xFE/0xFD）到底是不是「自动」。

为什么必须重测（2026-10-13）：
  第一轮写 0xFF 显示「无变化」——但当时基线是 60%（≈3000 RPM@60°C）。
  若 0xFF = 自动，而 EC 自动曲线在 60°C 的输出恰好也是 ~3000 RPM，
  就会呈现假阴性！被 fork 炸弹污染的第二轮里，0xFF 反而呈现
  「负载 5000 / 空载 2500」——正是自动曲线的特征。
  ⇒ 必须用「空载→负载→空载」的温差来判定，不能只看空载有没有变化。

判定标准：
  写值后风扇随温度上下变化（负载升、卸载降）⇒ 自动
  风扇钉死不动                                    ⇒ 手动/被忽略

安全：温度 > 92°C 立即回滚；结束必回滚；后端请求带重试。
负载：subprocess.Popen 独立进程（绝不用无守卫的 multiprocessing）。

用法：python _fan_auto3.py [0xFF 0xFE ...]
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
NCORES = min(8, os.cpu_count() or 8)

BURNER = r'''
import sys, time
x = 0
t0 = time.time()
while time.time() - t0 < float(sys.argv[1]):
    x = (x * 1103515245 + 12345) & 0x7FFFFFFF
print(x & 1)
'''


def _req(path, payload=None, tries=4):
    last = None
    for i in range(tries):
        try:
            if payload is None:
                with urllib.request.urlopen(BASE + path, timeout=10) as r:
                    return json.loads(r.read().decode())
            req = urllib.request.Request(
                BASE + path, data=json.dumps(payload).encode(),
                headers={"Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=10) as r:
                return json.loads(r.read().decode())
        except Exception as e:              # noqa
            last = e
            time.sleep(1.5 * (i + 1))
    raise last


def post(path, payload):
    return _req(path, payload)


def get(path):
    return _req(path)


def st():
    d = get("/api/status")
    ec = d.get("ec") or {}
    return ec.get("cpu_temp"), ec.get("gpu_temp"), ec.get("fan1_rpm"), ec.get("fan2_rpm")


def fans_raw(d0, d1, d2):
    # 0x0205 是高危命令：raw 绕过 set_fans，必须自己先 arm（一次性，TTL 30s）
    post("/api/ec/arm", {"cmd": 0x0205})
    return post("/api/ec/raw", {"sub": "0x0205", "group": "0xFB00",
                                "args": [d0, 0, d2, 0, d1, 0, 0, 0]})


def watch(seconds, tag):
    hot = False
    for i in range(int(seconds / 2)):
        time.sleep(2)
        try:
            c, g, f1, f2 = st()
        except Exception as e:              # noqa
            print("   [%s] 采样失败 %r" % (tag, e), flush=True)
            continue
        print("   [%s] t+%03ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s"
              % (tag, (i + 1) * 2, c, g, f1, f2), flush=True)
        if (c or 0) >= TEMP_GUARD:
            print("   !! 温度 %s°C 超阈值，立即回滚" % c, flush=True)
            hot = True
            break
    return hot


def burn(seconds):
    fd, path = tempfile.mkstemp(suffix=".py", prefix="_burn_")
    with os.fdopen(fd, "w") as f:
        f.write(BURNER)
    ps = [subprocess.Popen([sys.executable, path, str(seconds + 60)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
          for _ in range(NCORES)]
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
    vals = [int(v, 0) for v in sys.argv[1:]] or [0xFF]
    print("=== 0. 确保软件曲线已停（手动模式）===")
    print("  ", post("/api/fan/mode", {"mode": "manual"}))
    time.sleep(1)
    print("   当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())

    print("\n=== 1. 基线 %d%% ===" % SAFE)
    print("  ", fans_raw(SAFE, SAFE, SAFE))
    watch(8, "base")

    for val in vals:
        print("\n" + "#" * 62)
        print("############ UFA = 0x%02X (%d) ############" % (val, val))
        print("#" * 62)
        print("--- 写 0x%02X ---" % val)
        print("  ", fans_raw(val, val, val))
        print("--- 空载观察 24 秒 ---")
        if watch(24, "idle"):
            break
        print("--- 加负载（%d 进程）30 秒：自动的话风扇该往上走 ---" % NCORES)
        ps, path = burn(30)
        try:
            if watch(30, "load"):
                break
        finally:
            kill(ps, path)
        print("--- 卸载 40 秒：自动的话风扇该往下走 ---")
        if watch(40, "cool"):
            break

    print("\n=== 回滚安全值 %d%% ===" % SAFE)
    try:
        print("  ", fans_raw(SAFE, SAFE, SAFE))
    except Exception as e:                  # noqa
        print("   回滚失败：%r" % e)
    watch(8, "rollback")
    print("\n完成。当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())


if __name__ == "__main__":
    main()
