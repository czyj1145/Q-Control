# -*- coding: utf-8 -*-
"""NVMe 读写负载：把 PCH（南桥）加热，用于验证 EC 里有没有跟随 PCH 的字节。

PCH 直连 NVMe，持续 PCIe 流量会让 PCH die 明显升温（不像 CPU 负载那样主要加热 CPU）。
温和设计：每轮读写 256MB + sleep 1 秒，总时长由参数控制，临时文件用完即删。
只写系统临时目录，文件名带前缀便于识别。
"""
import io
import os
import sys
import tempfile
import time

DUR = float(sys.argv[1]) if len(sys.argv) > 1 else 200.0
SIZE = 256 * 1024 * 1024          # 每轮 256MB
CHUNK = 4 * 1024 * 1024

path = os.path.join(tempfile.gettempdir(), "_pch_heat.tmp")
blk = os.urandom(CHUNK)
n = SIZE // CHUNK
t0 = time.time()
rounds = 0
written = 0
try:
    while time.time() - t0 < DUR:
        with open(path, "wb", buffering=0) as f:
            for _ in range(n):
                f.write(blk)
        written += SIZE
        with open(path, "rb", buffering=0) as f:
            while f.read(CHUNK):
                pass
        written += SIZE
        rounds += 1
        time.sleep(1)
finally:
    try:
        os.remove(path)
    except Exception:                                        # noqa
        pass
print("磁盘负载结束：%d 轮，共读写 %.1f GB，耗时 %.0fs"
      % (rounds, written / 1e9, time.time() - t0))
