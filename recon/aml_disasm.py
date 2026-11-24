# -*- coding: utf-8 -*-
"""
轻量 AML 反编译器 —— 用于逆向广达(Quanta)/七彩虹 隐星 G16 Pro 的 DSDT/SSDT
目标：把 ACPI 表还原为类 iasl 的可读文本，从而搞清 RW_GMWMI 的 EC 命令协议。
"""
import sys, os, re

# ---------- 基础读取 ----------
class Reader:
    def __init__(self, data, pos=0):
        self.d = data
        self.p = pos
    def byte(self):
        v = self.d[self.p]; self.p += 1; return v
    def peek(self, n=1, off=0):
        return self.d[self.p + off: self.p + off + n]
    def word(self):
        v = int.from_bytes(self.d[self.p:self.p+2], 'little'); self.p += 2; return v
    def dword(self):
        v = int.from_bytes(self.d[self.p:self.p+4], 'little'); self.p += 4; return v
    def qword(self):
        v = int.from_bytes(self.d[self.p:self.p+8], 'little'); self.p += 8; return v
    def eof(self, end):
        return self.p >= end

def pkg_length(r):
    lead = r.byte()
    bc = lead >> 6
    if bc == 0:
        return lead & 0x3F
    length = lead & 0x0F
    for i in range(bc):
        length |= r.byte() << (4 + 8 * i)
    return length

def is_nameseg(b):
    return (65 <= b <= 90) or b == 0x5F   # '_'

def is_nameseg_char(b):
    return (65 <= b <= 90) or (48 <= b <= 57) or b == 0x5F

def valid_nameseg(r):
    if r.p + 4 > len(r.d):
        return False
    b = r.peek(4)
    if not is_nameseg(b[0]):
        return False
    return all(is_nameseg_char(c) for c in b[1:])

def name_string(r):
    if r.p >= len(r.d):
        return "?"
    s = ""
    if r.peek(1) == b'\\':
        s = "\\"; r.byte()
        if r.peek(1) == b'\x00':
            r.byte(); return "\\"
    while r.peek(1) == b'^':
        s += "^"; r.byte()
    c = r.peek(1)
    if c == b'/':                       # MultiNamePrefix
        r.byte(); n = r.byte(); segs = []
        for _ in range(n):
            segs.append(r.peek(4).decode('latin1')); r.p += 4
        return s + ".".join(segs)
    if c == b'.':                       # DualNamePrefix
        r.byte()
        a = r.peek(4).decode('latin1'); r.p += 4
        b2 = r.peek(4).decode('latin1'); r.p += 4
        return s + a + "." + b2
    seg = r.peek(4).decode('latin1'); r.p += 4
    return s + seg

# ---------- opcode 表 ----------
TYPE1 = {
    0xA0: 'If', 0xA1: 'Else', 0xA2: 'While', 0xA3: 'Noop',
    0xA4: 'Return', 0xA5: 'Break', 0xA6: 'BreakPoint', 0x9F: 'Continue',
}
TYPE2_1ARG = {
    0x71: 'RefOf', 0x75: 'Increment', 0x76: 'Decrement', 0x80: 'Not',
    0x81: 'FindSetLeftBit', 0x82: 'FindSetRightBit', 0x83: 'DerefOf',
    0x87: 'SizeOf', 0x8E: 'ObjectType', 0x92: 'LNot', 0x96: 'ToBuffer',
    0x97: 'ToDecimalString', 0x98: 'ToHexString', 0x99: 'ToInteger',
}
TYPE2_2ARG = {
    0x72: 'Add', 0x73: 'Concatenate', 0x74: 'Subtract', 0x77: 'Multiply',
    0x78: 'Divide', 0x79: 'ShiftLeft', 0x7A: 'ShiftRight', 0x7B: 'And',
    0x7C: 'NAnd', 0x7D: 'Or', 0x7E: 'NOr', 0x7F: 'Xor', 0x84: 'ConcatResTemplate',
    0x85: 'Mod', 0x86: 'Notify', 0x88: 'Index', 0x90: 'LAnd', 0x91: 'LOr',
    0x93: 'LEqual', 0x94: 'LGreater', 0x95: 'LLess', 0x9D: 'CopyObject',
    0x8A: 'CreateDWordField', 0x8B: 'CreateWordField', 0x8C: 'CreateByteField',
    0x8D: 'CreateBitField', 0x8F: 'CreateQWordField',
}
TYPE2_3ARG = {0x9C: 'ToString'}
TYPE2_4ARG = {0x9E: 'Mid', 0x89: 'Match'}
EXT = {
    0x01: 'Mutex', 0x02: 'Event', 0x12: 'CondRefOf', 0x13: 'CreateField',
    0x1F: 'LoadTable', 0x20: 'Revision', 0x21: 'Debug', 0x30: 'Fatal',
    0x32: 'Sleep', 0x33: 'Stall', 0x80: 'OpRegion', 0x81: 'Field',
    0x82: 'Device', 0x83: 'Processor', 0x84: 'PowerRes', 0x85: 'ThermalZone',
    0x86: 'IndexField', 0x87: 'BankField', 0x88: 'DataRegion',
}

class Disasm:
    def __init__(self, data):
        self.r = Reader(data)
        self.out = []
        self.depth = 0
        self.scope = []

    def emit(self, s):
        self.out.append("    " * self.depth + s)

    # ---- 顶层 ----
    def run(self, end):
        guard = 0
        last = -1
        while not self.r.eof(end):
            guard += 1
            if guard > 200000 or len(self.out) > 400000:
                self.emit("; ... 超出解析预算，截断")
                return
            start = self.r.p
            try:
                self.term(end)
            except Exception:
                try:
                    self.emit(f"; !! 解析中断 @0x{start:X}")
                except Exception:
                    pass
                # 关键：只能前进，绝不能回退，否则会指数级重复
                self.r.p = max(self.r.p, start + 1)
            if self.r.p <= start:      # 防死循环
                self.r.p = start + 1
            if self.r.p == last:
                self.r.p += 1
            last = self.r.p

    def term(self, end):
        b = self.r.peek(1)[0]

        # 常量/简单操作数（TermArg 位置）
        if b == 0x00: self.r.byte(); self.emit("Zero"); return
        if b == 0x01: self.r.byte(); self.emit("One"); return
        if b == 0xFF: self.r.byte(); self.emit("Ones"); return
        if 0x60 <= b <= 0x67: self.r.byte(); self.emit(f"Local{b - 0x60}"); return
        if 0x68 <= b <= 0x6E: self.r.byte(); self.emit(f"Arg{b - 0x68}"); return
        if b == 0x0A: self.r.byte(); self.emit(f"0x{self.r.byte():02X}"); return
        if b == 0x0B: self.r.byte(); self.emit(f"0x{self.r.word():04X}"); return
        if b == 0x0C: self.r.byte(); self.emit(f"0x{self.r.dword():08X}"); return
        if b == 0x0E: self.r.byte(); self.emit(f"0x{self.r.qword():X}"); return
        if b == 0x0D:
            self.r.byte()
            s = bytearray()
            while self.r.peek(1) not in (b'\x00', b''):
                s.append(self.r.byte())
            self.r.byte()
            self.emit(f'"{s.decode("latin1")}"'); return

        if b == 0x06:   # Alias
            self.r.byte(); n1 = name_string(self.r); n2 = name_string(self.r)
            self.emit(f"Alias({n1}, {n2})"); return

        if b == 0x08:   # Name
            self.r.byte(); n = name_string(self.r)
            v = self.read_dataobj()
            self.emit(f"Name({n}, {v})"); return

        if b == 0x10:   # Scope
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r)
            self.emit(f"Scope({n}) {{"); self.depth += 1
            self.scope.append(n)
            self.run(end2); self.depth -= 1; self.scope.pop()
            self.r.p = end2; self.emit("}"); return

        if b == 0x5B:
            self.ext_op(end); return

        if b == 0x14:   # Method
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r); flags = self.r.byte()
            self.emit(f"Method({n}, {flags & 7}, "
                      f"{'S' if flags & 8 else 'N'}"
                      f"{'0x%02X' % self.r.byte() if flags & 0x80 else ''}) {{")
            if flags & 0x80: self.r.byte()
            self.depth += 1; self.run(end2); self.depth -= 1
            self.r.p = end2; self.emit("}"); return

        if b == 0x15:   # External
            self.r.byte(); n = name_string(self.r)
            oc = self.r.byte(); ac = self.r.byte()
            self.emit(f"External({n}, ObjType=0x{oc:02X}, ArgCount={ac})"); return

        if b == 0x11:   # Buffer
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            sz = self.read_dataobj()
            items = []
            while not self.r.eof(end2):
                if self.r.peek(1) == b'\x0A':
                    self.r.byte(); items.append(self.r.byte())
                elif self.r.peek(1) == b'\x0B':
                    self.r.byte(); items += list(self.r.peek(2)); self.r.p += 2
                elif self.r.peek(1) == b'\x0C':
                    self.r.byte(); items += list(self.r.peek(4)); self.r.p += 4
                elif is_nameseg(self.r.peek(1)[0]):
                    items.append(name_string(self.r))
                else:
                    items.append(f"?{self.r.byte():02X}")
            self.r.p = end2
            self.emit("Buffer(0x%02X) { %s }" % (
                sz if isinstance(sz, int) else 0,
                " ".join(f"0x{i:02X}" if isinstance(i, int) else str(i) for i in items)))
            return

        if b == 0x12 or b == 0x13:   # Package / VarPackage
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            n = self.r.byte()
            items = []
            idx = 0
            while not self.r.eof(end2):
                if b == 0x13:
                    items.append(self.read_dataobj_raw())
                else:
                    items.append(self.read_dataobj_raw())
                idx += 1
                if idx > 256: break
            self.r.p = end2
            self.emit(f"Package({n}) {{ {', '.join(str(i) for i in items)} }}"); return

        if b in TYPE1:
            self.r.byte(); name = TYPE1[b]
            # 无 PkgLength 的简单语句：Return / Break / Continue / Noop / BreakPoint
            if b == 0xA4:   # Return: ReturnOp TermArg
                v = self.read_dataobj_raw()
                self.emit(f"Return({v})"); return
            if b in (0xA3, 0xA5, 0xA6, 0x9F):
                self.emit(f"{name}"); return
            if b == 0xA1:   # Else
                ln = pkg_length(self.r); end2 = self.r.p + ln
                self.emit("Else {"); self.depth += 1; self.run(end2); self.depth -= 1
                self.r.p = end2; self.emit("}"); return
            ln = pkg_length(self.r); end2 = self.r.p + ln
            if b == 0xA0:   # If
                cond = self.read_dataobj_raw()
                self.emit(f"If ({cond}) {{"); self.depth += 1
                self.run(end2); self.depth -= 1; self.r.p = end2; self.emit("}"); return
            if b == 0xA2:   # While: WhileOp PkgLength Predicate TermList
                cond = self.read_dataobj_raw()
                self.emit(f"While ({cond}) {{"); self.depth += 1
                self.run(end2); self.depth -= 1; self.r.p = end2; self.emit("}"); return
            self.emit(f"{name} ???"); self.r.p = end2; return

        if b == 0x70:   # Store
            self.r.byte()
            src = self.read_dataobj_raw()
            dst = self.read_dataobj_raw()
            self.emit(f"Store({src}, {dst})"); return

        if b in TYPE2_1ARG:
            self.r.byte(); self.emit(f"{TYPE2_1ARG[b]}({self.read_dataobj_raw()})"); return
        if b in TYPE2_2ARG:
            self.r.byte()
            a1 = self.read_dataobj_raw(); a2 = self.read_dataobj_raw()
            self.emit(f"{TYPE2_2ARG[b]}({a1}, {a2})"); return
        if b in TYPE2_3ARG:
            self.r.byte()
            a1 = self.read_dataobj_raw(); a2 = self.read_dataobj_raw(); a3 = self.read_dataobj_raw()
            self.emit(f"{TYPE2_3ARG[b]}({a1}, {a2}, {a3})"); return
        if b in TYPE2_4ARG:
            self.r.byte()
            xs = [self.read_dataobj_raw() for _ in range(4)]
            self.emit(f"{TYPE2_4ARG[b]}({', '.join(map(str, xs))})"); return

        # 裸 NameString（表达式引用）
        c = self.r.peek(1)
        if c in (b'\\', b'^', b'.', b'/') or valid_nameseg(self.r):
            self.emit(name_string(self.r)); return

        self.emit(f"; ?? Unknown 0x{b:02X}")
        self.r.byte()

    # ---- 0x5B 扩展 ----
    def ext_op(self, end):
        self.r.byte()
        op = self.r.byte()
        name = EXT.get(op, f"Ext0x{op:02X}")
        if name in ('Mutex', 'Event'):
            n = name_string(self.r)
            extra = ""
            if name == 'Mutex': extra = f", 0x{self.r.byte():02X}"
            self.emit(f"{name}({n}{extra})"); return
        if name == 'CondRefOf':
            self.emit(f"CondRefOf({self.read_dataobj_raw()}, {self.read_dataobj_raw()})"); return
        if name in ('Sleep', 'Stall'):
            self.emit(f"{name}({self.read_dataobj_raw()})"); return
        if name == 'Debug':
            self.emit("Debug"); return
        if name == 'OpRegion':
            ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r); sp = self.r.byte()
            off = self.read_dataobj_raw(); sz = self.read_dataobj_raw()
            space = {0: 'SystemMemory', 1: 'SystemIO', 2: 'PCI_Config',
                     3: 'EmbeddedControl', 4: 'SMBus', 10: 'PlatformComm'}.get(sp, f'0x{sp:02X}')
            self.emit(f"OperationRegion({n}, {space}, {off}, {sz})"); self.r.p = end2; return
        if name in ('Field', 'IndexField', 'BankField'):
            ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r); fl = self.r.byte()
            bits = []
            if fl & 0x01: bits.append('Lock')
            acc = (fl >> 1) & 0x0F
            bits.append({0: 'AnyAcc', 1: 'ByteAcc', 2: 'WordAcc', 3: 'DWordAcc',
                         4: 'QWordAcc', 5: 'BufferAcc'}.get(acc, f'Acc{acc}'))
            if name == 'BankField':
                self.emit(f"BankField({n}, {self.read_dataobj_raw()}, {'|'.join(bits)}) {{")
            else:
                self.emit(f"Field({n}, {'|'.join(bits)}) {{")
            self.depth += 1
            idx = 0
            while not self.r.eof(end2):
                e = self.r.peek(1)[0]
                if e == 0x00:
                    self.r.byte(); self.emit(f"Offset(0x{idx:02X}) // reserved")
                elif e == 0x01:
                    self.r.byte(); l2 = pkg_length(self.r); e2 = self.r.p + l2
                    t = self.r.byte(); a = self.r.byte()
                    self.emit(f"AccessAs(0x{t:02X}, 0x{a:02X})"); self.r.p = e2
                elif e == 0x02:
                    self.r.byte(); self.emit(f"Connect({name_string(self.r)})")
                elif e == 0x03:
                    self.r.byte(); l2 = pkg_length(self.r); self.r.p = self.r.p + l2
                    self.emit("AccessAsExt()")
                else:
                    nm = self.r.peek(4).decode('latin1'); self.r.p += 4
                    l2 = pkg_length(self.r)
                    self.emit(f"{nm},   // bit_off=0x{idx:02X} len={l2}")
                    idx += l2
            self.depth -= 1; self.r.p = end2; self.emit("}"); return
        if name in ('Device', 'Processor', 'PowerRes', 'ThermalZone'):
            ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r)
            if name == 'Processor':
                self.r.byte(); self.r.dword(); self.r.byte()
            self.emit(f"{name}({n}) {{")
            self.depth += 1; self.scope.append(n)
            self.run(end2); self.depth -= 1; self.scope.pop()
            self.r.p = end2; self.emit("}"); return
        if name == 'DataRegion':
            ln = pkg_length(self.r); end2 = self.r.p + ln
            n = name_string(self.r)
            s1 = self.read_dataobj_raw(); s2 = self.read_dataobj_raw(); s3 = self.read_dataobj_raw()
            self.emit(f"DataRegion({n}, {s1}, {s2}, {s3})"); self.r.p = end2; return
        # 未知扩展：跳过一个 pkg 块
        try:
            ln = pkg_length(self.r); self.r.p += ln
        except Exception:
            pass
        self.emit(f"; ?? {name}")

    # ---- TermArg / DataObject ----
    def read_dataobj_raw(self):
        b = self.r.peek(1)[0]
        if b == 0x0A: self.r.byte(); return f"0x{self.r.byte():02X}"
        if b == 0x0B: self.r.byte(); return f"0x{self.r.word():04X}"
        if b == 0x0C: self.r.byte(); return f"0x{self.r.dword():08X}"
        if b == 0x0E: self.r.byte(); return f"0x{self.r.qword():X}"
        if b == 0x00: self.r.byte(); return "0x00"
        if b == 0x01: self.r.byte(); return "0x01"
        if b == 0xFF: self.r.byte(); return "0xFF"
        if 0x60 <= b <= 0x67: self.r.byte(); return f"Local{b-0x60}"
        if 0x68 <= b <= 0x6E: self.r.byte(); return f"Arg{b-0x68}"
        if b == 0x0D:
            self.r.byte(); s = bytearray()
            while self.r.peek(1) not in (b'\x00', b''): s.append(self.r.byte())
            self.r.byte(); return f'"{s.decode("latin1")}"'
        if b == 0x11:
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            sz = self.read_dataobj_raw(); items = []
            while not self.r.eof(end2):
                if self.r.peek(1) == b'\x0A': self.r.byte(); items.append(self.r.byte())
                elif is_nameseg(self.r.peek(1)[0]): items.append(name_string(self.r))
                else: items.append(f"?{self.r.byte():02X}")
            self.r.p = end2
            return "Buffer() { %s }" % " ".join(f"0x{i:02X}" if isinstance(i, int) else str(i) for i in items)
        if b == 0x12 or b == 0x13:
            self.r.byte(); ln = pkg_length(self.r); end2 = self.r.p + ln
            self.r.byte(); items = []
            c = 0
            while not self.r.eof(end2) and c < 128:
                items.append(self.read_dataobj_raw()); c += 1
            self.r.p = end2
            return "Package() { %s }" % ", ".join(map(str, items))
        if b in TYPE2_1ARG:
            self.r.byte(); return f"{TYPE2_1ARG[b]}({self.read_dataobj_raw()})"
        if b in TYPE2_2ARG:
            self.r.byte()
            a1 = self.read_dataobj_raw(); a2 = self.read_dataobj_raw()
            return f"{TYPE2_2ARG[b]}({a1}, {a2})"
        if b == 0x70:
            self.r.byte()
            s = self.read_dataobj_raw(); d = self.read_dataobj_raw()
            return f"Store({s}, {d})"
        if b in TYPE2_3ARG:
            self.r.byte()
            return f"{TYPE2_3ARG[b]}({', '.join(self.read_dataobj_raw() for _ in range(3))})"
        if b == 0x5B:
            save = self.r.p; self.r.p = save
            self.r.byte(); op = self.r.byte()
            if op == 0x12:
                return f"CondRefOf({self.read_dataobj_raw()}, {self.read_dataobj_raw()})"
            if op == 0x21: return "Debug"
            self.r.p = save
        c = self.r.peek(1)
        if c in (b'\\', b'^', b'.', b'/') or valid_nameseg(self.r):
            return name_string(self.r)
        self.r.byte(); return f"?0x{b:02X}"

    def read_dataobj(self):
        v = self.read_dataobj_raw()
        try:
            if isinstance(v, str) and v.startswith("0x"): return int(v, 16)
        except Exception: pass
        return v


def disassemble(path):
    data = open(path, 'rb').read()
    sig = data[:4].decode('latin1')
    tbl_len = int.from_bytes(data[4:8], 'little')
    body = data[36:tbl_len] if sig in ('DSDT', 'SSDT') else data
    d = Disasm(body)
    d.run(len(body))
    return "\n".join(d.out)


if __name__ == '__main__':
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else src + ".dsl"
    txt = disassemble(src)
    open(dst, 'w', encoding='utf-8', errors='ignore').write(txt)
    print(f"{os.path.basename(src)} -> {os.path.basename(dst)}  ({len(txt.splitlines())} 行)")
