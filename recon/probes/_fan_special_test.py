# -*- coding: utf-8 -*-
r"""试探：UFA1/2/3 写特殊值（0xFF / 0x80 / 0x7F）会被 EC 解释成什么。

背景：DSDT 写组只有 0x0205 一个风扇命令（UFA1/2/3 = 三风扇占空比），
没有独立的 auto/max 命令。若 EC 固件用某个特殊值表示"自动/最大"，
这里就能试出来。全程带回滚。

判定：
  - 转速飙到 ~5500+ 且不随温度回落 ⇒ 0xFF 被当成"最大"
  - 转速随温度自己变 ⇒ 被当成"自动"（重大发现）
  - 转速归零 ⇒ 被当成 0/停转
  - 没变化 ⇒ 被忽略或钳到 100%

用法：python _fan_special_test.py
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
SAFE = 60            # 基线与回滚值
TEMP_MAX = 90        # 温度硬阈值


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


# EC 写入总闸 2026-10-13 起常开不可关（/api/ec/writes 接口已拆），无需再开
print("   当前：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())

print("\n=== 1. 基线 %d%% ===" % SAFE)
fans_raw(SAFE, SAFE, SAFE)
for i in range(4):
    time.sleep(2)
    print("   t+%02ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % ((i + 1) * 2, *st()))

for val in (0xFF, 0x80, 0x7F):
    print("\n=== 2. UFA1/2/3 = 0x%02X (%d) ===" % (val, val))
    r = fans_raw(val, val, val)
    print("   回包:", json.dumps(r, ensure_ascii=False)[:200])
    verdict = "无明显变化"
    for i in range(10):
        time.sleep(2)
        t, g, f1, f2 = st()
        print("   t+%02ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s"
              % ((i + 1) * 2, t, g, f1, f2))
        if (t or 0) >= TEMP_MAX:
            verdict = "温度触顶，立刻回滚"
            break
    print("   小结[0x%02X]: %s" % (val, verdict))
    print("\n=== 3. 回滚 %d%% ===" % SAFE)
    fans_raw(SAFE, SAFE, SAFE)
    time.sleep(4)
    print("   回滚后：CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s" % st())

print("\n完成。")
