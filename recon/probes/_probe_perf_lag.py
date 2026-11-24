"""诊断：切性能模式后，/api/status 里的 perf.mode 要多久才跟上？

现象：界面按钮先乐观点亮 → 下一次轮询拿到**旧**快照 → 按钮弹回去 →
再下一次才跟上。看起来就是"乱跳 + 像切换失败"。

本脚本只读 + 一次写入（写完会还原原档位），不碰其他命令。
"""
import json
import time
import urllib.request

BASE = "http://127.0.0.1:8765"
OP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(path, body=None):
    data = json.dumps(body).encode("utf-8") if body is not None else None
    req = urllib.request.Request(
        BASE + path, data=data,
        headers={"Content-Type": "application/json"} if data else {})
    with OP.open(req, timeout=10) as r:
        return json.loads(r.read().decode("utf-8"))


def mode_key(d):
    p = d.get("perf") or {}
    return p.get("mode"), p.get("apmd")


MODE_TO_KEY = {1: "office", 2: "game", 3: "beast"}
KEY_TO_MODE = {v: k for k, v in MODE_TO_KEY.items()}

st = call("/api/status")
before_key, before_apmd = mode_key(st)
print("起始档位：%s (APMD=%s)   ec_writes=%s" % (before_key, before_apmd, st.get("ec_writes")))
if not st.get("ec_writes"):
    print("EC 写入开关是关的 —— 先打开再测（界面顶栏总闸）")
    raise SystemExit(1)

target = 1 if before_key != "office" else 2          # 切到另一个档
print("目标档位：%s (mode=%d)" % (MODE_TO_KEY[target], target))

# 先建基线，看清采样线程的刷新节奏（不写，只观察 status 变化时刻）
print("\n--- 基线：连续读 3 秒，看 status 快照切换的间隔 ---")
seen = []
t0 = time.perf_counter()
for _ in range(30):
    k, _a = mode_key(call("/api/status"))
    seen.append((round(time.perf_counter() - t0, 3), k))
    time.sleep(0.1)
# 快照"变化"其实是采样线程每次重建 cache；这里打印相邻 perf 值不变的最后时刻没意义，
# 改为打印 perf 值序列的压缩结果
comp = []
for t, k in seen:
    if not comp or comp[-1][1] != k:
        comp.append((t, k))
print("  3 秒内 perf.mode 序列：", comp)

print("\n--- 正题：下发切换，然后每 100ms 轮询 /api/status ---")
r_arm = call("/api/ec/arm", {"cmd": 0x0300})
print("  arm:", r_arm)
t_write = time.perf_counter()
r = call("/api/ec/perf", {"mode": target})
t_done = time.perf_counter()
print("  写入返回：%s   耗时 %.0f ms" % (r, (t_done - t_write) * 1000))

hit_at = None
for _ in range(60):                                   # 最多看 6 秒
    k, _a = mode_key(call("/api/status"))
    if k == MODE_TO_KEY[target]:
        hit_at = time.perf_counter()
        break
    time.sleep(0.1)

if hit_at is None:
    print("  ⚠️ 6 秒内 status 一直是旧档位 —— 写入没生效？")
else:
    print("  写入完成 → status 跟上，用了 %.2f 秒" % (hit_at - t_done))
    print("  ⇒ 这段时间里界面按 status 渲染，就会把按钮弹回旧档（= 乱跳）")

print("\n还原档位：%s" % before_key)
if before_key in KEY_TO_MODE:
    call("/api/ec/arm", {"cmd": 0x0300})
    print("  还原结果：", call("/api/ec/perf", {"mode": KEY_TO_MODE[before_key]}))
