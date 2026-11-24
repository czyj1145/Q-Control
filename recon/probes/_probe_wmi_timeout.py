# -*- coding: utf-8 -*-
"""一次性探针：确认 NextEvent 超时到底是哪种异常 / 什么错误码。

背景：热键监听的超时判据是 `e.hresult == 0x00043005`。
现场 /api/osd 里 hotkey/cbwmi 的 last_error 全是『已超时』的 com_error，
说明这个判据没命中 —— 超时被当成真错误处理（sleep + 重订阅）。
本探针把真实码打出来，用于修正判据。用完即删。
"""
import pythoncom
import win32com.client

CLASSES = ["GMC_WMIEvent", "WMI2Event"]


def probe(cls):
    print("=" * 60)
    print("class =", cls)
    pythoncom.CoInitialize()
    try:
        svc = win32com.client.GetObject(
            "winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\WMI")
        w = svc.ExecNotificationQuery("SELECT * FROM %s" % cls)
        got = 0
        for i in range(3):                       # 连看 3 次超时，确认稳定
            try:
                ev = w.NextEvent(400)
                if ev is not None:
                    got += 1
                    print("  [%d] 收到事件（用户刚好在按键）" % i)
                    continue
            except Exception as e:               # noqa
                hr = getattr(e, "hresult", None)
                ex = getattr(e, "excepinfo", None)
                print("  [%d] %s" % (i, type(e).__name__))
                print("      hresult   = %s" % (
                    "0x%08X" % (hr & 0xFFFFFFFF) if isinstance(hr, int) else hr))
                print("      excepinfo = %r" % (ex,))
                if isinstance(ex, tuple) and len(ex) >= 6:
                    sc = ex[5]
                    print("      scode     = %s" % (
                        "0x%08X" % (sc & 0xFFFFFFFF) if isinstance(sc, int) else sc))
                print("      args      = %r" % (e.args,))
    finally:
        pythoncom.CoUninitialize()


if __name__ == "__main__":
    for c in CLASSES:
        probe(c)
