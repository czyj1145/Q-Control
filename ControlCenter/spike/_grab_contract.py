"""P1 契约冻结：把运行中后端的真实响应抓成 fixtures。

只打 GET 端点（全部无副作用），POST 端点的响应结构从处理函数源码读。
产物：ControlCenter/tests/fixtures/<name>.json
"""
import json
import os
import urllib.request

BASE = "http://127.0.0.1:8765"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "..", "tests", "fixtures")
os.makedirs(OUT, exist_ok=True)

# 只列无副作用的 GET。/api/debug/hwinfo 会 ensure_running（有副作用），跳过。
ENDPOINTS = {
    "status": "/api/status",
    "hardware": "/api/hardware",
    "history": "/api/history",
    "power_plans": "/api/power/plans",
    "fancurve": "/api/fancurve",
    "gpu_info": "/api/gpu/info",
    "gpu_oc": "/api/gpu/oc",
    "app_settings": "/api/app/settings",
    "vendor_state": "/api/vendor/state",
    "osd": "/api/osd",
    "device": "/api/device",
    "model": "/api/model",
    "notfound": "/api/__nope__",
}


def get(path):
    req = urllib.request.Request(BASE + path, method="GET")
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, r.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8")


def shape(obj, depth=0, max_depth=4):
    """生成类型骨架（用于 C# DTO 设计），值类型用实际样例标注。"""
    pad = "  " * depth
    if isinstance(obj, dict):
        lines = []
        for k, v in obj.items():
            t = shape(v, depth + 1, max_depth)
            lines.append(f"{pad}{k}: {t}")
        return "{\n" + "\n".join(lines) + "\n" + pad + "}"
    if isinstance(obj, list):
        if not obj:
            return "[] (empty)"
        return f"[{len(obj)}] " + shape(obj[0], depth, max_depth)
    if isinstance(obj, str):
        return f'str = {obj[:40]!r}'
    return f"{type(obj).__name__} = {obj}"


summary = []
for name, path in ENDPOINTS.items():
    code, body = get(path)
    try:
        data = json.loads(body)
    except Exception:
        data = None
    fn = os.path.join(OUT, name + ".json")
    # ⚠️ 存**原始响应文本**，不重新排版：桩数据要能在 A/B 对比（P7）时和真后端
    #    逐字节 diff。json.dumps 重排会改键序、加缩进，凭空制造 diff 噪音。
    #    原始 body 本身就是合法 JSON（中文不转义），读回来照样能解析。
    with open(fn, "w", encoding="utf-8", newline="\n") as f:
        f.write(body if body.endswith("\n") else body + "\n")
    size = len(body)
    summary.append((name, path, code, size, data is not None))
    print(f"{code}  {size:>8} B  {path}  -> {name}.json")

print()
print("=" * 70)
print("状态端点完整结构：")
code, body = get("/api/status")
print(shape(json.loads(body)))
print()
print("=" * 70)
print("设备端点完整结构：")
code, body = get("/api/device")
print(shape(json.loads(body)))
