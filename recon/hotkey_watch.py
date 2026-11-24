# -*- coding: utf-8 -*-
"""
监听广达热键事件 GMC_WMIEvent（Fn 组合键 / 模式切换 / 飞行模式等）

按原厂热键时 EC 会触发 ACPI Notify，BIOS 抛出 WMI 事件。
本脚本把它们打出来，用来标定「哪个 Fn 组合 → 哪个 EventDetail」。
纯监听，不写 EC。

⚠️ 2026-10-11：界面上的「热键事件」行已下线，控制中心不再启动监听线程，
   故从 ControlCenter/ 移到 recon/ —— 它是**逆向工具**，不是应用代码。
   独立用法：python hotkey_watch.py   （Ctrl+C 退出）
"""
import threading
import time

import pythoncom
import win32com.client

EVENT_CLASS = "GMC_WMIEvent"


class HotKeyWatcher:
    def __init__(self):
        self.events = []
        self.running = False
        self._stop = threading.Event()
        self._thread = None

    def start(self):
        if self.running:
            return True
        self.running = True
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, daemon=True)
        self._thread.start()
        return True

    def stop(self):
        self.running = False
        self._stop.set()

    def _loop(self):
        pythoncom.CoInitialize()
        while not self._stop.is_set():
            try:
                svc = win32com.client.GetObject(
                    "winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\WMI")
                watcher = svc.ExecNotificationQuery(
                    f"SELECT * FROM {EVENT_CLASS}")
                while not self._stop.is_set():
                    try:
                        ev = watcher.NextEvent(1000)   # 1s 超时，可响应停止
                    except Exception:
                        # 无事件时的超时属正常，不算错误
                        continue
                    if ev is None:
                        continue
                    # dump 事件对象的全部属性，找出真正携带数据的那个
                    props = {}
                    detail = []
                    try:
                        for p in ev.Properties_:
                            try:
                                v = p.Value
                            except Exception:
                                continue
                            if isinstance(v, tuple):
                                v = list(v)
                            if isinstance(v, list) and v and isinstance(v[0], int):
                                props[p.Name] = " ".join(f"{b:02X}" for b in v[:32])
                                if p.Name == "EventDetail":
                                    detail = v
                            else:
                                props[p.Name] = v
                    except Exception:
                        pass
                    self.events.append({
                        "ts": time.time(),
                        "detail": detail,
                        "hex": " ".join(f"{b:02X}" for b in detail[:16]),
                        "props": props,
                    })
                    self.events = self.events[-50:]
                    print("[热键事件]", props)
            except Exception as e:  # noqa
                self.events.append({"ts": time.time(), "error": str(e)})
                time.sleep(2.0)

    def recent(self, n=10):
        return self.events[-n:]


if __name__ == "__main__":
    import json
    import sys

    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    w = HotKeyWatcher()
    w.start()
    print("监听 GMC_WMIEvent 中…… 按原厂 Fn 组合键试试（Ctrl+C 退出）", flush=True)
    try:
        while True:
            import time
            time.sleep(1)
            while w.events:
                print(json.dumps(w.events.pop(0), ensure_ascii=False), flush=True)
    except KeyboardInterrupt:
        w.stop()
        print("已退出")
