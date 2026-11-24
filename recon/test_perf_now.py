# -*- coding: utf-8 -*-
"""实测：性能模式写入是否落地（读回 APMD）"""
import json
import urllib.request

B = "http://127.0.0.1:8765"


def post(path, body=None):
    data = json.dumps(body or {}).encode()
    req = urllib.request.Request(B + path, data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.loads(r.read().decode())


def get(path):
    with urllib.request.urlopen(B + path, timeout=10) as r:
        return json.loads(r.read().decode())


def read_apmd():
    # 0x0300 不在只读白名单 → 读之前也要 arm
    post("/api/ec/arm", {"cmd": 0x0300})
    d = post("/api/ec/raw", {"sub": "0x0300", "args": [0] * 8, "group": 64000})
    return (d.get("u32") or {}).get("DAT2@4")


post("/api/ec/writes", {"enabled": True})
print("初始 APMD =", read_apmd())
for idx, apmd in ((1, 0x01), (2, 0x0A), (3, 0x60)):
    post("/api/ec/arm", {"cmd": 0x0300})
    r = post("/api/ec/perf", {"mode": idx})
    import time
    time.sleep(2.5)
    print("写 mode=%d (APMD应=%d) -> resp=%s   读回 APMD=%s"
          % (idx, apmd, r, read_apmd()))
