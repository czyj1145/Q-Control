# -*- coding: utf-8 -*-
"""诊断屏幕亮度：直连 root\\WMI 尝试写入，找出「无效的参数」的真正原因。"""
import win32com.client
import pythoncom

pythoncom.CoInitialize()
NS = r"winmgmts:{impersonationLevel=impersonate}!\\.\root\WMI"
print("namespace:", NS)
svc = win32com.client.GetObject(NS)

ms = svc.InstancesOf("WmiMonitorBrightnessMethods")
print("methods found:", len(list(ms)) if hasattr(ms, "__len__") else "?")
for m in svc.InstancesOf("WmiMonitorBrightnessMethods"):
    print("method instance:", m.InstanceName)
    # 签名: WmiSetBrightness(Timeout, Brightness)
    for args in [(0, 60), (60, 0), (0, 30)]:
        try:
            r = m.WmiSetBrightness(*args)
            print("   WmiSetBrightness%s -> OK ret=%s" % (args, r))
        except Exception as e:  # noqa
            print("   WmiSetBrightness%s -> FAIL %s" % (args, e))
    # 也试关键字调用
    try:
        r = m.WmiSetBrightness(Timeout=0, Brightness=70)
        print("   kwargs(Timeout=0,Brightness=70) -> OK ret=%s" % (r,))
    except Exception as e:  # noqa
        print("   kwargs -> FAIL %s" % (e,))

for b in svc.InstancesOf("WmiMonitorBrightness"):
    print("CurrentBrightness =", b.CurrentBrightness,
          " Levels count =", len(b.Levels or []))
