# -*- coding: utf-8 -*-
"""
广达(Quanta) RW_GMWMI 接口封装
逆向自本机 DSDT：Device(PNP0C14) / _WDG 数据块 "AA" / 方法 WQAA(读) WSAA(写)

BufferBytes(32B) 字段布局（CreateField 位定义）：
  DAT0 : bit   0, 16b  -> 命令组   (GWF0)
  DAT1 : bit  16, 16b  -> 子命令   (GWF1)
  DAT2 : bit  32, 32b  -> 数据0    (GWD0)
  DAT3 : bit  64, 32b  -> 数据1    (GWD2)
  DT3A..DT3D: byte 8..11
  D3DL/D3DH : byte11 低/高 4 位
  DAT4 : bit  96, 32b  -> 数据2    (GWD3)
  DAT5 : bit 128, 32b
  DAT6 : bit 160, 32b
  RESV : bit 192, 64b
"""
import pythoncom
import win32com.client

WMI_NS = "winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\WMI"
CLASS = "RW_GMWMI"
INSTANCE = 'ACPI\\PNP0C14\\0x1_0'


class GMWMI:
    def __init__(self):
        pythoncom.CoInitialize()
        self.svc = win32com.client.GetObject(WMI_NS)

    def _inst(self):
        for it in self.svc.InstancesOf(CLASS):
            if it.InstanceName == INSTANCE:
                return it
        # 兜底：取第一个
        for it in self.svc.InstancesOf(CLASS):
            return it
        raise RuntimeError("未找到 RW_GMWMI 实例")

    def read_buffer(self):
        """触发 WQAA，返回 32 字节列表"""
        it = self._inst()
        return list(it.Properties_("BufferBytes").Value or [])

    def write_buffer(self, buf):
        """触发 WSAA，buf 为 32 字节序列，返回 Put 结果"""
        assert len(buf) == 32
        it = self._inst()
        it.Properties_("BufferBytes").Value = list(buf)
        return it.Put_()

    # ---- 字段助手 ----
    @staticmethod
    def set_u16(buf, off, val):
        buf[off] = val & 0xFF
        buf[off + 1] = (val >> 8) & 0xFF

    @staticmethod
    def set_u32(buf, off, val):
        for i in range(4):
            buf[off + i] = (val >> (8 * i)) & 0xFF

    @staticmethod
    def get_u16(buf, off):
        return buf[off] | (buf[off + 1] << 8)

    @staticmethod
    def get_u32(buf, off):
        return int.from_bytes(bytes(buf[off:off + 4]), 'little')

    def call(self, cmd, sub, d0=0, d1=0, d2=0, d3=0):
        """执行一次命令：写 buffer -> 触发 WSAA -> 读回 buffer"""
        buf = [0] * 32
        self.set_u16(buf, 0, cmd)    # DAT0
        self.set_u16(buf, 2, sub)    # DAT1
        self.set_u32(buf, 4, d0)     # DAT2
        self.set_u32(buf, 8, d1)     # DAT3
        self.set_u32(buf, 12, d2)    # DAT4
        self.set_u32(buf, 16, d3)    # DAT5
        self.write_buffer(buf)
        return self.read_buffer()


def dump(buf, label=""):
    print(f"  {label:<16} " + " ".join(f"{b:02X}" for b in buf))
    print(f"  {'':16} DAT0=0x{GMWMI.get_u16(buf,0):04X} DAT1=0x{GMWMI.get_u16(buf,2):04X} "
          f"DAT2=0x{GMWMI.get_u32(buf,4):08X} DAT3=0x{GMWMI.get_u32(buf,8):08X} "
          f"DAT4=0x{GMWMI.get_u32(buf,12):08X} DAT5=0x{GMWMI.get_u32(buf,16):08X} "
          f"DAT6=0x{GMWMI.get_u32(buf,20):08X}")


if __name__ == "__main__":
    g = GMWMI()
    print("=== 1) 直接读取当前 BufferBytes (WQAA) ===")
    dump(g.read_buffer(), "read()")

    print("\n=== 2) 写入只读状态命令 0xFA00/0x0200 后读回 ===")
    try:
        r = g.call(0xFA00, 0x0200)
        dump(r, "after WSAA")
    except Exception as e:
        print("  失败:", e)

    print("\n=== 3) 再读一次 (WQAA) ===")
    dump(g.read_buffer(), "read() again")
