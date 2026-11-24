# -*- coding: utf-8 -*-
"""
实证：写 UFA=0 到底是「交回 EC 自动」还是「停转」。

背景（2026-10-13 用户报「关闭手动风扇不会恢复自动」）：
CPU 82~84°C、风扇只有 ~1500 RPM 一动不动 ⇒ 卡在之前写死的手动值 30%。
本机写组（GWF0=0xFB00）里只有 0x0205 一个风扇命令，没有独立的 auto/max 命令，
所以唯一能验证的就是 UFA 写 0 的语义。

流程（全程带回滚保护）：
  1. 打开 EC 写入 → 写 70% 先把温度压下来（当前 84°C，停转实验才有安全余量）
  2. 等温度回落
  3. raw 下发 0x0205 @0xFB00，UFA1/2/3 全 0
  4. 观测 N 秒：转速归零或温度飙到阈值 ⇒ 立刻写回 70% 结束（判定"停转"）
     转速自己爬起来并随温度变 ⇒ 判定"EC 接管 = 自动"

用法：python _fan_release_test.py
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
SAFE = 70            # 回滚/降温用的转速
STOP_RPM = 300       # 低于此值视为停转
TEMP_MAX = 95        # 温度硬阈值，超了立刻回滚


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
    return ec.get("cpu_temp"), ec.get("gpu_temp"), \
        ec.get("fan1_rpm"), ec.get("fan2_rpm")


def fans(pct):
    """走正式接口写转速（内部会 clamp 到 min_duty）"""
    return post("/api/ec/fan", {"cpu": pct, "gpu": pct, "sys": pct})


def fans_raw(d0, d1, d2):
    """绕过 clamp：raw 下发 0x0205，args = [d0,d1,d2,d3,a,b,c,d]
    UFA1 = d0&0xFF, UFA2 = a(buf[8])&0xFF, UFA3 = d2&0xFF"""
    return post("/api/ec/raw", {"sub": "0x0205", "group": "0xFB00",
                                "args": [d0, 0, d2, 0, d1, 0, 0, 0]})


# EC 写入总闸 2026-10-13 起常开不可关（/api/ec/writes 接口已拆），无需再开
print("\n=== 1. 先写 %d%% 降温（当前状态：%s）===" % (SAFE, st()))
print(fans(SAFE))
for i in range(8):
    time.sleep(3)
    print("  t+%02ds  CPU=%s°C GPU=%s°C  FAN1=%s FAN2=%s" % ((i + 1) * 3, *st()))

print("\n=== 2. raw 下发 0x0205 UFA1/2/3 = 0 ===")
print(fans_raw(0, 0, 0))

print("\n=== 3. 观测 24 秒（转速归零或 CPU≥%d°C 立刻回滚 %d%%）===" % (TEMP_MAX, SAFE))
verdict = "不明（转速既没归零也没明显自动爬升）"
zero_seen = False
peak = 0
for i in range(12):
    time.sleep(2)
    t, g, f1, f2 = st()
    print("  t+%02ds  CPU=%s°C GPU=%s°C  FAN1=%s FAN2=%s" % ((i + 1) * 2, t, g, f1, f2))
    peak = max(peak, t or 0)
    if (f1 or 0) < STOP_RPM and (f2 or 0) < STOP_RPM:
        zero_seen = True
    if zero_seen or (t or 0) >= TEMP_MAX:
        verdict = ("停转：写 0 后风扇归零、EC 不接管" if zero_seen
                   else "温度触顶 %s°C 前自动回滚" % t)
        break

print("\n=== 4. 回滚到 %d%% ===" % SAFE)
print(fans(SAFE))
time.sleep(3)
print("  回滚后：CPU=%s°C  FAN1=%s FAN2=%s" % st())

print("\n结论：", verdict)
sys.exit(0)
