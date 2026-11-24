# -*- coding: utf-8 -*-
"""S0 归档：修好移入 recon/probes/ 的探针脚本的路径依赖（2026-10-14）。

背景：这些脚本原在 ControlCenter/ 下，S0 版本控制止血时整体移入本目录。
它们分三类路径依赖，逐一修：

  A. import ControlCenter 模块（hal/server/hwinfo/model…）—— sys.path 指回
     ControlCenter/（照 recon/sniff.py 的先例）；
  B. 读 ControlCenter/ 下的资源（tools/HWiNFO/、ui/、ec-writes.log）——
     资源路径同样指回 ControlCenter/；
  C. 只走 HTTP（打 127.0.0.1:8765）或纯 Windows API 的 —— 无需修。

⚠️ 本脚本只做精确字符串替换，每个替换都断言命中过一次，宁慢不乱。
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
CC = os.path.join(HERE, "..", "..", "ControlCenter")

# (文件, 旧串, 新串, 说明)
PATCHES = [
    ("_probe_hwi_all.py",
     'sys.path.insert(0, HERE)',
     'sys.path.insert(0, os.path.join(HERE, "..", "..", "ControlCenter"))',
     "A: import hwinfo"),
    ("_probe_hwinfo_tray.py",
     'sys.path.insert(0, HERE)',
     'sys.path.insert(0, os.path.join(HERE, "..", "..", "ControlCenter"))',
     "A: import hwinfo"),
    ("_probe_live.py",
     'sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))',
     'sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))',
     "A: import server/hwinfo"),
    ("_probe_hwinfo.py",
     'HWI_DIR = os.path.join(HERE, "..", "tools", "HWiNFO")',
     'HWI_DIR = os.path.join(HERE, "..", "..", "tools", "HWiNFO")',
     "B: tools/HWiNFO/ 在工作区根"),
    ("_fan_auto4.py",
     'WRITES_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ec-writes.log")',
     'WRITES_LOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter", "ec-writes.log")',
     "B: ec-writes.log 在 ControlCenter/"),
    ("_mock_serve.py",
     'UI_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ui")',
     'UI_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter", "ui")',
     "B: ui/ 在 ControlCenter/"),
    ("_fan_ro_load.py",
     'sys.path.insert(0, ".")',
     'sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))',
     "A: 顺带修掉依赖 CWD 的隐患"),
    ("_fan_state.py",
     'sys.path.insert(0, ".")',
     'sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))',
     "A: 同上"),
    ("_fan_watch.py",
     'sys.path.insert(0, ".")',
     'sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))',
     "A: 同上"),
    ("_fn1_probe.py",
     'sys.path.insert(0, ".")',
     'sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))',
     "A: 同上"),
]

fails = 0
for name, old, new, why in PATCHES:
    path = os.path.join(HERE, name)
    with open(path, encoding="utf-8") as f:
        text = f.read()
    n = text.count(old)
    if n != 1:
        print("FAIL %-24s 命中 %d 次（预期 1）：%s" % (name, n, why))
        fails += 1
        continue
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text.replace(old, new))
    print("ok   %-24s %s" % (name, why))

print("----")
print("失败 %d 处" % fails)
raise SystemExit(1 if fails else 0)
