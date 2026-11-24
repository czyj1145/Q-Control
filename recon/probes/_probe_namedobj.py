# -*- coding: utf-8 -*-
"""枚举 \\BaseNamedObjects 里的 HWiNFO 相关内核对象。

用途：回答两个问题
  1. HWiNFO 是不是**单实例**（有没有互斥量/事件之类的所有权对象）；
  2. 共享内存段名是不是只有 `Global\\HWiNFO_SENS_SM2` 这一个
     （多实例时第二只会不会用别的名字 —— 2026-10-14「并排拉起不出 SM」的候选解释）。

只读。`\\BaseNamedObjects` 的枚举用 `NtOpenDirectoryObject` + `NtQueryDirectoryObject`；
ENTRY 在 x64 上是 32 字节（USHORT 长度 + 补齐 + UTF-16 名字）。
"""
import ctypes

nt = ctypes.WinDLL("ntdll")


class UNICODE_STRING(ctypes.Structure):
    _fields_ = [("Length", ctypes.c_ushort),
                ("MaximumLength", ctypes.c_ushort),
                ("Buffer", ctypes.c_void_p)]


class OBJECT_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Length", ctypes.c_ulong), ("RootDirectory", ctypes.c_void_p),
                ("ObjectName", ctypes.c_void_p), ("Attributes", ctypes.c_ulong),
                ("SecurityDescriptor", ctypes.c_void_p),
                ("SecurityQualityOfService", ctypes.c_void_p)]


class ENTRY32(ctypes.Structure):
    _fields_ = [("NameLength", ctypes.c_ulong), ("TypeLength", ctypes.c_ulong)]


nt.NtOpenDirectoryObject = ctypes.WINFUNCTYPE(
    ctypes.c_long, ctypes.POINTER(ctypes.c_void_p), ctypes.c_ulong,
    ctypes.c_void_p)(("NtOpenDirectoryObject", nt))
nt.NtQueryDirectoryObject = ctypes.WINFUNCTYPE(
    ctypes.c_long, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong,
    ctypes.c_int, ctypes.c_ulong,
    ctypes.POINTER(ctypes.c_ulong))(("NtQueryDirectoryObject", nt))

DIR = "\\BaseNamedObjects".encode("utf-16-le")
print("[1] 打开目录对象…")
_dir_buf = ctypes.create_string_buffer(DIR)          # ⚠️ 必须留着引用，否则被 GC 后
us = UNICODE_STRING(len(DIR), len(DIR),              #    NtOpenDirectoryObject 会读野指针
                    ctypes.cast(_dir_buf, ctypes.c_void_p))
oa = OBJECT_ATTRIBUTES(ctypes.sizeof(OBJECT_ATTRIBUTES), None,
                       ctypes.addressof(us), 0x40, None, None)
h = ctypes.c_void_p()
st = nt.NtOpenDirectoryObject(ctypes.byref(h), 0x0001, ctypes.byref(oa))
if st != 0:
    print("NtOpenDirectoryObject 失败 0x%08X" % (st & 0xFFFFFFFF))
    raise SystemExit(1)

buf = ctypes.create_string_buffer(1 << 16)
ctx = ctypes.c_ulong(0)
ret = ctypes.c_ulong(0)
rows = []
STATUS_NO_MORE = -1073741812          # 0xC000001A? 实际是 0x8000001A 的补码
for _round in range(200):             # 上限：防失控
    st = nt.NtQueryDirectoryObject(h, buf, ctypes.sizeof(buf), 0, ctx,
                                   ctypes.byref(ret))
    if st != 0:
        break
    off = 0
    got = 0
    while off + 8 <= ctypes.sizeof(buf):
        e = ENTRY32.from_buffer_copy(bytes(buf[off:off + 8]))
        if e.NameLength == 0 or e.NameLength > 1024:
            break
        raw = bytes(buf[off + 8: off + 8 + e.NameLength])
        rows.append(raw.decode("utf-16-le", errors="replace"))
        off += 8 + e.NameLength + e.TypeLength
        got += 1
    if got == 0:
        break

hits = [n for n in rows if "hwinfo" in n.lower()]
print("对象总数 = %d，HWiNFO 相关 = %d" % (len(rows), len(hits)))
for n in sorted(hits):
    print("   ", n)
