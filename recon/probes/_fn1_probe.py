# -*- coding: utf-8 -*-
r"""Fn+1（飓风模式）探针：同时抓 EC 转速采样 + CBWMI WMI 事件。

目的：确认 Fn+1 按键到底有没有到 ACPI、EC 有没有切风扇模式。

原理（2026-10-13 DSDT 逆向结论）：
  EC 的 _Q60 里 `If (LEqual(ECRD(CBSC), 0x11)) { Store(FANM, WMIA.FMAX);
  Sleep(50); Notify(WMIA, 0x80) }` —— 固件风扇模式变化 → 事件 0x11 →
  镜像进 WMIA 缓冲区 → 抛 WMI 通知。
  WMIA(CBWMI) 的 _WDG 第一个 GUID = ABBC0F20-8EA1-11d1-00A0-C90629100000，
  对应 root\WMI 的 **WMI2Event** 类（EventDetail 属性即 BUFF，FMAX 在偏移 0x1E）。

本脚本纯只读：不写 EC、不碰事件标志。
用法：python _fn1_probe.py [秒数] [日志文件]
"""
import sys
import threading
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "ControlCenter"))

SECS = int(sys.argv[1]) if len(sys.argv) > 1 else 180
LOG = sys.argv[2] if len(sys.argv) > 2 else "_fn1_probe.log"

try:
    import pythoncom
    import win32com.client
    _COM = True
except Exception:                                     # noqa
    _COM = False

_stop = threading.Event()
_lock = threading.Lock()


def out(f, line):
    with _lock:
        f.write(line + "\n")
        f.flush()
        print(line, flush=True)


def sampler(f):
    """独立读 EC（不需要管理员、不依赖后端）"""
    try:
        from hal import create_hal
        ec = create_hal(mock=False)
    except Exception as e:                             # noqa
        out(f, "[sampler] HAL 初始化失败 %r" % e)
        return
    while not _stop.is_set():
        try:
            st = ec.read_status()
            line = "%s CPU=%s GPU=%s FAN1=%s FAN2=%s" % (
                time.strftime("%H:%M:%S"), st["cpu_temp"], st["gpu_temp"],
                st["fan1_rpm"], st["fan2_rpm"])
        except Exception as e:                         # noqa
            line = "%s ERR %r" % (time.strftime("%H:%M:%S"), e)
        out(f, line)
        for _ in range(10):                # 1 秒拆 10 段，stop 能及时生效
            if _stop.is_set():
                return
            time.sleep(0.1)


def wmi_watch(f, cls, tag):
    """订阅一个 WMI 事件类，打印事件详情"""
    if not _COM:
        out(f, "[%s] 无 win32com，跳过" % tag)
        return
    pythoncom.CoInitialize()
    try:
        while not _stop.is_set():
            try:
                svc = win32com.client.GetObject(
                    "winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\WMI")
                watcher = svc.ExecNotificationQuery(
                    "SELECT * FROM %s" % cls)
                out(f, "[%s] 已订阅 %s" % (tag, cls))
            except Exception as e:                     # noqa
                out(f, "[%s] 订阅失败 %r，3 秒后重试" % (tag, e))
                time.sleep(3)
                continue
            while not _stop.is_set():
                try:
                    ev = watcher.NextEvent(500)
                except Exception:                      # noqa
                    continue
                if ev is None:
                    continue
                ts = time.strftime("%H:%M:%S")
                detail = None
                try:
                    d = ev.Properties_("EventDetail")
                    detail = bytes(d.Value) if d.Value else b""
                except Exception:                      # noqa
                    pass
                if detail:
                    hexs = " ".join("%02X" % b for b in detail[:40])
                    out(f, "%s [%s] <<< %s 事件 EventDetail=%s"
                        % (ts, tag, cls, hexs))
                    # FMAX 在 BUFF 偏移 0x1E
                    if len(detail) > 0x1E:
                        out(f, "%s [%s]     FMAX(0x1E)=%d EVID(0x00)=%d"
                            % (ts, tag, detail[0x1E], detail[0]))
                else:
                    out(f, "%s [%s] <<< %s 事件（无 EventDetail）"
                        % (ts, tag, cls))
    finally:
        pythoncom.CoUninitialize()


if __name__ == "__main__":
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("# start %s  secs=%d\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), SECS))
        f.flush()
        threads = [
            threading.Thread(target=sampler, args=(f,), daemon=True),
            threading.Thread(target=wmi_watch, args=(f, "WMI2Event", "cbwmi"),
                             daemon=True),
            threading.Thread(target=wmi_watch, args=(f, "GMC_WMIEvent", "gwmi"),
                             daemon=True),
        ]
        for t in threads:
            t.start()
        t0 = time.time()
        while time.time() - t0 < SECS:
            time.sleep(0.5)
        _stop.set()
        for t in threads:
            t.join(timeout=3)
        out(f, "# done %s" % time.strftime("%Y-%m-%d %H:%M:%S"))
    print("done ->", LOG)
