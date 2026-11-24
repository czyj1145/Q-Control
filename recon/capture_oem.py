# -*- coding: utf-8 -*-
r"""
抓取「原厂控制中心」真实下发的 EC 报文。

原理：原厂 ControlCenter.exe 也用同一个 root\wmi:RW_GMWMI 接口
（exe 里有 WMIClassRW_GMWMI / ReadGMWMI / WriteGMWMI / WMISetLEDn / SetPowerMode 字符串）。
RW_GMWMI 的 BufferBytes 会保留**最后一次写入的 32 字节报文**，
所以只要高频采样（50ms），就能在两次轮询之间抓原厂的写入。

记录规则：每个「新出现的报文」（相对上一次采样发生变化）都记一行，
所以同一个值被点两次也能分别看到。我们自己的 0xFA00/0x0200 轮询会被过滤。

用法：python capture_oem.py [持续秒数]
输出：recon/oem_capture.log
"""
import sys
import time
import datetime

sys.path.insert(0, ".")

from gmwmi import GMWMI  # noqa: E402

DURATION = int(sys.argv[1]) if len(sys.argv) > 1 else 300
LOG = "oem_capture.log"

g = GMWMI()
prev = None
t0 = time.time()
ours = 0
n = 0

with open(LOG, "w", encoding="utf-8", buffering=1) as f:
    f.write("# 开始抓包 %s  持续 %d 秒\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), DURATION))
    while time.time() - t0 < DURATION:
        try:
            b = g.read_buffer()
        except Exception as e:  # noqa
            f.write("ERR %s\n" % e)
            time.sleep(0.5)
            continue
        if len(b) != 32:
            time.sleep(0.05)
            continue
        cur = bytes(b)
        if cur == prev:
            time.sleep(0.004)
            continue
        prev = cur
        gwf0 = b[0] | (b[1] << 8)
        gwf1 = b[2] | (b[3] << 8)
        # 过滤「我们自己的轮询」：状态读 0xFA00/0x0200、APMD 读 0xFA00/0x0300
        if gwf0 == 0xFA00 and gwf1 in (0x0200, 0x0300):
            ours += 1
            time.sleep(0.004)
            continue
        n += 1
        d2 = int.from_bytes(bytes(b[4:8]), "little")
        d4 = int.from_bytes(bytes(b[12:16]), "little")
        d5 = int.from_bytes(bytes(b[16:20]), "little")
        d6 = int.from_bytes(bytes(b[20:24]), "little")
        extra = ""
        if gwf1 == 0x0100:
            extra = ("  → 地址=%d 颜色 B=%02X G=%02X R=%02X DT3D=0x%02X(灯效=%d,档=%d)"
                     % (b[4], b[8], b[9], b[10], b[11], b[11] >> 4, b[11] & 0xF))
        f.write("#%02d [%s] GWF0=0x%04X GWF1=0x%04X\n"
                "      raw      = %s\n"
                "      GWD0/DA2 = %-6d  DT3A..D(8..11) = %s  DAT4=%d DAT5=%d DAT6=%d%s\n"
                % (n, datetime.datetime.now().strftime("%H:%M:%S.%f")[:-3], gwf0, gwf1,
                   " ".join("%02X" % x for x in b), d2,
                   " ".join("%02X" % x for x in b[8:12]), d4, d5, d6, extra))
        time.sleep(0.004)
    f.write("# 结束。忽略自有轮询 %d 次；记录 %d 条报文\n" % (ours, n))

print("done. logged %d payload events -> %s" % (n, LOG))
