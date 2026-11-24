# -*- coding: utf-8 -*-
"""
【已下线 · 实验留档】软件灯效引擎

⚠️ 这个模块**已从应用移除**（2026-10-11），不在 `ControlCenter/` 里、没人 import 它。
   保留在此只是因为"原厂只有 4 种灯效、其余只能软件实现"这个探索本身有价值，
   以后若 EC 出现别的（更高频的）写入通道，可以拿这份改。

## 为什么下线：真机观感会闪，且是架构性上限

实测本机 `0x0100` 一帧的耗时（`gap` 已压到 60ms，是真实往返 15ms 的 4 倍余量）：

    三区（一帧 3 条命令） = 308 ms  ->  3.2 fps
    单区（一帧 1 条命令） = 120 ms  ->  8.3 fps

而且三区那一帧是**逐条依次写**的：第 1 条写完（addr 5）到第 3 条写完（addr 3）
之间有 205 ms —— 这段时间里三个区各自停留在**不同帧**的颜色上（视觉撕裂）。
低帧率阶跃 + 分区不同步，人眼直接读作「闪烁」。

⇒ 不是参数没调好：把 FX_GAP 压到极限（~20ms）三区也只有约 8fps，仍然不可用。
   要真正流畅得让 EC 支持「一条命令写全键盘」并带硬件过渡，我们控制不了硬件。
   用户拍板：闪就删掉 —— 所以应用里只保留原厂那 4 种硬件灯效。

## 下面保留原始实现说明（仅供理解）

软件灯效引擎 —— 在原厂 4 种硬件灯效之外，自己加动态灯效

## 为什么只能"自己实现"（2026-10-11 结论）

键盘背光的灯效是 **EC 硬件实现**的：代码写在 DT3D 高半字节（D3DH → LKBM）。
原厂只用了 4 个，用户已逐一验证（并且观感与名字对得上）：

    常亮 = 1     呼吸 = 3     多彩循环 = 6     流光溢彩 = 7

其余取值（0/2/4/5/8~15）没有对应效果 —— 也就是说
**「往 EC 里塞一个新灯效代码」这条路走不通**，新灯效只能由软件实现。

（见 PROTOCOL.md 与界面里的灯效名表，两边都标了同一组结论。）

## 做法

强制 EC 停在「常亮」（effect=1，它就不再跑自己的动画），然后**按帧把颜色写进去**：

    ▸ 原厂的「呼吸」只能呼吸一个颜色，我们能让它做原厂没有的事
    ▸ 温度联动 / 负载律动 / 警示闪 —— 原厂完全没有
    ▸ 三区相位错开的波浪 —— 比原厂「流光溢彩」更可控

## 性能（实测，见 MEMORY.md）

单条 0x0100 写入的 WMI 往返 **只有约 40ms**；`presets.ec.gap` 的 0.45s 是**人为节流**。
逐帧刷灯必须绕过它 —— EC 封装层已支持指定最小间隔。
于是：单区 ≈ 16fps、三区（每帧 3 条）≈ 5fps。这是硬件上限，不是懒得优化。

## 安全

* 必须 `ec.writes_enabled` 才能启动。
* 只写 0x0100（背光），**不碰**风扇 / 性能模式 / 显卡模式。
* **停止时把原颜色与原灯效写回**，不会把键盘留在动画的最后一帧。
* 退出控制中心时 on_quit 会调 stop()，同样会恢复。

## 与风扇曲线的关系

两者都写 EC，但 hal 的 `_raw` 有全局锁、`_throttle` 各自按 `_last_call` 计算，
所以并发安全、不会互相踩。灯效引擎用 60ms 间隔，风扇曲线用 450ms —— 互不阻塞。
"""
import math
import os
import sys
import threading
import time

# 这份留档在 recon/ 下，而配置层在 ControlCenter/ —— 补上路径才能单独跑
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "..", "ControlCenter"))

from model import MODEL

# 逐帧刷灯的命令间隔（秒）。
# ⚠️ 不能再用默认的 0.45s（那是给一次性写命令的保护值）——实测往返仅 ~40ms，
#    留 20ms 余量后取 60ms：单区 ~16fps、三区 ~5fps。
FX_GAP = 0.06

# 引擎运行期间强制 EC 处于「常亮」，否则 EC 自己的硬件动画会和我们的逐帧写打架
HOLD_EFFECT = 1

# 自研灯效速度的可调范围（倍速：既影响动画推进，也影响刷新率）
SPEED_MIN, SPEED_MAX = 0.3, 2.0


# ---------------------------------------------------------------- 颜色工具
def _hsv(h, s=1.0, v=1.0):
    """h/s/v ∈ [0,1] → (r,g,b) 0~255。自己写而不引 colorsys：签名更直观、无多余分支。"""
    h = h % 1.0
    i = int(h * 6.0) % 6
    f = h * 6.0 - int(h * 6.0)
    p = v * (1.0 - s)
    q = v * (1.0 - s * f)
    t = v * (1.0 - s * (1.0 - f))
    rgb = [(v, t, p), (q, v, p), (p, v, t),
           (p, q, v), (t, p, v), (v, p, q)][i]
    return tuple(max(0, min(255, int(round(c * 255)))) for c in rgb)


def _lerp(a, b, k):
    return a + (b - a) * k


def _lerp_rgb(c1, c2, k):
    return tuple(max(0, min(255, int(round(_lerp(c1[i], c2[i], k))))) for i in range(3))


def _ramp(stops, v):
    """在 [(阈值, (r,g,b)), ...] 之间线性插值，v 超出两端就取端点色。"""
    if v <= stops[0][0]:
        return stops[0][1]
    if v >= stops[-1][0]:
        return stops[-1][1]
    for (t1, c1), (t2, c2) in zip(stops, stops[1:]):
        if t1 <= v <= t2:
            k = (v - t1) / max(1e-6, t2 - t1)
            return _lerp_rgb(c1, c2, k)
    return stops[-1][1]


# ---------------------------------------------------------------- 灯效渲染
# 约定：
#   mode="single" 的渲染函数返回 (r,g,b)          → 写到键盘主区（addr 6）
#   mode="zones"  的渲染函数返回 [(addr,(r,g,b)), …] → 逐区写（A/B/C = 5/4/3）
# t = 动画时间（秒 × 速度倍率）；base = presets.backlight.rgb；st = 传感器快照

def _fx_rainbow(t, base, st):
    """彩虹渐变：色相匀速循环（一圈约 8 秒），单色整体渐变。"""
    return _hsv(t / 8.0, 1.0, 1.0)


def _fx_alarm(t, base, st):
    """警示闪：红 / 蓝交替快闪，中间留一瞬暗，约 0.9 秒一个周期。"""
    p = t % 0.9
    if p < 0.40:
        return (255, 0, 0)
    if p < 0.45:
        return (25, 0, 25)
    if p < 0.85:
        return (0, 40, 255)
    return (25, 0, 25)


def _fx_temp(t, base, st):
    """
    温度联动：颜色随 CPU 温度走 蓝→青→绿→黄→橙→红。
    ★ 这是原厂完全没有的一类 —— 用键盘背光的颜色直接"读"出当前负载水平。
    温度不变时主循环会跳过写入（见 _loop 的 frames 比较），不会空刷 EC。
    """
    c = st.get("cpu_temp")
    if c is None:
        c = st.get("gpu_temp")
    if c is None:
        c = 60
    return _ramp([
        (45, (0, 80, 255)), (55, (0, 200, 255)), (65, (0, 255, 80)),
        (75, (255, 220, 0)), (85, (255, 120, 0)), (95, (255, 0, 0)),
    ], float(c))


def _fx_load(t, base, st):
    """
    负载律动：CPU 使用率 → 亮度 + 颜色（空闲暗青，满载亮红）。
    与温度联动同理，值不变就不写 EC。
    """
    u = st.get("cpu_usage")
    if u is None:
        u = 0.0
    u = max(0.0, min(100.0, float(u))) / 100.0
    c = _lerp_rgb((0, 60, 90), (255, 30, 0), u)
    k = 0.25 + 0.75 * u                      # 亮度跟着负载走
    return tuple(int(round(x * k)) for x in c)


def _fx_wave(t, base, st):
    """三区波浪：三区色相依次落后 1/3 圈，形成流动的彩虹波。"""
    out = []
    for i, addr in enumerate((5, 4, 3)):      # A(左) / B(中) / C(右)
        out.append((addr, _hsv(t / 8.0 - i / 3.0, 1.0, 1.0)))
    return out


def _fx_chase(t, base, st):
    """三区追逐：一个亮色光点依次扫过 A→B→C，其余两区压暗到 12%。"""
    n = 3
    pos = int(t * 1.5) % n                    # 每 2/3 秒挪一格
    dim = (24, 24, 24)
    out = []
    for i, addr in enumerate((5, 4, 3)):
        out.append((addr, tuple(base) if i == pos else dim))
    return out


# key → (显示名, 渲染函数, 模式, 目标帧率, 说明)
FX = {
    "rainbow": ("彩虹渐变", _fx_rainbow, "single", 16, "色相循环的整体渐变，约 8 秒一圈"),
    "alarm":   ("警示闪",   _fx_alarm,   "single", 8,  "红蓝交替快闪，适合压测/渲染计时"),
    "temp":    ("温度联动", _fx_temp,    "single", 4,  "颜色随 CPU 温度 蓝→青→绿→黄→橙→红"),
    "load":    ("负载律动", _fx_load,    "single", 4,  "颜色与亮度随 CPU 使用率变化"),
    "wave":    ("三区波浪", _fx_wave,    "zones",  5,  "A/B/C 三区相位错开的流动彩虹"),
    "chase":   ("三区追逐", _fx_chase,   "zones",  5,  "亮色光点依次扫过 A→B→C"),
}

# 提示：这些是**软件**灯效，不是 EC 硬件灯效 —— 前端下拉里要分组标注清楚，
#       免得以后又把它们当成"原厂代码"去 EC 里找（那 4 个才是硬件灯效）。
KEY = "lightfx"


def catalog():
    """给前端的灯效清单（顺序即下拉顺序）。"""
    return [{"key": k, "label": v[0], "mode": v[2], "fps": v[3], "desc": v[4]}
            for k, v in FX.items()]


class LightFX:
    """
    灯效引擎。生命周期：start(name) → 后台线程按帧写 EC → stop() 恢复原状。

    ctx: 可调用对象，返回传感器快照 {"cpu_temp","gpu_temp","cpu_usage","gpu_usage"}。
         由 server 注入（直接读它已经维护好的 cache，避免灯效线程再打一次 EC/sensors）。
    """

    def __init__(self, ec, model=None, ctx=None):
        self.ec = ec
        self.model = model or MODEL
        self._ctx = ctx or (lambda: {})
        self.running = False
        self.name = None
        self.speed = 1.0
        self.frames = 0
        self.last_error = None
        self._thread = None
        self._stop = threading.Event()
        self._t0 = 0.0
        self._level = 1
        self._saved = None
        self._last_frames = None

    # ---------------- 内部 ----------------
    def _snapshot(self):
        try:
            return self._ctx() or {}
        except Exception:                       # noqa
            return {}

    def _interval(self):
        """帧间隔（秒）：目标帧率与 EC 能力取较慢的那个，再按速度倍率提速。"""
        if self.name not in FX:
            return FX_GAP
        _label, _fn, mode, fps, _desc = FX[self.name]
        n = 3 if mode == "zones" else 1
        floor = FX_GAP * n                      # EC 物理下限（每帧 n 条命令）
        target = 1.0 / max(1, fps)
        return max(target / max(SPEED_MIN, self.speed), floor)

    def _save_current(self):
        """记下启动前的状态，用于停止时恢复。只在本轮第一次启动时记。"""
        if self._saved is not None:
            return
        st = {}
        try:
            st = self.ec.cached_status() or {}
        except Exception:                       # noqa
            pass
        try:
            lvl = int(st.get("backlight_level") or 1)
        except Exception:                       # noqa
            lvl = 1
        zones = self.model.get("backlight", "zones") or []
        self._saved = {
            "level": max(0, min(2, lvl)),
            "rgb": list(self.model.get("backlight", "rgb", default=[255, 255, 255])),
            "effect": int(self.model.get("backlight", "effect", default=1)),
            "zones": [{"addr": z.get("addr"), "rgb": list(z.get("rgb") or [])}
                      for z in zones],
        }

    def _loop(self):
        while not self._stop.is_set():
            t_start = time.time()
            try:
                spec = FX.get(self.name)
                if not spec:
                    break
                _label, render, mode, _fps, _desc = spec
                st = self._snapshot()
                t = (time.time() - self._t0) * self.speed

                if mode == "zones":
                    base = list(self.model.get("backlight", "rgb",
                                               default=[255, 255, 255]))
                    frames = list(render(t, base, st))
                else:
                    rgb = render(t, list(self._saved["rgb"]), st)
                    frames = [(self.model.get("backlight", "addr", default=6),
                               tuple(rgb))]

                # ★ 内容没变就不写 EC：温度/负载灯效在数值稳定时几乎不发命令，
                #   省下的是 EC 的 I/O 配额（它还要给风扇曲线用）。
                if frames != self._last_frames:
                    for addr, rgb in frames:
                        if self._stop.is_set():
                            break
                        self.ec.set_backlight(
                            self._level, rgb[0], rgb[1], rgb[2],
                            brightness=addr, effect=HOLD_EFFECT, gap=FX_GAP)
                        self.frames += 1
                    self._last_frames = frames
                self.last_error = None
            except Exception as e:              # noqa
                # 单帧失败不能让引擎退出（比如 EC 被独占一下）；记下来下帧继续。
                self.last_error = "%s: %s" % (type(e).__name__, e)

            wait = self._interval() - (time.time() - t_start)
            if wait > 0:
                self._stop.wait(wait)

    # ---------------- 对外 ----------------
    def set_speed(self, speed):
        try:
            v = float(speed)
        except (TypeError, ValueError):
            return {"ok": False, "error": "速度不是数值"}
        self.speed = max(SPEED_MIN, min(SPEED_MAX, v))
        return {"ok": True, "speed": self.speed}

    def set_level(self, level):
        """
        灯效运行中用户改了亮度档 → 引擎必须跟着改。

        否则引擎每帧都用**启动时记下的档位**覆盖回去，用户点界面的档位按钮
        看着就是「没反应」（实际上写进去了、又被下一帧抹掉）。
        """
        try:
            v = max(0, min(2, int(level)))
        except (TypeError, ValueError):
            return {"ok": False, "error": "非法档位"}
        self._level = v
        return {"ok": True, "level": v}

    def start(self, name, speed=None):
        if name not in FX:
            return {"ok": False, "error": "未知灯效：%s" % name}
        if not self.ec.writes_enabled:
            return {"ok": False, "error": "请先开启 EC 写入权限"}
        if speed is not None:
            self.set_speed(speed)

        was_running = self.running
        if self.running:                        # 切换灯效：停线程但**不恢复**原状
            self._halt_thread()

        self._save_current()

        # 亮度档 0 = 灯全灭，灯效就看不见了 → 自动抬到 1 档（并告诉前端）
        self._level = self._saved["level"]
        bumped = False
        if self._level == 0:
            self._level = 1
            bumped = True

        self.name = name
        self.frames = 0
        self._last_frames = None
        self._t0 = time.time()
        self.running = True
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name="lightfx",
                                        daemon=True)
        self._thread.start()
        out = {"ok": True, "running": True, "name": name,
               "label": FX[name][0], "mode": FX[name][2],
               "speed": self.speed, "level": self._level,
               "interval_ms": int(self._interval() * 1000)}
        if bumped:
            out["note"] = "原亮度档为 0（灯全灭），已自动抬到 1 档"
        return out

    def _halt_thread(self):
        """只停线程，不做恢复（内部用）。"""
        self._stop.set()
        th = self._thread
        if th and th.is_alive() and th is not threading.current_thread():
            th.join(timeout=2.0)
        self._thread = None
        self.running = False

    def stop(self, restore=True):
        self._halt_thread()
        restored = False
        if restore and self._saved:
            try:
                s = self._saved
                zones = [z for z in (s.get("zones") or []) if z.get("addr") is not None]
                if zones:
                    self.ec.set_backlight_zones(s["level"], zones,
                                                effect=s["effect"])
                else:
                    self.ec.set_backlight(s["level"], *(s["rgb"] or [255, 255, 255]),
                                          effect=s["effect"])
                restored = True
            except Exception as e:              # noqa
                self.last_error = "恢复失败: %s" % e
        self._saved = None
        self._last_frames = None
        self.name = None
        return {"ok": True, "running": False, "restored": restored}

    def state(self):
        return {
            "running": self.running,
            "name": self.name,
            "label": FX[self.name][0] if self.name in FX else None,
            "speed": self.speed,
            "frames": self.frames,
            "last_error": self.last_error,
            "catalog": catalog(),
            "gap_ms": int(FX_GAP * 1000),
            "interval_ms": int(self._interval() * 1000) if self.name in FX else None,
            "speed_range": [SPEED_MIN, SPEED_MAX],
        }
