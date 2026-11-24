# -*- coding: utf-8 -*-
r"""FN+1（飓风模式 = EC 固件风扇 Max/Auto 切换）真机验证探针 v2。

与 v1（_fn1_probe.py）的区别：**完全不读 EC**。
v1 的 sampler 用 hal 直接发 WMI 读命令，和正在跑的控制中心后端并发会读到
残留（PROTOCOL §五）。后端此刻在跑（127.0.0.1:8765），所以转速改走 HTTP
轮询 /api/status（快照自带缓存，不增加 EC 通道压力）。

要真机确认的四件事（10-13 DSDT 静态结论的实证）：
  1. 按 FN+1 时 root\WMI 的 **WMI2Event**（CBWMI/WMIA 设备，_WDG 首 GUID
     ABBC0F20-8EA1-11d1-00A0-C90629100000）是否真的抛事件；
  2. 事件 EventDetail(BUFF) 的 [0]=EVID 是否 == 0x11（_Q60 风扇事件表：
     `If (LEqual(ECRD(CBSC), 0x11)) { Store(FANM, WMIA.FMAX); ... }`）；
  3. BUFF[0x1E]=FMAX（EC FANM 风扇模式寄存器镜像）按一次变什么、再按一次
     变回什么 —— 由此定 auto/max 的取值语义；
  4. 辅证：/api/status 的 fan RPM 是否跳 ~5000（飓风 = 最大）。

顺带订阅 GMC_WMIEvent（FN+2/3 那条）看 FN+1 是否也抛 —— 预期不抛
（_Q60 通知的是 WMIA 不是 GWMI）。

纯只读：不写 EC、不碰事件标志、不发任何 WMI 命令；多进程订阅 WMI 事件
互不抢占，不影响原厂 daemon 和后端。
用法：python _fn1_probe2.py [秒数] [日志文件]
"""
import json
import sys
import threading
import time
import urllib.request

SECS = int(sys.argv[1]) if len(sys.argv) > 1 else 180
LOG = sys.argv[2] if len(sys.argv) > 2 else "_fn1_probe2.log"
API = "http://127.0.0.1:8765"
_op = urllib.request.build_opener(urllib.request.ProxyHandler({}))

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


def wmi_watch(f, cls, tag):
    """订阅一个 WMI 事件类，打印 EventDetail 原文（纯监听，不写任何东西）。"""
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
                detail = b""
                try:
                    d = ev.Properties_("EventDetail")
                    detail = bytes(d.Value) if d.Value else b""
                except Exception:                      # noqa
                    pass
                if detail:
                    hexs = " ".join("%02X" % b for b in detail[:40])
                    extra = ""
                    if cls == "WMI2Event" and len(detail) > 0x1E:
                        extra = "  EVID(0x00)=0x%02X FMAX(0x1E)=%d" % (
                            detail[0], detail[0x1E])
                    out(f, "%s [%s] <<< %s EventDetail=%s%s"
                        % (ts, tag, cls, hexs, extra))
                else:
                    out(f, "%s [%s] <<< %s 事件（无 EventDetail）"
                        % (ts, tag, cls))
    finally:
        pythoncom.CoUninitialize()


def rpm_watch(f):
    """HTTP 轮询后端快照拿风扇转速 + 性能模式（不碰 EC 通道，只打变化）。

    perf 也盯：10-09 嗅探曾观察到 FN+1 让 0x0300·DAT2（=APMD）在 0/1 间跳变，
    若属实，后端每秒的采样循环本该能看见 —— 顺便验证这条老线索。
    """
    out(f, "[rpm] 开始轮询 %s/api/status（含 perf）" % API)
    last = None
    while not _stop.is_set():
        try:
            st = json.loads(_op.open(API + "/api/status", timeout=5)
                            .read().decode())
            ec = st.get("ec") or {}
            pf = st.get("perf") or {}
            key = (ec.get("fan1_rpm"), ec.get("fan2_rpm"),
                   ec.get("cpu_temp"), ec.get("gpu_temp"),
                   pf.get("mode"), pf.get("apmd"))
            if key != last:
                out(f, "%s [rpm] fan1=%s fan2=%s cpu=%s gpu=%s perf=%s/apmd=%s"
                    % (time.strftime("%H:%M:%S"), key[0], key[1],
                       key[2], key[3], key[4], key[5]))
                last = key
        except Exception as e:                         # noqa
            out(f, "%s [rpm] 轮询失败 %r" % (time.strftime("%H:%M:%S"), e))
        for _ in range(10):
            if _stop.is_set():
                return
            time.sleep(0.1)


if __name__ == "__main__":
    with open(LOG, "w", encoding="utf-8") as f:
        f.write("# FN+1 验证探针 v2 start %s secs=%d\n"
                % (time.strftime("%Y-%m-%d %H:%M:%S"), SECS))
        f.flush()
        threads = [
            threading.Thread(target=wmi_watch, args=(f, "WMI2Event", "cbwmi"),
                             daemon=True),
            threading.Thread(target=wmi_watch, args=(f, "GMC_WMIEvent", "gwmi"),
                             daemon=True),
            threading.Thread(target=rpm_watch, args=(f,), daemon=True),
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
