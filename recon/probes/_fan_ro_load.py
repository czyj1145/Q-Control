# -*- coding: utf-8 -*-
r"""只读负载测试：判断 EC 当前是自动调速还是卡在手动值。

不写任何 EC 寄存器（当前后端已挂、shell 非管理员，也写不了）。
用独立子进程做负载（绝不在本进程 multiprocessing——上次没加 __main__ 守卫
导致子进程重跑整个脚本，把后端 WMI 写爆了）。

判据：加负载后转速大幅攀升（→4000+）⇒ 自动；转速不动 ⇒ 卡手动值。
"""
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))
from hal import create_hal  # noqa: E402

BURNERS = 8


def burner():
    code = "import time\nx=0\nwhile True: x=(x*1103515245+12345)&0x7FFFFFFF"
    return subprocess.Popen([sys.executable, "-c", code],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def sample(ec, n, gap, tag):
    for i in range(n):
        st = ec.read_status()
        print("  [%s] t+%03ds CPU=%s°C GPU=%s°C FAN1=%s FAN2=%s"
              % (tag, (i + 1) * gap, st["cpu_temp"], st["gpu_temp"],
                 st["fan1_rpm"], st["fan2_rpm"]))
        time.sleep(gap)


if __name__ == "__main__":
    ec = create_hal(mock=False)
    print("=== 空载 15 秒 ===")
    sample(ec, 5, 3, "idle")

    print("\n=== 加负载（%d 进程）50 秒 ===" % BURNERS)
    ps = [burner() for _ in range(BURNERS)]
    try:
        sample(ec, 17, 3, "load")
    finally:
        for p in ps:
            p.kill()

    print("\n=== 卸载 30 秒 ===")
    sample(ec, 10, 3, "cool")
    print("\n完成（全程只读）")
