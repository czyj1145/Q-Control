# -*- coding: utf-8 -*-
import winreg
FOUND = []
KEYS = [
    (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE"),
    (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node"),
    (winreg.HKEY_CURRENT_USER, r"SOFTWARE"),
]
TARGET = ('fan', 'userfan', 'uf', 'perf', 'mode', 'apmd', 'perm', 'backlight', 'lkb')

def scan(root, path, depth=0):
    if depth > 4 or len(FOUND) > 400:
        return
    try:
        k = winreg.OpenKey(root, path)
    except Exception:
        return
    # 先看值
    i = 0
    while True:
        try:
            name, val, typ = winreg.EnumValue(k, i); i += 1
        except OSError:
            break
        ln = (name or "").lower()
        if any(t in ln for t in TARGET):
            FOUND.append((path, name, val, typ))
    # 再看子键
    j = 0
    subs = []
    while True:
        try:
            subs.append(winreg.EnumKey(k, j)); j += 1
        except OSError:
            break
    for s in subs:
        if any(t in s.lower() for t in ('controlcenter', 'nl5', 'quanta', 'gaming', 'fan')):
            scan(root, path + "\\" + s, depth + 1)

for root, base in KEYS:
    scan(root, base)

seen = set()
print("=== 原厂风扇/模式相关注册表值 ===")
for path, name, val, typ in FOUND:
    key = (path, name)
    if key in seen:
        continue
    seen.add(key)
    print(f"  {path}")
    print(f"      {name} = {val!r}  (type {typ})")
if not FOUND:
    print("  (未找到)")
