# -*- coding: utf-8 -*-
"""
热键嗅探器（全程只读，绝不写 EC）

⚠️ 归属：**独立逆向工具**，不属于运行时代码。
   · 2026-10-11 已从控制中心界面 / HTTP 服务摘除（协议标定完成，不再需要实时嗅探）；
   · 2026-10-12 P0 清理时由 `ControlCenter/sniff.py` 迁到 `recon/`，
     使 `ControlCenter/` 保持"只放可发布运行时代码"。

原理：持续轮询所有"读"命令能拿到的寄存器值，你按下原厂 Fn 热键，
      它把按键前后发生变化的字段记录下来 —— 哪个变，就对应哪个功能。

用法：命令行 `python sniff.py`（终端里直接看变化）。
      需要 EC 访问权限 ⇒ 请在**管理员**终端里跑。
"""
import os
import sys
import time

# recon/ 与 ControlCenter/ 平级；hal / model 在 ControlCenter 里（model.json 由
# 配置层用自身 __file__ 定位，故此处只需修 sys.path，不必 chdir）。
_HERE = os.path.dirname(os.path.abspath(__file__))
_CC = os.path.join(os.path.dirname(_HERE), "ControlCenter")
sys.path.insert(0, _CC)

from hal import WmiHAL as ECController  # noqa: E402  直接从 hal 导入
from model import READ_ONLY_CMDS  # noqa: E402  唯一真源：只读命令白名单

# ⚠️ 只跑"确认无写副作用"的命令，来源 READ_ONLY_CMDS（与 HAL 守卫共用同一张表）。
#   0x0300 —— 会执行 ECWT(0x00 → APMD)，等于把性能模式设成安静档！
#   0x0206 —— 会执行 ECWT(→ OMCC)
#   0x0204 / 0x0205 / 0x0100 —— 本身就是设置命令
# 0x0200 内含 ECWT(0x01 → OMCC)，属于原厂读取流程的一部分（通知 EC 刷新数据），
# 是读取动作必需的伴随操作，保留。
READ_CMDS = [
    (c, {
        0x0200: "状态(温度/风扇/背光)",
        0x0202: "ENTP/AWMI 性能状态",
        0x0203: "GFXT 显卡模式",
        0x0207: "VGAT/RPM5",
        0x0400: "DAT3",
    }.get(c, "未知只读命令"))
    for c in sorted(READ_ONLY_CMDS)
]

FIELDS = [
    ("DAT2", 4, 4), ("DAT3", 8, 4), ("DT3A", 8, 1), ("DT3B", 9, 1),
    ("DT3C", 10, 1), ("DT3D", 11, 1), ("DAT4", 12, 4), ("DAT5", 16, 4),
    ("DAT6", 20, 4),
]


def read_all(ec):
    """把所有只读命令跑一遍，返回 {命令: {字段: 值}}"""
    out = {}
    for cmd, _ in READ_CMDS:
        try:
            r = ec._raw(cmd)
        except Exception as e:  # noqa
            out[cmd] = {"__err__": str(e)[:60]}
            continue
        vals = {}
        for name, off, size in FIELDS:
            if size == 4:
                vals[name] = int.from_bytes(bytes(r[off:off + 4]), "little")
            else:
                vals[name] = r[off]
        out[cmd] = vals
    return out


def flat(d):
    rows = {}
    for cmd, vals in d.items():
        if "__err__" in vals:
            rows[f"0x{cmd:04X}"] = vals["__err__"]
            continue
        for k, v in vals.items():
            rows[f"0x{cmd:04X}·{k}"] = v
    return rows


def main():
    ec = ECController()
    ok, err = ec.available()
    if not ok:
        print("无法访问 EC:", err)
        return
    print("=" * 62)
    print("热键嗅探器（只读模式，不会写入任何寄存器）")
    print("=" * 62)
    base = flat(read_all(ec))
    print("\n基线已记录，请按你的 Fn 热键（按完等 2~3 秒看输出）\n")
    for k in sorted(base):
        print(f"   {k:<18} {base[k]}")
    print("\n" + "-" * 62)
    try:
        while True:
            time.sleep(0.8)
            cur = flat(read_all(ec))
            diffs = [(k, base.get(k), cur.get(k))
                     for k in sorted(set(list(base) + list(cur)))
                     if base.get(k) != cur.get(k)]
            if diffs:
                print(f"\n[{time.strftime('%H:%M:%S')}] 检测到变化：")
                for k, b, c in diffs:
                    print(f"    {k:<18} {b}  ->  {c}")
                print("-" * 62)
            base = cur
    except KeyboardInterrupt:
        print("\n结束。")


if __name__ == "__main__":
    main()
