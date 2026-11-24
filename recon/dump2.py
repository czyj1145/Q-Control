# -*- coding: utf-8 -*-
import sys; sys.path.insert(0,'.')
from aml_disasm import Disasm
d = open("acpi/DSDT_ALASKA_A_M_I__01072009_00000000_DSDT.aml",'rb').read()
start, end = 0x88AE1, 0x89A8A
body = d[start:end]
dis = Disasm(body); dis.run(len(body))
txt = "\n".join(dis.out)
open("acpi/WSAA.dsl","w",encoding="utf-8",errors="ignore").write(txt)
print("行数:", len(dis.out))
print(txt[:6000])
