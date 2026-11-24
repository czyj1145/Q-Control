"""生成应用图标 `Assets/qcontrol.ico`（多尺寸）+ 预览图。

为什么要这个脚本：`QControl.exe` 之前**没有任何图标资源** —— 任务栏、Alt+Tab、
资源管理器里看到的都是 .NET 的通用默认图标。品牌位图必须是一份「可以被 Windows
直接读的 .ico」，不能靠运行时自绘（托盘那种做法在窗口/外壳这边行不通）。

纯标准库实现（不引 Pillow：这台机器上没有，也不想为一张图加构建依赖）：
  · 形状 → 有符号距离场（SDF）：圆角方片、圆环（甜甜圈）、胶囊（圆头线段）
  · 抗锯齿 → 每档 4×4 超采样后盒式降采样
  · 编码 → BMP 条目（≤128，兼容性最好）+ PNG 条目（256，体积小）

设计空间 **16×16**，与下面两处必须保持一致（改一处要改三处）：
  · src/QControl.App/Ui/MainWindow.xaml  侧栏 logo（Viewbox + Canvas 16×16）
  · src/QControl.App/Tray/TrayIcon.cs    BuildGlyphIcon（托盘自绘）
字形 = 圆环（Q 的碗）+ 右下 45° 圆头尾巴。

用法：
    python spike/_gen_app_icon.py            # 生成 ico + 预览
    python spike/_gen_app_icon.py --check    # 只报文件状态，不写盘
"""

from __future__ import annotations

import math
import struct
import sys
import zlib
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                       # ControlCenter/
OUT_ICO = ROOT / "src" / "QControl.App" / "Assets" / "qcontrol.ico"
OUT_PREVIEW = ROOT / "_icon_preview"
# 关于页「关于作者」那块 46×46 的头像（2026-11-17 用户拍板：不要「星」字，要应用标志）。
# 单独出一张 PNG：WPF 的 Image 读 .ico 时挑哪一档不可控（默认常常是 16px 那一帧，
# 放到 46px 上糊成一片），所以这里直接把 256 档写成 PNG，界面按需缩放。
OUT_MARK = ROOT / "src" / "QControl.App" / "Assets" / "qcontrol-mark.png"
MARK_SIZE = 256

# ---------------------------------------------------------------- 设计常量（16×16）
BOWL_C = (8.0, 8.0)        # 碗心
BOWL_R = 5.6               # 碗的中心线半径
STROKE = 2.8               # 碗的描边宽度（≈ 碗半径的一半，偏粗一档：16px 下还要认得出是 Q）

# 尾巴**从碗心里穿出去**（起点落在内圈里，不是落在中心线上）—— 落在中心线上会被读成
# 「放大镜手柄」（2026-10-23 比过 4 版：起点落中心线 / 尾巴同宽 / 尾巴更细更长）；
# 穿心 + 略细才像 Q。要再比就临时改这里的数字重跑，预览图会立刻更新。
TAIL_A = (8.0 + 3.2 * math.sqrt(0.5), 8.0 + 3.2 * math.sqrt(0.5))
TAIL_B = (15.2, 15.2)                          # 尾巴终点（超出环外一小截）
TAIL_W = 2.5                                   # 尾巴宽度（比碗细一档，但别太细：32px 就看不见了）

# 墨迹包围盒由上面的几何**算出来**，别手填 —— 改字形时忘了同步这里，
# 图标就会偏到一边（居中靠的是墨迹中心，不是设计框中心）。
_INK_MIN = BOWL_C[0] - BOWL_R - STROKE / 2
_INK_MAX = max(TAIL_B[0] + TAIL_W / 2, BOWL_C[0] + BOWL_R + STROKE / 2)
INK_CENTER = ((_INK_MIN + _INK_MAX) / 2.0,) * 2
INK_SPAN = _INK_MAX - _INK_MIN

# 颜色（与 Ui/ThemeHex.cs 的 DarkAcc、UiPalette 的 Acc2、TrayIcon 的渐变一致）
ACC = (0x4D, 0xD6, 0xFF)
ACC2 = (0x7C, 0x5C, 0xFF)
GLYPH = (0x0B, 0x0D, 0x12)

SQUARE_RADIUS = 0.333      # 圆角方片半径 / 边长（侧栏 logo 32px 的 11 与之对齐）
GLYPH_RATIO = 0.575        # 字形墨迹跨度 / 边长
SS = 4                     # 超采样倍数

SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


# ---------------------------------------------------------------- SDF 形状
def _sd_round_rect(x: float, y: float, half: float, r: float) -> float:
    qx = abs(x) - half + r
    qy = abs(y) - half + r
    outside = math.hypot(max(qx, 0.0), max(qy, 0.0))
    d = (min(max(qx, qy), 0.0) if max(qx, qy) < 0 else outside) - r
    return d


def _sd_annulus(x: float, y: float, cx: float, cy: float, r: float, w: float) -> float:
    return abs(math.hypot(x - cx, y - cy) - r) - w / 2.0


def _sd_capsule(x: float, y: float, a, b, w: float) -> float:
    vx, vy = b[0] - a[0], b[1] - a[1]
    wx, wy = x - a[0], y - a[1]
    ln = vx * vx + vy * vy
    t = 0.0 if ln <= 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / ln))
    return math.hypot(wx - t * vx, wy - t * vy) - w / 2.0


def _cov(d: float, px: float) -> float:
    """距离 → 覆盖度。px = 一个像素在设计空间里的边长（`0.5 - d` 的同尺度版本）。"""
    return max(0.0, min(1.0, 0.5 - d / px))


def _mix(a, b, t: float):
    return a + (b - a) * t


def render(size: int) -> list[list[tuple[int, int, int, int]]]:
    """渲染一张 size×size 的直通 RGBA（行主序，左上原点）。"""
    # ⚠️ 下面全部在**超采样像素坐标**里算（0..n），不是 0..size —— 两者混用会让
    #    图形只画在输出图左上角 1/SS 那块（踩过）。
    n = size * SS
    half = n / 2.0
    radius = SQUARE_RADIUS * n
    scale = (GLYPH_RATIO * n) / INK_SPAN     # 设计单位 → 超采样像素
    off_x, off_y = INK_CENTER
    glyph_px = 1.0 / scale                   # 一个子像素在设计单位里的边长

    acc = tuple(c / 255.0 for c in ACC)
    acc2 = tuple(c / 255.0 for c in ACC2)
    glyph = tuple(c / 255.0 for c in GLYPH)

    rows = []
    for sy in range(n):
        row = []
        for sx in range(n):
            # 子像素中心（超采样像素坐标）
            fx = sx + 0.5
            fy = sy + 0.5
            cov_sq = _cov(_sd_round_rect(fx - half, fy - half, half, radius), 1.0)
            if cov_sq <= 0.0:
                row.append((0, 0, 0, 0))
                continue
            # 45° 渐变：沿 (1,1) 方向投影
            t = max(0.0, min(1.0, (fx + fy) / (2.0 * n)))
            base = tuple(_mix(acc[i], acc2[i], t) for i in range(3))
            # 字形（只在方片内部生效）
            gx = off_x + (fx - half) / scale
            gy = off_y + (fy - half) / scale
            d = min(_sd_annulus(gx, gy, BOWL_C[0], BOWL_C[1], BOWL_R, STROKE),
                    _sd_capsule(gx, gy, TAIL_A, TAIL_B, TAIL_W))
            cov_g = _cov(d, glyph_px) * cov_sq
            rgb = tuple(_mix(base[i], glyph[i], cov_g) for i in range(3))
            row.append((int(rgb[0] * 255 + 0.5), int(rgb[1] * 255 + 0.5),
                        int(rgb[2] * 255 + 0.5), int(cov_sq * 255 + 0.5)))
        rows.append(row)

    # 盒式降采样
    out = []
    for y in range(size):
        line = []
        for x in range(size):
            r = g = b = a = 0
            for dy in range(SS):
                for dx in range(SS):
                    p = rows[y * SS + dy][x * SS + dx]
                    # 直通色按 alpha 加权平均，避免边缘发灰
                    al = p[3]
                    r += p[0] * al
                    g += p[1] * al
                    b += p[2] * al
                    a += al
            if a == 0:
                line.append((0, 0, 0, 0))
            else:
                line.append((int(r / a + 0.5), int(g / a + 0.5), int(b / a + 0.5),
                             int(a / (SS * SS) + 0.5)))
        out.append(line)
    return out


# ---------------------------------------------------------------- 编码
def _bmp_entry(img, size: int) -> bytes:
    """ICO 里的 BMP 条目：BITMAPINFOHEADER（高 = 2×）+ 32bpp BGRA（自下而上）+ AND 掩码。"""
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    pixels = bytearray()
    for y in range(size - 1, -1, -1):
        for x in range(size):
            r, g, b, a = img[y][x]
            pixels += bytes((b, g, r, a))
    mask_row = (size + 31) // 32 * 4
    mask = bytearray()
    for y in range(size - 1, -1, -1):
        bits = bytearray(mask_row)
        for x in range(size):
            if img[y][x][3] == 0:                        # 1 = 透明
                bits[x // 8] |= 1 << (7 - (x % 8))
        mask += bits
    return header + bytes(pixels) + bytes(mask)


def _png_entry(img, size: int) -> bytes:
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            raw += bytes(img[y][x])

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))


def build_ico() -> bytes:
    entries = []
    for size in SIZES:
        img = render(size)
        data = _png_entry(img, size) if size >= 128 else _bmp_entry(img, size)
        entries.append((size, data))
    head = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    dirs, blobs = b"", b""
    for size, data in entries:
        dim = 0 if size >= 256 else size
        dirs += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data
    return head + dirs + blobs


# ---------------------------------------------------------------- 预览
def _fill(buf, w, h, x0, y0, bw, bh, rgb):
    for y in range(max(0, y0), min(h, y0 + bh)):
        for x in range(max(0, x0), min(w, x0 + bw)):
            i = (y * w + x) * 4
            buf[i], buf[i + 1], buf[i + 2], buf[i + 3] = rgb[0], rgb[1], rgb[2], 255


def _blit(dst, dw, dh, img, size, x0, y0):
    for y in range(min(size, dh - y0)):
        for x in range(min(size, dw - x0)):
            r, g, b, a = img[y][x]
            if a == 0:
                continue
            i = ((y0 + y) * dw + (x0 + x)) * 4
            t = a / 255.0
            dst[i] = int(r * t + dst[i] * (1 - t))
            dst[i + 1] = int(g * t + dst[i + 1] * (1 - t))
            dst[i + 2] = int(b * t + dst[i + 2] * (1 - t))
            dst[i + 3] = 255


def save_png(path: Path, w: int, h: int, buf: bytearray):
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += buf[y * w * 4:(y + 1) * w * 4]

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    path.write_bytes(b"\x89PNG\r\n\x1a\n"
                     + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
                     + chunk(b"IEND", b""))


def build_preview(bg=(0x20, 0x20, 0x20)):
    """预览：左侧 256 大图，右侧按大→小排真实尺寸。底色可换（深/浅任务栏各一张）。"""
    imgs = {s: render(s) for s in SIZES}
    pad, gap = 16, 20

    # 布局：左边 256，右边一列（128 / 64 / 48 / 32+24+16）
    w = pad + 256 + gap + 128 + pad
    h = pad + 256 + pad
    buf = bytearray(w * h * 4)
    _fill(buf, w, h, 0, 0, w, h, bg)
    _blit(buf, w, h, imgs[256], 256, pad, pad)
    x = pad + 256 + gap
    _blit(buf, w, h, imgs[128], 128, x, pad)
    y = pad + 128 + 12
    _blit(buf, w, h, imgs[64], 64, x, y)
    _blit(buf, w, h, imgs[48], 48, x + 64 + 8, y + 8)
    y += 64 + 12
    xx = x
    for s in (32, 24, 16):
        _blit(buf, w, h, imgs[s], s, xx, y + 32 - s)
        xx += s + 10
    return w, h, buf


def main(argv):
    check = "--check" in argv
    if check:
        print(f"ico    {'存在' if OUT_ICO.exists() else '缺失'}: {OUT_ICO}")
        if OUT_ICO.exists():
            print(f"       大小 {OUT_ICO.stat().st_size} 字节")
        print(f"mark   {'存在' if OUT_MARK.exists() else '缺失'}: {OUT_MARK}")
        return 0

    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    data = build_ico()
    OUT_ICO.write_bytes(data)
    print(f"写出 {OUT_ICO.relative_to(ROOT)}  ({len(data)} 字节, {len(SIZES)} 档: "
          f"{', '.join(map(str, SIZES))})")

    OUT_MARK.write_bytes(_png_entry(render(MARK_SIZE), MARK_SIZE))
    print(f"写出 {OUT_MARK.relative_to(ROOT)}  ({MARK_SIZE}×{MARK_SIZE})")

    OUT_PREVIEW.mkdir(exist_ok=True)
    for name, bg in (("icon-dark.png", (0x20, 0x20, 0x20)),
                     ("icon-light.png", (0xF0, 0xF3, 0xF8))):
        w, h, buf = build_preview(bg)
        save_png(OUT_PREVIEW / name, w, h, buf)
        print(f"写出 {(OUT_PREVIEW / name).relative_to(ROOT)}  ({w}×{h})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
