# -*- coding: utf-8 -*-
"""抓 FN 按键时的后端记录（诊断用，不碰 EC）。

为什么这么做：FN+3 改了 EC 里的 PERM，而 PERM 不在任何可读命令里。
但「事件到达时到底读到了什么」全都被后端记进了 /api/osd 的 recent
（含深字段 0x0202/0x0207，只在事件时读）。

这个脚本只轮询后端接口（纯 HTTP），**不去读 EC** —— 避免和正在跑的
控制中心抢 WMI buffer（PROTOCOL §五：两个进程同时发命令会读到残留）。
"""
import json
import sys
import time
import urllib.request

API = "http://127.0.0.1:8765"
op = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def get(path):
    return json.loads(op.open(API + path, timeout=3).read().decode())


def main(seconds=75):
    sys.stdout.reconfigure(encoding="utf-8")
    try:
        st0 = get("/api/osd")
    except Exception as e:
        print("连不上后端：%r" % e)
        return
    seen = len(st0.get("recent") or [])
    hk0 = st0["hotkey"]["count"]
    print("基线：热键事件=%d，recent=%d 条" % (hk0, seen))
    print("--- 开始监控 %d 秒，请按 FN+3 ---" % seconds)

    t0 = time.time()
    last_hk = hk0
    last_osd = None
    while time.time() - t0 < seconds:
        time.sleep(0.4)
        try:
            st = get("/api/osd")
        except Exception:
            continue
        hk = st["hotkey"]["count"]
        recent = st.get("recent") or []
        if hk != last_hk:
            print("[%4.1fs] ✔ 收到 FN 事件（累计 %d）" % (time.time() - t0, hk))
            last_hk = hk
        # 只打印新出现的条目
        if len(recent) > seen:
            for item in recent[seen:]:
                print("        %s  src=%-6s changed=%s%s"
                      % (time.strftime("%H:%M:%S"), item.get("src"),
                         item.get("changed"),
                         ("  " + item["note"]) if item.get("note") else ""))
            seen = len(recent)
        elif len(recent) < seen:
            seen = len(recent)          # 环形缓冲被覆盖，重置游标
        # 顺带记一下 OSD 状态变化
        key = (st["enabled"], st["error"])
        if last_osd is not None and key != last_osd:
            print("        OSD 状态变化：enabled=%s error=%s" % key)
        last_osd = key

    print("--- 监控结束 ---")
    st = get("/api/osd")
    print("热键事件总数 =", st["hotkey"]["count"], "| 原厂 =", st["vendor"] or "无")
    for item in (st.get("recent") or [])[-10:]:
        print("   %-6s %s %s" % (item.get("src"), item.get("changed"),
                                 item.get("note") or ""))


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 75)
