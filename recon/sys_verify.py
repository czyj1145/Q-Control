# -*- coding: utf-8 -*-
"""
SYS 读数可信度验证 —— 同拍对比 `0x0200` 与 `0x0207`，判断第三路到底是不是真的。

为什么要这个脚本：`0x0207` 的 a2 在原厂 IL 里叫 `SysTemperature`，但那是**推断**。
2026-11-18 在本机（两风扇 NL5）实测，a2 恒等于 GPU 温度 —— 说明这条命令在某些机型上
只是把 GPU 温度回填进 a2。三风扇机型上它是不是真的换成 SYS 温度，只能靠观测。

用法（Q-Control 正在跑，127.0.0.1:8765）：
    python sys_verify.py            # 默认采 40 拍，每拍 500 ms
    python sys_verify.py 60 0.3     # 60 拍，间隔 0.3 秒

建议：采集中途让机器负载变化一次（跑个编译 / 游戏），这样才有"参照温度变化"可比。

判据：
  · a2 逐点等于 GPU 温度（且 GPU 变过 ≥2 ℃）⇒ 副本，不可信
  · a2 逐点等于 CPU 温度（且 CPU 变过 ≥2 ℃）⇒ 副本，不可信
  · a2 自己独立波动 ≥2 ℃ ⇒ 真的 SYS 温度
  · 三方都静止 ⇒ 证据不足，继续测（加负载）
"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8765"


def ec_raw(sub, timeout=6.0):
    body = json.dumps({"sub": sub}).encode()
    req = urllib.request.Request(
        BASE + "/api/ec/raw", data=body,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8"))


def u32(resp, off):
    """从 32 字节帧里按小端取一个 DWORD（与 EcHal.ValueOf 的 4 字节分支一致）。"""
    raw = resp.get("resp") or ""
    b = [int(x, 16) for x in raw.split()]
    if len(b) < off + 4:
        return None
    return b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)


def follows(sys_vals, ref_vals, min_delta=2.0, tol=0.5):
    """参照变化过 min_delta 以上，且 sys 逐点与之相等 ⇒ 认为是同一路读数的副本。"""
    if not ref_vals or not sys_vals:
        return False
    if max(ref_vals) - min(ref_vals) < min_delta:
        return False
    if len(sys_vals) != len(ref_vals):
        return False
    return all(abs(a - b) <= tol for a, b in zip(sys_vals, ref_vals))


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 40
    gap = float(sys.argv[2]) if len(sys.argv) > 2 else 0.5

    rows = []
    print("idx  cpuT gpuT  fan1  fan2 | a2  a3   a4  | a2-vs-gpu a2-vs-cpu")
    for i in range(n):
        try:
            s = ec_raw(512)
            g = ec_raw(519)
        except Exception as e:
            print(f"读取失败：{e}")
            break
        cpu_t = u32(s, 4)
        gpu_t = u32(s, 8)
        fan1 = u32(s, 12)
        fan2 = u32(s, 16)
        a2 = u32(g, 4)
        a3 = u32(g, 8)
        a4 = u32(g, 12)
        rows.append({
            "cpu": cpu_t, "gpu": gpu_t, "fan1": fan1, "fan2": fan2,
            "a2": a2, "a3": a3, "a4": a4,
        })
        dg = "" if (a2 is None or gpu_t is None) else f"{a2 - gpu_t:+d}"
        dc = "" if (a2 is None or cpu_t is None) else f"{a2 - cpu_t:+d}"
        print(f"{i:3}  {cpu_t!s:>4} {gpu_t!s:>4} {fan1!s:>5} {fan2!s:>5} | "
              f"{a2!s:>3} {a3!s:>4} {a4!s:>4} | {dg:>9} {dc:>9}")
        time.sleep(gap)

    if len(rows) < 8:
        print("\n样本太少，没法判定。")
        return

    a2 = [r["a2"] for r in rows]
    gpu = [r["gpu"] for r in rows]
    cpu = [r["cpu"] for r in rows]

    print("\n================ 判定 ================")
    print(f"样本 {len(rows)} 拍；GPU 温度跨度 {max(gpu) - min(gpu)} ℃，"
          f"CPU 温度跨度 {max(cpu) - min(cpu)} ℃，a2 跨度 {max(a2) - min(a2)} ℃")

    if follows(a2, gpu):
        print("a2 = **GPU 温度的副本**（GPU 变过且 a2 逐点跟随）⇒ 不可信，不能当 SYS 温度。")
    elif follows(a2, cpu):
        print("a2 = **CPU 温度的副本**（CPU 变过且 a2 逐点跟随）⇒ 不可信，不能当 SYS 温度。")
    elif max(a2) - min(a2) >= 2:
        print("a2 自己独立波动 ≥2 ℃，且不跟随 CPU/GPU ⇒ **看起来是真的 SYS 温度**。")
    elif (len(rows) >= 10
          and all(abs(x - y) == 0 for x, y in zip(a2, gpu))
          and sum(abs(x - y) for x, y in zip(a2, cpu)) / len(a2) >= 3):
        # 没有"参照变化过"这条硬证据，但逐拍精确等于 GPU、又明显不等于 CPU，
        # 这种吻合很难是巧合。不下定论，但要明确指出"可疑"，别让人以为测过了没问题。
        print(f"**高度可疑**：a2 与 GPU 温度逐拍完全相等，与 CPU 平均差 "
              f"{sum(abs(x - y) for x, y in zip(a2, cpu)) / len(a2):.0f} ℃。"
              "\n  但采集期间 GPU 温度没变化，无法确认是跟随还是巧合。"
              "\n  ⇒ 跑负载让 GPU 温度变 2~3 ℃ 后再测一次即可定案。")
    else:
        print("三方都静止，证据不足。加负载让 GPU 温度明显变化后再跑一次。")

    a3 = [r["a3"] for r in rows]
    fan1 = [r["fan1"] for r in rows]
    fan2 = [r["fan2"] for r in rows]
    if max(a3) == 0:
        print("a3 恒为 0 ⇒ 本机要么没有 SYS 风扇，要么该槽位未被填充（两风扇机型的正常形态）。")
    elif follows(a3, fan1, min_delta=300, tol=1):
        print("a3 = **fan1 的副本** ⇒ 不是独立的 SYS 转速。")
    elif follows(a3, fan2, min_delta=300, tol=1):
        print("a3 = **fan2 的副本** ⇒ 不是独立的 SYS 转速。")
    elif 0 < max(a3) <= 8000:
        print(f"a3 在 0~8000 之间（{min(a3)}~{max(a3)}）⇒ 数值形态像转速；"
              "要确认得让 SYS 占空比单独变化（0%→100%）看它跟不跟。")
    else:
        print(f"a3 超出 0~8000（{min(a3)}~{max(a3)}）⇒ 不是转速，程序里也会丢掉这个值。")


if __name__ == "__main__":
    main()
