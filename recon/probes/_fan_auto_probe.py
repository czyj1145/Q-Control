# -*- coding: utf-8 -*-
r"""验证 0x7F / 0xFF 是否触发"EC 自动"：写值 → 加 CPU 负载 → 看转速是否随温度走。

上一轮观察：
  - 0xFF(255)：无变化（被忽略？）
  - 0x80(128)：爬到 ~5000 RPM 稳住（≈100% 占空比）
  - 0x7F(127)：先爬到 5000，随后**随温度下降而下降**（2894@59°C）——疑似自动

本轮：写目标值后先静置 25 秒，再加满载 40 秒，再卸载 30 秒。
若转速跟随温度升降 ⇒ 该值 = 自动（或 EC 已接管）。
"""
import json
import subprocess
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
SAFE = 60


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
    ec = (get("/api/status").get("ec") or {})
    return ec.get("cpu_temp"), ec.get("gpu_temp"), ec.get("fan1_rpm"), ec.get("fan2_rpm")


def fans_raw(d0, d1, d2):
    return post("/api/ec/raw", {"sub": "0x0205", "group": "0xFB00",
                                "args": [d0, 0, d2, 0, d1, 0, 0, 0]})


def burn(stop):
    code = "import time\nx=0\nwhile True: x=(x*1103515245+12345)&0x7FFFFFFF"
    return subprocess.Popen([sys.executable, "-c", code],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def watch(seconds, tag):
    rows = []
    for i in range(seconds // 2):
        time.sleep(2)
        t, g, f1, f2 = st()
        rows.append((t, f1))
        print("   [%s] t+%03ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s"
              % (tag, (i + 1) * 2, t, g, f1, f2))
    return rows


if __name__ == "__main__":
    # EC 写入总闸 2026-10-13 起常开不可关（/api/ec/writes 接口已拆），无需再开

    for val in (0x7F, 0xFF):
        print("\n############ UFA = 0x%02X (%d) ############" % (val, val))
        print("--- 基线 %d%% ---" % SAFE)
        fans_raw(SAFE, SAFE, SAFE)
        watch(8, "base")

        print("--- 写 0x%02X ---" % val)
        fans_raw(val, val, val)
        watch(25, "idle")

        print("--- 加满载（8 进程）40 秒 ---")
        ps = [burn(None) for _ in range(8)]
        try:
            watch(40, "load")
        finally:
            for p in ps:
                p.kill()

        print("--- 卸载 30 秒 ---")
        watch(30, "cool")

        print("--- 回滚 %d%% ---" % SAFE)
        fans_raw(SAFE, SAFE, SAFE)
        watch(10, "rb")

    print("\n完成。")
