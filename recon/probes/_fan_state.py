# -*- coding: utf-8 -*-
"""只读观测风扇当前是不是被卡在手动值上。

判据：温度在动、转速一动不动 ⇒ EC 停在我们写死的手动值（固件不再自动调速）。
只读：只发 0x0200（状态读），不下发任何写命令。

用法：python _fan_state.py [采样次数] [间隔秒]
"""
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))
from hal import create_hal, is_admin   # noqa: E402

n = int(sys.argv[1]) if len(sys.argv) > 1 else 6
gap = float(sys.argv[2]) if len(sys.argv) > 2 else 3.0

print("管理员权限：", is_admin())
ec = create_hal(mock=False)
print("HAL:", type(ec).__name__, " is_mock:", getattr(ec, "is_mock", None))

rows = []
for i in range(n):
    st = ec.read_status()
    rows.append(st)
    print("  #%d  CPU=%s°C  GPU=%s°C  风扇1=%s RPM  风扇2=%s RPM"
          % (i + 1, st["cpu_temp"], st["gpu_temp"],
             st["fan1_rpm"], st["fan2_rpm"]))
    if i + 1 < n:
        time.sleep(gap)

t = [r["cpu_temp"] for r in rows]
f1 = [r["fan1_rpm"] for r in rows]
f2 = [r["fan2_rpm"] for r in rows]
print("\n温度波动 %d°C，风扇1 波动 %d RPM，风扇2 波动 %d RPM"
      % (max(t) - min(t), max(f1) - min(f1), max(f2) - min(f2)))
if max(t) - min(t) >= 1 and max(f1) - min(f1) <= 60 and max(f2) - min(f2) <= 60:
    print("⇒ 温度在动、转速不动：**像是卡在手动值**（EC 没在自动调速）")
elif max(f1) - min(f1) <= 60:
    print("⇒ 温度没怎么动，转速也不动：无法判定（负载太平稳）")
else:
    print("⇒ 转速随温度在动：EC 处于自动调速")
