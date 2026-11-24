# EC 控制协议（速查）

> 机型：七彩虹 隐星 G16 Pro（广达 Quanta NL5，i9-13900HX / RTX 5070 Ti）。
> 逆向自本机 DSDT 与原厂二进制；**证据、反编译片段与实验数据另见 [`EVIDENCE.md`](EVIDENCE.md)**。本文只保留写代码时要用的结论与红线。

## 一、接口

| 项 | 值 |
|---|---|
| WMI 命名空间 / 类 | `root\WMI` / `RW_GMWMI`（数据块） |
| 实例 | `ACPI\PNP0C14\0x1_0` |
| 数据块 GUID | `{644C5791-B7B0-4123-A90B-E93876E0DAAD}` |
| `_WDG` Object ID | `AA` |
| 读 / 写方法 | `WQAA`（ArgCount=1） / `WSAA`（ArgCount=2，Arg1 = 32 字节 buffer） |
| 事件类 | `GMC_WMIEvent`，`{74286D6E-429C-427A-B34B-B5D15D032B05}`；FN+1 另走 `WMI2Event`（§九） |

调用：① 构造 32 字节 buffer 写入 `RW_GMWMI.BufferBytes` 并 `Put()` → 触发 `WSAA`；② 读 `BufferBytes` → 触发 `WQAA`；③ buffer 在两次命令之间保持，可重复读。

## 二、BufferBytes（32 字节）布局

| 字节 | 字段 | 位偏移 | 位宽 | 别名 |
|---|---|---|---|---|
| 0-1 | `DAT0` | 0 | 16 | **GWF0 = 命令组** |
| 2-3 | `DAT1` | 16 | 16 | **GWF1 = 子命令** |
| 4-7 | `DAT2` | 32 | 32 | GWD0 = 参数/返回 0 |
| 8 | `DT3A` | 64 | 8 | 参数 A |
| 9 | `DT3B` | 72 | 8 | 参数 B |
| 10 | `DT3C` | 80 | 8 | 参数 C |
| 11 | `DT3D` | 88 | 8 | 参数 D（低 4 位 `D3DL` / 高 4 位 `D3DH` → GWD1） |
| 12-15 | `DAT4` | 96 | 32 | GWD3 = 参数/返回 2 |
| 16-19 | `DAT5` | 128 | 32 | 返回 3 |
| 20-23 | `DAT6` | 160 | 32 | 返回 4 |
| 24-31 | `RESV` | 192 | 64 | 保留 |

数值为**小端**（`DAT0 = buf[0] | buf[1]<<8`）。

## 三、命令表（GWF0 = 0xFA00）

| GWF1 | 方向 | 功能 | EC 寄存器 | 参数 |
|---|---|---|---|---|
| `0x0100` | 写 | **键盘背光 RGB** | `LKBC/LKBA/LKBB/LKBG/LKBR/LKBL/WKCL` | GWD0→LKBA, DT3A→LKBB, DT3B→LKBG, DT3C→LKBR, D3DL→LKBL+WKCL |
| `0x0200` | 读 | **系统状态**：CPU/GPU 温度、风扇 1/2 转速、背光档位 | `RTMP, VGAT, OMCC, FS1H/FS1L, FS2H/FS2L, RGBK, LKCL, WKCL` | 无 |
| `0x0201` | 写 | 背光模式 | `RGBK` | — |
| `0x0202` | 读 | 性能模式状态 | `ENTP, AWMI` | — |
| `0x0203` | 读/写 | **显卡模式（GFXT）** | `GFXT` | 读在 `0xFA00` 组；**写在 `0xFB00` 组**，取值 **1/2/3**（§七） |
| `0x0204` | 写 | **HSRE** | `HSRE` | ⚠️ 屏幕高刷开关，非散热策略。DAT2=1→EC 写 0；DAT2=2→写 1 |
| `0x0205` | 写 | **手动风扇转速** | `UFA1, UFA2, UFA3` | GWD0&0xFF→UFA1，GWD2&0xFF→UFA2，GWD3&0xFF→UFA3（百分比） |
| `0x0206` | 写/读 | 风扇控制权 OMCC | `OMCC` | a2：`1` = 软件接管、`0` = 交回固件 |
| `0x0207` | 读 | SYS 路读数（三风扇） | `VGAT, RPM5` | a2 = SYS 温度（1 字节）、a3 = SYS 转速（2 字节），见 §十 |
| `0x0300` | 写 | **性能模式**（DAT2 → APMD） | `PERM, APMD` | DAT2 恒为 {0,1,2} 且与 APMD **透传**；语义以 model.json 为真源：beast=0、game=1、office=2 |
| `0x0400` | 读 | — | 无 EC 访问 | DAT3 |

寄存器语义：`RTMP` CPU 温度、`VGAT` GPU 温度；`FS1H/L`、`FS2H/L` 风扇 1/2 转速（高字节<<8|低字节，RPM）；`UFA1/2/3` 用户风扇百分比（对应原厂 `CPUFanPercent` / `GPUFanPercent` / `SYSFanPercent`）；`PERM` / `APMD` 性能模式；`LKB*` / `WKCL` 键盘背光；`ENTP` / `AWMI` 状态回读。

实测返回样本：

```
写: 00 FA 00 02 00 00 00 00 ...   (GWF0=0xFA00, GWF1=0x0200)
读: 00 FA 00 02 45 … 3B … F5 0C … 24 0C … 02 …
    DAT2=0x45(69) CPU 69°C   DAT3=0x3B(59) GPU 59°C
    DAT4=0x0CF5(3317) 风扇1   DAT5=0x0C24(3108) 风扇2   DAT6=2 背光档位
```

## 四、⚠️ 看着是「读」其实会写 EC

| 命令 | 表象 | 实际副作用 |
|---|---|---|
| `0x0300` | 常被当作「读性能模式」 | **无条件执行 `ECWT(0x00 → APMD)`**，一读就把档位改成安静档 |
| `0x0206` | — | 写 `OMCC` |
| `0x0200` | 读状态 | 内含 `ECWT(0x01 → OMCC)`，属原厂读取流程的伴随操作，必需且无害 |

**轮询与嗅探只可用 `0x0200`、`0x0202`、`0x0203`、`0x0207`、`0x0400`。**

## 五、安全红线

1. **buffer 共享**：连发不同命令可能读到上一条残留。每次读状态前重发 `0x0200`，两条之间留 ≥0.5 s（实测稳定值）。
2. **交回手动风扇不能写 0**：`0x0205` 写 `UFA=0` → 风扇**停转**且 EC 不接管；EC 一旦拿到非零 UFA 就按该值一直转（实测 84 °C 下 30% 锁定数小时不提速）。交回原生自动须**两条命令**：

   ```
   0x0206 ← 0        清除「软件接管」标志（OMCC）
   0x0205 ← FF FF FF 三路 UFA 一并清除
   ```

   `0x0205` 取值语义（逐值实测）：

   | 写入值 | EC 行为 | 备注 |
   |---|---|---|
   | `0x00` | **停转** | 危险，EC 不接管 |
   | `0x01`~`0x64` | 固定占空比 % | 约 50 RPM/%，36%≈1792 / 60%≈3000 / 80%≈4000 |
   | `0x65`~`0xFE` | 钳为 **100%** | 约 12 秒升至 5000 RPM 并稳定 ⇒ 即「最大」，无需重启 |
   | `0xFF` | 手动下 = 钳为 100%；先清 `0x0206` 后 = **交回固件** | 两条命令缺一不可 |

   三种写法实测（固定 30% ≈ 1500 RPM，CPU 72~78 ℃）：仅 `0x0206=0` → 无变化；仅 `0x0205=FF` → **5000 RPM 满转**；先清 OMCC 再下发 255 → 8 秒内 1497→3802 RPM 并随温度自调 ✅。实现见 `EcHal.ReleaseToEc()`。
3. **`0x0206` 读回的 a2 不可作开关判据**：原厂 daemon 运行时恒为 1（每秒写回）。
4. **「高温抢占 UFA」尚未定论**（2026-11-18）：现场现象与「daemon 与本程序同时在写」无法区分；停掉两方后 92~96 ℃ 下手动值仍立即生效。复现须在仅一方写入且 daemon 未运行的环境重做。排障：曲线显示「自动 · CPU 97%」而转速仅 3000 时，先查 ① daemon 是否在跑 ② 本进程曲线是否仍在写。
5. 所有写操作默认关闭，需用户显式开启「实验性 EC 控制」。

## 六、不依赖 EC 的数据源

优先级：**能以 EC 或系统接口读的一律走原生，仅在确无原生路径时用 HWiNFO**。

| 功能 | 接口 |
|---|---|
| 电量 / BIOS 版本 | `root\WMI` → `WMIACPI_Bat1Capacity` / `WMIACPI_BIOSVersion.vBytes` |
| 亮度 | `root\WMI` → `WmiMonitorBrightness` 读、`WmiMonitorBrightnessMethods` 写（仅内建屏；写入同时改当前电源计划的亮度索引） |
| 无线 / 蓝牙 | `root\WMI` → `WMIACPI_WLAN` / `WMIACPI_Bluetooth` |
| GPU 指标 / 功耗墙 | `nvidia-smi`（本机 default / max = 70 / 140 W，`power.limit` 恒 N/A；功耗墙仅留诊断口） |
| CPU 占用 / 频率 / 内存 | `psutil` |
| CPU 封装功耗 | 性能计数器 `\Energy Meter(RAPL_Package0_PKG)\Power`（mW，需连续采样 3 次） |
| 提交量 / 电池功率 | `psapi.GetPerformanceInfo`；`BatteryStatus.ChargeRate / DischargeRate`（mW） |
| 电源计划 | `powercfg /list` `/setactive` |
| ❌ 无原生接口 | PCH 温度、CPU 电压、显存结温、PL1-PL2、SMART ⇒ 仅 HWiNFO；GPU 功耗上限本机硬件不支持写入 |

## 七、显卡模式 `0x0203`

| 项 | 值 |
|---|---|
| 写入组 | **`0xFB00`**（发到 `0xFA00` 只读状态，等于未切换） |
| 参数字段 | `GWD0`（= `DAT2` = `buf[4..7]`） |
| 合法取值 | **1 / 2 / 3**（`0` 不匹配任何分支 ⇒ 空操作） |
| 读↔写 | 读回码与写值一一对应（`SAPD 0x04→1`、`0x01→2`、`0x00→3`）⇒ **可直接写回** |
| 语义 | **1 = 混合输出 / 2 = 独显直连 / 3 = 集显输出**（真机标定，映射存 `presets.gfx.modes`） |

- **写入即完成，生效须重启**：写组把值存入 `R0D5/R0D6`，由固件在下次 POST 读取；界面已注明「点击后无变化属正常」。
- 只读旁证：`sensors.Sensors.display_output()`（WMI `Win32_VideoController`，仅认 `PNPDeviceID` 以 `PCI` 开头的物理显卡，排除 `ROOT\DISPLAY\…` 虚拟适配器）。该值反映上次开机生效的模式。
- 模式 3（集显输出）尚未实机验证。

## 八、背光灯效与调色

`0x0100` 的 `DT3D` 高半字节（`D3DH` → `LKBM`）为**灯效代码**，由 EC 硬件执行动画：

| 码 | 灯效 |
|---|---|
| `1` | 常亮 |
| `3` | 呼吸 |
| `6` | 多彩循环 |
| `7` | 流光溢彩 |

**仅此 4 种**，其余取值（0/2/4/5/8~15）无对应效果，不得再写入新码。注册表值首字节 = `灯效<<4 | 亮度档`（`0x11` = 常亮 + 1 档）。

- **逐帧动画不可行**：三区一帧 308 ms（3.2 fps）且三区停在不同帧（撕裂），gap 压到极限也只有 ~8 fps，属 EC 写入路径的架构上限。软件侧只保留原厂 4 种灯效。
- **一次调色走写批**：`ec.burst_gap`（本机 `0.02`），三区同色约 **140 ms**；写批只放开「写接写」，批后首条读仍等完整写间隔。未写该键的机型一律逐条写。
- **整条灯带 `addr = 6`**：三区同色时发**一条**命令即可（实测 37~112 ms），异色仍逐区写。档案键 `ec.kb_trunk_addr`，未声明不启用；写后须同步三个分区的颜色镜像。
- 只发一个区的 `zones` **不会**裁剪配置：`BacklightZones.Apply(existing, incoming)` 以旧表为基础，只改点名地址。

## 九、FN 热键

**动作在 EC 固件内，PC 侧只事后报告**：`_QB5/6`（背光）、`_QB7`（性能）等 DSDT 方法仅读 EC 并抛 `GMC_WMIEvent`，关闭原厂后 FN 键照常可用，只失去原厂 OSD 与「性能档联动切电源计划」。

| 按键 | 事件源 | 可读信息 |
|---|---|---|
| FN+2 背光 / FN+3 性能 | `GMC_WMIEvent` | `EventDetail[1]`：`0x00` = 背光、`0x07` = 性能（仅识别按键，不含新档位） |
| FN+1 飓风 | **`WMI2Event`**（CBWMI/WMIA 设备） | `[0]` EVID `0x30` = 风扇模式变化；`[0x1E]` = FMAX（1 = 最大，0 = 自动） |

- **FN+3 由本程序切档**（10-19）：识别 `0x07` 后按 `office→game→beast` 取下一档，走与界面按钮相同路径；一次按键抛 3~4 个事件 ⇒ 800 ms 去重；原厂 `ControlCenter.exe` 运行时让位（**不检测 daemon**）。
- **系统模式要镜像注册表**：原厂 `ModeBeforeDC`（0=狂暴 / 1=游戏 / 2=办公）才是持久化真源，只写 EC 时一开原厂就被覆盖。`SetPerfMode(..., mirrorOem:true)` 在写后读回校验通过后同步；拔电自动切办公必须 `mirrorOem:false`；键不存在时不创建。
- **FN+1 不要赌固件 max**：UFA 有一半情况压过它，按键时直接把目标钉到 `max_duty` 并写 EC（`ApplyHwMax`）。

## 十、三风扇（SYS 路）

- **判定**：`ec.fans_by_sku` 取并集 `{0x33, 0x34, 0x35}`（`0x0201` 读 SKU）；`0x0200` 只含 CPU/GPU，SYS 须另发 `0x0207`。
- **读数**：`0x0207` 的 a2 = SYS 温度（1 字节）、a3 = SYS 转速（2 字节），**仅 `ec.fans >= 3` 时读取**；量程校验 0~125 ℃ / 0~12000 RPM，越界整条丢弃。
- **下发**：`0x0205` 的 a2 = CPU、a3 = GPU、a4 = SYS；两风扇机型 SYS 槽写 `255`（忽略）。
- **曲线**：`fan.curve_sys` 为第三条独立曲线（原厂亦如此），三路同帧写。**空 `curve_sys` = 明确清除该路**，HTTP 层必须区分「没带该键」与「显式 `[]`」；界面仅在 `caps.fan_count >= 3` 时显示 SYS。
- **交回原厂**：写 `HKLM\SOFTWARE\WOW6432Node\NL5\ControlCenter` 的 `UserFan1/2/3`、`UserFanN_SYS`、`FanControlStatus=1`、`FanControlSelect`，末尾写 `ControlCenterClose=1` 触发重读；取回时恢复备份 → `FanControlStatus=0` → **再写一次 `ControlCenterClose=1`**。8 个 duty 的温度固定为 `[30,40,50,60,70,80,90,100]`，控制点须重采样。

---

证据与实验数据（原厂字符串、DSDT / daemon 反编译、fps 与耗时实测、待核对项）见 [`EVIDENCE.md`](EVIDENCE.md)；复现工具见其末节 `recon/`。
