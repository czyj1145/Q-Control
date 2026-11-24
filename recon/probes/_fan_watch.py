# -*- coding: utf-8 -*-
r"""只读采样器：记录温度/转速到日志，供"按 Fn+1 看 EC 反应"实验使用。

不写任何 EC 寄存器（当前 shell 非管理员、后端已挂，本来就写不了）。
用法：python _fan_watch.py [秒数] [日志文件]
"""
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))
from hal import create_hal  # noqa: E402

SECS = int(sys.argv[1]) if len(sys.argv) > 1 else 180
LOG = sys.argv[2] if len(sys.argv) > 2 else "_fan_watch.log"

if __name__ == "__main__":
    ec = create_hal(mock=False)
    t0 = time.time()
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("# start %s\n" % time.strftime("%Y-%m-%d %H:%M:%S"))
        f.flush()
        while time.time() - t0 < SECS:
            try:
                st = ec.read_status()
            except Exception as e:  # noqa
                f.write("ERR %s\n" % e)
                f.flush()
                time.sleep(1)
                continue
            line = "%s CPU=%s GPU=%s FAN1=%s FAN2=%s" % (
                time.strftime("%H:%M:%S"), st["cpu_temp"], st["gpu_temp"],
                st["fan1_rpm"], st["fan2_rpm"])
            f.write(line + "\n")
            f.flush()
            print(line)
            time.sleep(1)
    print("done ->", LOG)
