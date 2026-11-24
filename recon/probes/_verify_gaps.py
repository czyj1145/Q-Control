# -*- coding: utf-8 -*-
"""缺口修复验收 —— 这三条是 2026-10-12「风扇满转 + 恢复出厂没用」那起事故
直接暴露出来的（诊断见 2026-10-12 的排查记录）。

  ② `FanCurve.stop(release=True)` 如实回报是否真写成功（released/stuck），
     退出路径据此提醒用户 —— 旧代码不管成没成都报 released=True，
     run.on_quit 又是 `except: pass`，风扇卡在手动值上时用户毫无提示。
  ③ EC 写操作落盘 `ec-writes.log`（成功与失败都记）—— 以便事后回溯。
  ④ `POST /api/app/quit`：没注入钩子时安全回答（stop.cmd 的优雅退出入口）。

①（前端失联横幅）无法在这里断言：它是 DOM/CSS，用无头 Edge 截图看
（见 _verify_offline_shot 那段说明 / ui/_offline*.png）。

全程离线：MockHAL + 假 HAL，**不碰真机 EC**，也不动 model.json。
"""
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request

import model as model_mod
import server
from fancurve import FanCurve
from hal import WriteDenied

PORT = 8798
BASE = "http://127.0.0.1:%d" % PORT
_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class FakeHAL:
    """allow=False 时写抛 WriteDenied（模拟写入开关关着 / 授权过期）。"""

    def __init__(self, allow=True):
        self.writes_enabled = allow
        self.writes = []

    def set_fans(self, cpu=None, gpu=None, sys_=None):
        if not self.writes_enabled:
            raise WriteDenied("0x0205 是写命令，EC 写入未开启。")
        self.writes.append((cpu, gpu, sys_))


class FakeModel:
    """FanCurve 只用到 presets / save / reload_if_changed。"""

    def __init__(self):
        self.presets = {"fan": {
            "enabled": False, "source": "cpu",
            "curve": [{"t": 40, "d": 40}, {"t": 90, "d": 100}],
            "min_duty": 30, "max_duty": 100,
            "hysteresis": 2.0, "min_interval": 6.0,
        }}

    def save(self):
        pass

    def reload_if_changed(self):
        pass


def test_stop_reports_truth():
    """② stop() 要如实回报，且只在「真卡住」时给 last_error。"""
    out = {}

    # A) 写成功：风扇已交回 → released=True，不算 stuck，不留错误
    ec = FakeHAL(allow=True)
    fc = FanCurve(ec, model=FakeModel())
    fc.last_duty = 45                      # 假装之前写过手动值
    r = fc.stop(release=True)
    out["A"] = (r, list(ec.writes))

    # B) 写过手动值但写不回去 → released=False 且 stuck=True（必须提醒）
    ec = FakeHAL(allow=False)
    fc = FanCurve(ec, model=FakeModel())
    fc.last_duty = 45
    r = fc.stop(release=True)
    out["B"] = (r, list(ec.writes))

    # C) 本来就没写过手动值 + 写失败 → 无关紧要，不该报警
    ec = FakeHAL(allow=False)
    fc = FanCurve(ec, model=FakeModel())
    r = fc.stop(release=True)
    out["C"] = (r, list(ec.writes))

    # D) release=False（只停曲线，不动 EC）
    ec = FakeHAL(allow=True)
    fc = FanCurve(ec, model=FakeModel())
    fc.last_duty = 45
    r = fc.stop(release=False)
    out["D"] = (r, list(ec.writes))
    return out


def main():
    fails = []

    def check(name, cond, detail=""):
        print(("  [PASS] " if cond else "  [FAIL] ") + name
              + (("  -> " + str(detail)) if detail else ""))
        if not cond:
            fails.append(name)

    print("== ② stop(release) 如实回报 ==")
    out = test_stop_reports_truth()
    for k in ("A", "B", "C", "D"):
        r, wrote = out[k]
        print("   %s: released=%r stuck=%r last_error=%r 写次数=%d"
              % (k, r.get("released"), r.get("stuck"), r.get("last_error"), len(wrote)))
    check("A 写成功 → released=True / stuck=False / 无 last_error",
          out["A"][0]["released"] is True and out["A"][0]["stuck"] is False
          and out["A"][0]["last_error"] is None and len(out["A"][1]) == 1)
    check("B 卡住 → released=False / stuck=True / 带 last_error",
          out["B"][0]["released"] is False and out["B"][0]["stuck"] is True
          and bool(out["B"][0]["last_error"]))
    check("C 没写过手动值 → released=False 但 stuck=False（不吓人）",
          out["C"][0]["released"] is False and out["C"][0]["stuck"] is False
          and out["C"][0]["last_error"] is None)
    check("D release=False → 完全不写 EC",
          out["D"][0]["released"] is None and len(out["D"][1]) == 0)

    print("== ③ EC 写操作落盘 ec-writes.log ==")
    logf = model_mod.WRITE_LOG
    os.makedirs(os.path.dirname(logf), exist_ok=True)
    before = os.path.getsize(logf) if os.path.exists(logf) else 0
    app = server.App(mock=True)
    app.ec.enable_writes(True)
    app.ec.set_fans(40, 40, 40)                       # 成功写
    app.ec.arm(0x0300)
    app.ec.set_perf_mode(1)                           # 成功写（高危）
    app.ec.enable_writes(False)
    denied = ""
    try:
        app.ec.set_fans(50, 50, 50)                   # 被拒的写
    except Exception as e:                            # noqa
        denied = type(e).__name__
    time.sleep(0.1)
    with open(logf, encoding="utf-8") as f:
        f.seek(before)
        new = f.read()
    tail = "\n".join("     " + l for l in new.strip().splitlines()[-4:]) if new else ""
    print("   新增日志：\n" + tail)
    check("写成功已落盘（0x0205 手动风扇）", "0x0205" in new and "手动风扇" in new)
    check("高危命令也落盘（0x0300 性能模式）", "0x0300" in new and "性能模式" in new)
    check("被拒的写也落盘（含 FAIL 与原因）",
          "FAIL" in new and "0x0205" in new)
    # MockHAL 的假写必须带标记：否则事后回溯真机历史时会把它当成「真的写过 EC」
    check("假写都带 MOCK 标记", new.count("MOCK") >= 3, "MOCK 出现 %d 次" % new.count("MOCK"))
    check("确实是 WriteDenied 被拒", denied == "WriteDenied", denied)

    print("== ④ /api/app/quit ==")
    server.Handler.app = app
    srv = server.ThreadingHTTPServer(("127.0.0.1", PORT), server.Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    time.sleep(0.3)

    def post_json(path, body=None):
        req = urllib.request.Request(
            BASE + path, json.dumps(body or {}).encode(),
            {"Content-Type": "application/json"}, method="POST")
        with _opener.open(req, timeout=5) as r:
            return json.loads(r.read().decode())

    r = post_json("/api/app/quit")
    check("没钩子时安全回答 ok=False（不崩）", r.get("ok") is False, r)

    called = threading.Event()
    app.quit_hook = lambda: called.set()
    r = post_json("/api/app/quit")
    check("有钩子时 ok=True", r.get("ok") is True, r)
    check("钩子被异步调起（延时退，先回响应）", called.wait(2.0))
    app.quit_hook = None

    srv.shutdown()
    print()
    if fails:
        print("!!! %d 项未过: %s" % (len(fails), fails))
        return 1
    print("全部通过 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
