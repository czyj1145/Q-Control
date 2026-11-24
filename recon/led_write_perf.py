# -*- coding: utf-8 -*-
r"""
重新测量 0x0100（键盘背光）单条命令的**真实**耗时 —— 借管理员服务器发命令。

为什么要重测：PROTOCOL.md §九 的"三区一帧 308ms / 3.2fps"结论，
可能把三样东西算进去了：
  ① hal._throttle 的人为 sleep（MIN_GAP=0.2，写命令也被钳到 5 条/秒）
  ② hal.WmiHAL._raw 每次写**枚举两遍 WMI 实例**（实测各 ~5ms）
  ③ 灯效引擎循环本身的 floor（FX_GAP × 分区数）

测法（绕开节流）：**空闲 1.3s 后再单发一条命令**，此时 gap 的 wait=0，
HTTP 往返（本地 ~1-2ms）≈ 命令自身的真实耗时 W。
节流线程/采样线程偶尔插进来会污染个别样本 ⇒ 取 min 才是干净的 W。

只写 0x0100（背光），写的是 model.json 里**当前的分区颜色**，观感不变；
跑完再通过 /api/ec/backlight 下发一次原设置做还原。
"""
import json
import statistics
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
OP = urllib.request.build_opener(urllib.request.ProxyHandler({}))
IDLE = 1.3
N = 15


def call(path, body=None, timeout=20):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(
        BASE + path, data=data,
        headers={"Content-Type": "application/json"} if data else {})
    with OP.open(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def timed(path, body):
    t0 = time.perf_counter()
    r = call(path, body)
    return (time.perf_counter() - t0) * 1000, r


def show(label, ts):
    ts = sorted(ts)
    print("  %-30s n=%2d  min %6.1f  p50 %6.1f  max %6.1f ms"
          % (label, len(ts), ts[0], statistics.median(ts), ts[-1]))
    return ts[0]


st = call("/api/status")
env = st.get("env") or {}
print("服务器: ec_writes=%s  write_ok=%s  admin=%s"
      % (st.get("ec_writes"), env.get("write_ok"), env.get("admin")))
if not env.get("write_ok"):
    print("⚠️ 服务器不是管理员或写入不可用 —— 测不了写入，退出")
    raise SystemExit(1)

lv = int(st.get("ec", {}).get("backlight_level") or 1)
print("当前背光档位:", lv)
dt3d = (1 << 4) | (lv & 0x0F)          # 灯效=常亮(1)，档位保持
print("下发用 DT3D = 0x%02X" % dt3d)

ZONES = [(5, [255, 0, 0]), (4, [0, 255, 0]), (3, [0, 0, 255])]

print("\n[A] 0x0200 状态读（对照基线，空闲后单发）")
ts = []
for _ in range(8):
    time.sleep(1.0)
    t, _r = timed("/api/ec/raw", {"sub": "0x0200", "args": [0] * 8,
                                  "group": "0xFA00", "gap": 0.001})
    ts.append(t)
show("0x0200 读", ts)

print("\n[B] 0x0100 背光写：逐分区、空闲后单发（真实耗时 W）")
per = {}
for addr, (r, g, b) in ZONES:
    ts = []
    for _ in range(N):
        time.sleep(IDLE)
        t, resp = timed("/api/ec/raw", {
            "sub": "0x0100",
            "args": [addr, 0, 0, 0, b, g, r, dt3d],
            "group": "0xFB00", "gap": 0.001})
        if not resp.get("ok"):
            print("  写入失败:", resp)
            break
        ts.append(t)
    per[addr] = show("0x0100 写 addr=%d" % addr, ts)

print("\n[C] /api/ec/backlight 单区写（含 server/model 全套开销）")
ts = []
for _ in range(N):
    time.sleep(IDLE)
    t, _r = timed("/api/ec/backlight",
                  {"level": lv, "r": 255, "g": 255, "b": 255,
                   "addr": 6, "effect": 1})
    ts.append(t)
show("backlight 单区", ts)

print("\n[D] 三区连发（不空闲）—— 现在的应用级间隔（MIN_GAP=0.2 钳制下）")
t0 = time.perf_counter()
for addr, (r, g, b) in ZONES:
    call("/api/ec/raw", {"sub": "0x0100", "args": [addr, 0, 0, 0, b, g, r, dt3d],
                         "group": "0xFB00", "gap": 0.001})
frame = (time.perf_counter() - t0) * 1000
print("  三区一帧（当前应用级）: %.0f ms  ⇒ %.1f fps" % (frame, 1000.0 / frame))

w = sum(per.values()) / 3.0
print("\n=== 汇总 ===")
print("单条 0x0100 真实耗时 W ≈ %.1f ms（三区实测均值）" % w)
print("⇒ 若不受 MIN_GAP 限制，三区一帧理论下限 ≈ %.0f ms ⇒ %.1f fps"
      % (3 * w, 1000.0 / (3 * w)))

print("\n还原原分区设置…")
print("  ", call("/api/ec/backlight", {
    "level": lv, "effect": 1,
    "zones": [{"addr": 5, "rgb": [255, 0, 0]},
              {"addr": 4, "rgb": [0, 255, 0]},
              {"addr": 3, "rgb": [0, 0, 255]}]}).get("ok"))
