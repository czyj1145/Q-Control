# -*- coding: utf-8 -*-
r"""干净复测 v4：UFA=0xFF 是不是「EC 原生自动」——带污染检测。

为什么要有污染检测（2026-10-13 教训）：
  v3 测试期间用户正在界面里拖风扇滑块（ec-writes.log 出现大量非本脚本的
  0x0205 写入），风扇的起伏其实来自用户写值，"随温度变化"是假象。
  ⇒ 本脚本启动后持续 tail ec-writes.log，一旦出现非本脚本的 0x0205 写入，
    立即标记 CONTAMINATED 并中止（数据作废），绝不给出错误结论。

判定（仅在无污染时有效）：
  写 0xFF 后风扇随温度上下变化 ⇒ EC 原生自动（重大发现）
  风扇钉死不动                 ⇒ 被忽略/钳定

安全：温度 > 92°C 回滚中止；结束必回滚；请求带重试。
负载：subprocess.Popen 独立进程（绝不用无守卫的 multiprocessing）。

用法：python _fan_auto4.py [0xFF]
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
WRITES_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter", "ec-writes.log")

BURNER = r'''
import sys, time
x = 0
t0 = time.time()
while time.time() - t0 < float(sys.argv[1]):
    x = (x * 1103515245 + 12345) & 0x7FFFFFFF
print(x & 1)
'''

contaminated = []          # 记录污染事件


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
    post("/api/ec/arm", {"cmd": 0x0205})
    return post("/api/ec/raw", {"sub": "0x0205", "group": "0xFB00",
                                "args": [d0, 0, d2, 0, d1, 0, 0, 0]})


class LogTail:
    """tail ec-writes.log，发现非本脚本的 0x0205 写入即报污染"""

    def __init__(self):
        self.off = 0
        try:
            self.off = os.path.getsize(WRITES_LOG)
        except OSError:
            pass

    def poll(self):
        try:
            with open(WRITES_LOG, "rb") as f:
                f.seek(self.off)
                chunk = f.read()
                self.off += len(chunk)
        except OSError:
            return
        for line in chunk.decode("utf-8", "ignore").splitlines():
            if "0x0205" in line and "原始 0x0205" not in line:
                contaminated.append(line)
                print("   !! 污染：检测到外部 0x0205 写入 -> %s" % line.strip(),
                      flush=True)


def watch(seconds, tag, tail):
    hot = False
    for i in range(int(seconds / 2)):
        time.sleep(2)
        tail.poll()
        if contaminated:
            print("   !! 出现外部写入，本段数据作废", flush=True)
            return "dirty"
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
    return "hot" if hot else "ok"


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
    val = int(sys.argv[1], 0) if len(sys.argv) > 1 else 0xFF
    tail = LogTail()
    print("=== 0. 停软件曲线 + 记录日志基线 ===")
    print("  ", post("/api/fan/mode", {"mode": "manual"}))
    time.sleep(1)
    print("   当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())

    print("\n=== 1. 基线 %d%% ===" % SAFE)
    print("  ", fans_raw(SAFE, SAFE, SAFE))
    r = watch(8, "base", tail)
    if r == "dirty":
        print("\n结论：基线阶段就被污染，测试无效。")
        return

    print("\n" + "#" * 62)
    print("############ UFA = 0x%02X (%d) ############" % (val, val))
    print("#" * 62)
    print("--- 写 0x%02X ---" % val)
    print("  ", fans_raw(val, val, val))

    print("--- 空载观察 20 秒 ---")
    r = watch(20, "idle", tail)
    if r != "ok":
        print("\n结论：测试无效（污染或过热）。")
        return

    print("--- 加负载（%d 进程）30 秒 ---" % NCORES)
    ps, path = burn(30)
    try:
        r = watch(30, "load", tail)
    finally:
        kill(ps, path)
    if r != "ok":
        print("\n结论：测试无效（污染或过热）。")
        return

    print("--- 卸载 40 秒 ---")
    r = watch(40, "cool", tail)
    if r != "ok":
        print("\n结论：测试无效（污染或过热）。")
        return

    print("\n=== 回滚安全值 %d%% ===" % SAFE)
    print("  ", fans_raw(SAFE, SAFE, SAFE))
    watch(8, "rollback", tail)
    print("\n完成（无污染）。请根据上方风扇/温度曲线判定。")


if __name__ == "__main__":
    main()
