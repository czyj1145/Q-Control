# -*- coding: utf-8 -*-
"""
E3（Energy Estimation Engine）暴力探测。

powrprof!CallNtPowerInformation 的 EnergyEstimationEngine 需要 InputBuffer
（说明要查什么），传 NULL 会得到 STATUS_INVALID_PARAMETER(0xC000000D) —— 上一轮就是这么失败的。
这里把常见的 level × input 结构组合都试一遍，看哪个返回 STATUS_SUCCESS(0)。

若成功，输出里会看到长度 > 0 的 buffer，再解析成组件能耗（mJ）。
"""
import ctypes
import json
import sys
import time
from ctypes import wintypes

powrprof = ctypes.WinDLL("powrprof.dll")
powrprof.CallNtPowerInformation.restype = ctypes.c_long
powrprof.CallNtPowerInformation.argtypes = [
    ctypes.c_int, ctypes.c_void_p, wintypes.ULONG,
    ctypes.c_void_p, wintypes.ULONG]

OUT_SIZE = 32768
STATUS_OK = 0


def call(level, inbuf, inlen):
    out = ctypes.create_string_buffer(OUT_SIZE)
    st = powrprof.CallNtPowerInformation(
        level, inbuf, inlen, ctypes.cast(out, ctypes.c_void_p), OUT_SIZE)
    return st, out.raw


def candidates():
    """(level, input 字节串) 的所有待试组合"""
    levels = list(range(43, 56))
    inputs = []
    # 不同宽度的全零 / 递增字段
    for n in (4, 8, 12, 16, 24, 32):
        inputs.append(("zero%d" % n, b"\x00" * n))
    for t in range(0, 6):
        for n in (8, 16, 32):
            b = bytearray(n)
            b[0:4] = t.to_bytes(4, "little")          # 第一个字段 = 查询类型
            b[4:8] = (0xFFFFFFFF).to_bytes(4, "little")  # 第二个字段 = 全部组件
            inputs.append(("t%d_%d" % (t, n), bytes(b)))
    return levels, inputs


def main():
    levels, inputs = candidates()
    hits = []
    tried = 0
    for lvl in levels:
        for name, buf in inputs:
            try:
                inb = ctypes.create_string_buffer(buf, len(buf))
                st, out = call(lvl, ctypes.cast(inb, ctypes.c_void_p), len(buf))
            except Exception:  # noqa
                continue
            tried += 1
            if st == STATUS_OK:
                nz = sum(1 for x in out[:4096] if x)
                hits.append({"level": lvl, "input": name, "inlen": len(buf),
                             "nonzero": nz, "head": out[:64].hex()})
        # 一个 level 只要有命中就不再穷举它
    res = {"tried": tried, "hits": hits}

    # 对命中的组合做两次差分，找累加器（能耗）
    for h in hits[:4]:
        lvl = h["level"]
        buf = next(b for n, b in inputs if n == h["input"])
        inb = ctypes.create_string_buffer(buf, len(buf))
        _, a = call(lvl, ctypes.cast(inb, ctypes.c_void_p), len(buf))
        time.sleep(2.0)
        _, b = call(lvl, ctypes.cast(inb, ctypes.c_void_p), len(buf))
        grow = {}
        for i in range(0, min(len(a), len(b)) - 8, 8):
            va = int.from_bytes(a[i:i + 8], "little")
            vb = int.from_bytes(b[i:i + 8], "little")
            if 0 < vb - va < 10 ** 12:
                grow[str(i)] = [va, vb, vb - va]
        h["grow"] = grow

    out = sys.argv[1] if len(sys.argv) > 1 else "probe_e3.json"
    with open(out, "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    print("tried=%d hits=%d -> %s" % (tried, len(hits), out))


if __name__ == "__main__":
    main()
