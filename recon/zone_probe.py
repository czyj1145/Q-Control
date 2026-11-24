# -*- coding: utf-8 -*-
r"""
逐个 LED 地址点亮探针 —— 用来标定「地址 ↔ 哪一块灯」。

键盘是三区背光，但每个区内部还包含多个背光分区（地址不止 3/4/5）。
原厂抓包里出现过 3/4/5/6/7/8，本脚本按顺序逐个写**纯红**，
每步停留几秒，人眼观察「哪一块亮」，即可把地址表补全。

用法：
    python zone_probe.py                 # 默认 3,4,5,6,7,8，每个停 6 秒
    python zone_probe.py 3,4,5,6,7,8,9   # 指定地址
    python zone_probe.py 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 6
"""
import json
import sys
import time
import urllib.request

B = "http://127.0.0.1:8765"


def post(path, body=None):
    req = urllib.request.Request(
        B + path, data=json.dumps(body or {}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode())


addrs = [int(x) for x in (sys.argv[1].split(",") if len(sys.argv) > 1
                          else ["3", "4", "5", "6", "7", "8"])]
hold = float(sys.argv[2]) if len(sys.argv) > 2 else 6.0

post("/api/ec/writes", {"enabled": True})
print("地址顺序：%s   每个停留 %.1f 秒   共 %.0f 秒"
      % (addrs, hold, hold * len(addrs)), flush=True)

for i, a in enumerate(addrs, 1):
    print("%d/%d  地址 %-3d → 纯红" % (i, len(addrs), a), flush=True)
    try:
        r = post("/api/ec/backlight",
                 {"level": 2, "r": 255, "g": 0, "b": 0, "addr": a, "effect": 1})
        print("        resp=%s" % json.dumps(r, ensure_ascii=False), flush=True)
    except Exception as e:  # noqa
        print("        失败：%s" % e, flush=True)
    time.sleep(hold)

# 收尾：把键盘恢复成白色，避免留一块红
try:
    post("/api/ec/backlight", {"level": 2, "r": 255, "g": 255, "b": 255,
                              "addr": 6, "effect": 1})
except Exception as e:  # noqa
    print("恢复失败：%s" % e)
print("已尝试恢复白色（地址 6）", flush=True)
