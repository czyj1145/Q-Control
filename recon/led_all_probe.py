# -*- coding: utf-8 -*-
r"""
找「一条命令就能管多个分区」的 LED 地址 —— 神光同步能不能一次同时变，全看它。

背景（2026-11-15）
    一条 `0x0100` 只能写一个 LED 地址，三区同色必须三条命令，每条 EC 要 ~46ms，
    合计 ~190ms。联动调色时人眼能看出一区一区地变；原厂同步模式是"同时"的，
    所以它多半用了某个"整条 / 全体"的地址（或固件认的位掩码）。

做法
    先把当前三区颜色记下来 → 然后**同一个鲜艳颜色**依次写到候选地址上，
    每个地址停几秒。人眼只需回答："这一步哪些区变了"。
    结束（含 Ctrl+C）一律把原色写回去。

用法
    python led_all_probe.py                 # 默认 6,7,8,0x38,0x07,0x3f，每个 5 秒
    python led_all_probe.py 6,0x38 4        # 指定地址与每步秒数

⚠️ 必须走**单条**接口（`{level,r,g,b,addr}`），不能走 `zones`：
   `zones` 那条路会让后端按请求重建 `presets.backlight.zones`，只发一个地址
   就把三区配置砍成一区（2026-11-14 采集事故同一个坑）。
"""
import json
import sys
import time
import urllib.request

B = "http://127.0.0.1:8765"


def get(path):
    with urllib.request.urlopen(B + path, timeout=15) as r:
        return json.loads(r.read().decode())


def post(path, body):
    req = urllib.request.Request(
        B + path, data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode())


def write_one(addr, rgb, level=2, effect=1):
    return post("/api/ec/backlight", {
        "level": level, "effect": effect,
        "r": rgb[0], "g": rgb[1], "b": rgb[2], "addr": addr,
    })


def restore(zones, level=2, effect=1):
    return post("/api/ec/backlight", {
        "level": level, "effect": effect, "force": True,
        "zones": [{"addr": a, "rgb": c} for a, c in zones],
    })


addrs = [int(x, 0) for x in (sys.argv[1].split(",") if len(sys.argv) > 1
                             else ["6", "7", "8", "0x38", "0x07", "0x3f"])]
hold = float(sys.argv[2]) if len(sys.argv) > 2 else 5.0

model = get("/api/model")
bl = model["presets"]["backlight"]
zones = [(z["addr"], z["rgb"]) for z in bl.get("zones", [])]
level = 2
print("当前分区：%s" % ", ".join("%d=%s" % (a, c) for a, c in zones), flush=True)
print("候选地址：%s   每个 %.1f 秒   共 %.0f 秒"
      % (addrs, hold, hold * len(addrs)), flush=True)
print("每一步都写**纯绿**。请记下「哪些区变绿了」。\n", flush=True)

try:
    for i, a in enumerate(addrs, 1):
        print("%d/%d  地址 0x%02X (%d) → 纯绿 … 请观察" % (i, len(addrs), a, a), flush=True)
        try:
            r = write_one(a, [0, 255, 0], level)
            print("        resp ok=%s addr=%s" % (r.get("ok"), r.get("addr")), flush=True)
        except Exception as e:  # noqa
            print("        失败：%s" % e, flush=True)
        time.sleep(hold)
        # 每一步之间先恢复原色，否则上一步的结果会干扰下一步的判断
        try:
            restore(zones, level)
        except Exception as e:  # noqa
            print("        恢复失败：%s" % e, flush=True)
        time.sleep(0.6)
except KeyboardInterrupt:
    print("\n中断，正在恢复原色…", flush=True)
finally:
    try:
        restore(zones, level)
        print("\n已恢复原色：%s" % ", ".join("%d=%s" % (a, c) for a, c in zones), flush=True)
    except Exception as e:  # noqa
        print("\n恢复失败：%s" % e, flush=True)
