# 实证与反编译记录（EVIDENCE）

本文存放支撑 [`PROTOCOL.md`](PROTOCOL.md) 的证据：原厂二进制字符串、DSDT / daemon 反编译片段、真机实验数据。协议速查与安全红线在 `PROTOCOL.md`，此处仅供复核与追溯。

## 一、原厂二进制字符串（静态扫描，2026-10-10）

方法：`recon/binstrings.py` 对 `C:\Program Files (x86)\NL5\ControlCenter\` 做**只读**字符串提取（不执行、不写注册表、不访问 EC）；命中明细仅存本地。

### 1.1 与 EC 通信的组件

| 字符串 | 位置 | 含义 |
|---|---|---|
| `WMIClassRW_GMWMI`、`root\wmi:RW_GMWMI` | ControlCenter.exe | 与本项目同一套 WMI 接口 ✅ |
| `ReadGMWMI` / `WriteGMWMI` / `ReadGMWMI_loopback` | ControlCenter.exe | 读写封装 |
| `GMC_WMIEvent` | ControlCenter.exe **+ Daemon.exe** | 事件监听在守护进程另有一份 |
| `SetFanSpeedWMI` | ControlCenter.exe | 风扇转速经 WMI（对应 `0x0205` UFA*） |

`ControlCenterC64.dll` 仅命中 4 条，不属 WMI 层；内核驱动 `ControlCenter64.sys` 零命中。EC 寄存器名（`UFA1`/`APMD`/`RTMP`…）在 exe 中**均未出现**——只存在于 DSDT，exe 侧只发数值子命令。

### 1.2 `0x0204` HSRE 并非散热策略

原厂证据指向**屏幕高刷（High Screen Refresh）**：`TabDisplayHSR`（位于「显示」页）、`HSR_120_mode` / `HSR_240_mode`、`SetHSROff120UI` / `SetHSROn240UI`、`"HSRStatus = {0}"`、`"HSR Click restart"`、`ic_hsr_120_mode_choose.png`。

推论：状态存于 EC，BIOS 开机读取以决定屏参，**重启后生效**。界面保持禁用。

### 1.3 `UserFan*` 注册表语义（原「待验证」项，已结案）

| 注册表值 | 语义 | 依据 |
|---|---|---|
| `UserFan1/2/3` | `"CPU 段;GPU 段"` **合并串** | `SaveCombineFanDataToStr()`、`ParsingUserFanData()`、`CPUFanPercent[{0}]` |
| `UserFan1_SYS/2_SYS/3_SYS` | **单段**系统风扇曲线 | `ParsingUserFanData_SYS`、`Default_Fan_SYS` |
| `1/2/3` | 界面三组**预设**，非三个风扇 | `Cbx_User_define_fan_1/2/3`、`combo_UserFanMode` |
| CPU / GPU / SYS 三种曲线可选 | — | `Cbx_User_define_fan_CPU/_GPU/_SYS`、`combo_UserFanType` |

`model.probe_factory_specs()` 已据此拆出 `fan_curve_cpu` / `fan_curve_gpu` / `fan_curve_sys`。

### 1.4 原厂曲线的转速计算方式（10-23 反编译实证）

反编译 `ControlCenterDaemon.exe`（.NET，`ilspycmd` 可出源码）的 `MonitorAndControlFan()` / `SetFanSpeedWMI()`。**结论：原厂无「听 CPU 还是听 GPU」选项——两条曲线、两个风扇，各按自身温度调。**

```
CPUFanPercent[8] ← UserFan* 的 CPU 段；GPUFanPercent[8] ← GPU 段；SYSFanPercent[8] ← UserFan*_SYS
每轮：CPUTemperature < 30 则固定 30
  在 Default_Temperature[8] 中查「温度 ≥ 档位值 且 差值 < 10」的首个档位 ⇒ Array_CPU_Num（GPU 同理）
  任一路档位索引变化才写：SetFanSpeedWMI(CPUFanPercent[CPU], GPUFanPercent[GPU],
                                        IsSupport3Fan ? SYSFanPercent[SYS] : 0xFF)
```

`SetFanSpeedWMI` 写 32 字节缓冲（`a0=0xFB00`、`a1=0x0205`）：

| 偏移 | 原厂字段 | DSDT | 落到 EC |
|---|---|---|---|
| 0 / 2 | `a0` / `a1` | GWF0 / GWF1 | 组 0xFB00、子命令 0x0205 |
| 4 | `a2` = CPU 转速 | GWD0（`Store(DAT2, GWD0)`） | **UFA1 = CPU 风扇** |
| 8 | `a3` = GPU 转速 | GWD2（`Store(DAT3, GWD2)`） | **UFA2 = GPU 风扇** |
| 12 | `a4` = SYS 转速 | GWD3（`Store(DAT4, GWD3)`） | UFA3 = SYS（两风扇机型写 0xFF = 忽略） |

（DSDT 的 0x0205 分支：`ECWT(And(GWD0,0xFF) → UFA1)`、`GWD2 → UFA2`、`GWD3 → UFA3`，UFA3 另套 `GGIV 0x080E000E` 判断。档案 `ec.fan_slots = ["d0","a","d2"]` 与之逐格对应。）

本项目自 10-23 起按此实现（`FanCurveEngine`）：`presets.fan.curve_cpu` / `curve_gpu`（含 `mode_curves_*`）两条曲线；CPU 温度→UFA1、GPU 温度→UFA2，两风扇机型不写 UFA3；温度保护墙仍取最高值；`presets.fan.source`（cpu/gpu/max）**已废弃**——原厂无此概念，界面改为「编辑曲线：CPU 风扇 / GPU 风扇」，旧配置单条 `curve` 迁移后同时供两路。

### 1.5 显卡模式的命名线索

`uma_mode` / `hybrid_mode`（"MS hybrid mode"）/ `discrete_mode`，另有 `IsUMAMode`、`SetHybridMode`、`DiscreteToUMA`、`IsSupportDDS`（动态显示切换）。行为见 `PROTOCOL.md` §七。

### 1.6 其他未实现的功能线索

| 原厂字符串 | 可能含义 |
|---|---|
| `get_LED_TRUNK` / `get_LED_LOGO` / `Led_trunk_Click` | **灯条与 Logo 灯**独立控制 |
| `LED_Gaming` / `LED_HighPerformance` / `LED_Custom` / `LED_Audio_SingleLEDZone` | 背光模式枚举 |
| `WMISetLED_palette` / `wmi_led_control` / `LED_POLICY` | 背光调色板 |
| `FUNC_BACKLIGHT0/1/2` | 三档背光，与界面 0/50/100% 一致 ✅ |
| `nvmlDeviceGetFanSpeed`、`NVML_CLOCK_*`、`InitialNVDLL` | GPU 走 NVML，非 EC |
| `ACPIPerformSMI` / `SMI_STRUCT_S` | 另有 **SMI** 通道（未研究） |
| `CHOOSE_SYS_HIGH` / `CHOOSE_SYS_L` / `CHOOSE_CUS_H` / `CHOOSE_CUS_AUDIO` | 风扇 / 音效预设枚举，语义未明 |

### 1.7 独立灯珠（11-18 按同模具抓包定稿）

`0x0100` 的 `d0` 槽位除键盘 A/B/C 外还有独立灯珠。同模具 NL5 的实机抓包：`3=C / 4=B / 5=A / 6=整条键盘 / 7=尾灯 / 8=Logo / 0=全部`。用户确认：**猎刃 16 仅有 Logo（槽位 8），无尾灯**。无真机复测条件时以抓包为准，不再标「待验证」。

本工程仅为 `thunderobot-leiren16` 声明 `ec.kb_extra_lights: {"logo": 8}`，其他档案不声明；这些灯珠不进入 `caps.kb_zones`，不参与 `BacklightZones.Limit/PurgeExtraKbZones`（11-17 把 7/8 误认第 4/5 分区的事故即源于此）。

## 二、三风扇（SYS 路）：原厂 daemon 反编译（2026-11-17）

方法：反编译本机 `ControlCenterDaemon.exe`（.NET，`recon/` 的 dnfile+dncil），涉及 `GetSupportStatus` / `SetFanSpeedWMI` / `GetHardwareInformation` / `GetHardwareInformation2` / `MonitorAndControlFan`。

**① 三风扇判定：用 SKU，与同模具实机样本一致（区间不同）**

```
GetSupportStatus: a1 = 0x0201 读 SKU       // 注意：513 == 0x0201
IsSupport3Fan  = (uint)(sku - 52) <= 1     // ⇒ sku ∈ {52, 53} = {0x34, 0x35}
IsSupporUserFan = (data.a0 >= 512)         IsNewPowerMode = (data.a0 >= 262)
```

另一份同模具实机样本的判据为 `sku ∈ {51, 52}`（0x33/0x34），两者**仅在 0x34 重叠**，本工程取并集 `{0x33, 0x34, 0x35}`（`ec.fans_by_sku`）。G16 Pro 不在其中，本机行为不变。

**② 三路下发：`a2=CPU / a3=GPU / a4=SYS`，两风扇机型 SYS 写 255**

```
SetFanSpeedWMI(cpu, gpu, sys): a0=0xFB00, a1=0x0205, a2=cpu, a3=gpu, a4=sys
  三风扇：SetFanSpeedWMI(CPUFanPercent[i], GPUFanPercent[i], SYSFanPercent[j])
  两风扇：SetFanSpeedWMI(CPUFanPercent[i], GPUFanPercent[i], 255)   ← 255 = 忽略
```

**③ SYS 读数：`0x0207` 的 a2 = 温度、a3 = 转速（同模具实机样本实证）**

```
GetHardwareInformation2(): a0=0xFA00, a1=519(0x0207) → data.a2 → SysTemperature（conv.u1）
ReadSysInfo():             a0=0xFA00, a1=519(0x0207) → sysTemp=(byte)a2; sysRpm=(ushort)a3
```

2026-11-18 反编译同模具的三风扇机型参考实现核对：

```csharp
public bool ReadSysInfo(out byte sysTemp, out ushort sysRpm) { ... data.a1 = 519; ...
    sysTemp = (byte)data.a2; sysRpm = (ushort)data.a3; }
```

该实现**仅对三风扇机型调用**（`_curve.IsSupport3Fan = sku == 51 || sku == 52`），界面直接显示 a2 ℃ / a3 RPM，无二次判断。本工程据此：`ec.sys_layout = {"sys_temp":[4,1], "fan3_rpm":[8,2]}`（相对 `0x0207` 帧）；**仅 `ec.fans >= 3` 时读取**；只做物理量程校验（0~125 ℃、0~12000 RPM），越界整条丢弃；SYS 映射独立，**不依赖主 `layout_verified`**。

**③·补、两风扇机型实测（11-18）：此前误判「a2 是 GPU 副本」的原因**

以 `/api/ec/raw` 同拍对比 `0x0200` 与 `0x0207`（本机，两风扇）：

```
0x0200: DAT2@4(CPU 温度)=90  DAT3@8(GPU 温度)=66  DAT4@12(cpu_rpm)~3900  DAT5@16(gpu_rpm)~2400
0x0207: DAT2@4(a2)=66        DAT3@8(a3)=0          DAT4@12(a4)=0  … 其余全 0
```

两风扇机型上 a2 确实等于 GPU 温度、a3 恒 0，但**参考实现根本不在此类机型调用该命令**，故不能推断三风扇机型上 a2 同为假值。曾据此加 `SysTempTrust` 镜像判定并在快照中删除 `sys_temp`——那会在三风扇机型上连带丢弃真实 SYS 温度（SYS 接近 GPU 温度属正常），**该项已删除，不得恢复**。

**③·补 2、控制权 `0x0206`：接管前写 1**

```csharp
TakeoverFan():  WriteFanControlStatus(1); WriteFanSpeed(cpu, gpu, sys);
ReleaseToEc():  WriteFanControlStatus(0); WriteFanSpeed(255,255,255);
```

`EcHal.SetFans` 在本进程**首次**写 UFA 前先发 `0x0206 a2=1`（`EnsureFanControl`），失败不致命。本机实测（两风扇、daemon 运行中）读回恒为 1，写 0 会被 daemon 立即改回，故在本机仅为幂等确认。

**④ `0x0200` 仅含 CPU/GPU**：`GetHardwareInformation(): a1=512(0x0200) → a2=CPU 温度, a3=GPU 温度`；温度/转速偏移（4/8/12/16）**不含** SYS，第三路须另发 `0x0207`。

**⑤ 独立 SYS 曲线（11-18 落地）**

原厂 `SYSFanPercent[]` / `UserFan*_SYS` 是**第三份独立曲线**，而非「跟随最热一路」。据此补充：

```
fan.curve_sys                 ← 当前生效的 SYS 曲线（缺失 = 不控制，走 EcHal 兜底）
fan.mode_curves_sys[档]        ← 旧位置，ModeBundle.Migrate 迁移至 perf.modes[档].fan.sys
IFanHardware.SysTemp(maxAge)   ← 另一路读数（0x0207 a2），两风扇机型默认 null
FanCurveEngine: duty_sys = curve_sys @ sys_temp，三路同帧写 a2/a3/a4
```

**空 `curve_sys` = 明确清除该路**（三风扇回 `max(CPU,GPU)`，两风扇写 255），故 HTTP 层必须区分「没带该键」与「显式 `[]`」，否则每次保存 CPU 曲线都会静默删掉 SYS 曲线。界面 SYS 按钮仅在 `caps.fan_count >= 3` 时出现。

**⑥ 写进 / 交回原厂（11-18 落地）**

```
HKLM\SOFTWARE\WOW6432Node\NL5\ControlCenter
  UserFan1/2/3     = "cpu0,…,cpu7;gpu0,…,gpu7"
  UserFan1_SYS/…   = "sys0,…,sys7"
  FanControlStatus = 1 开启原厂曲线 / 0 关闭（DAO 每轮据此决定是否写风扇）
  FanControlSelect = 0/1/2 → UserFan1/2/3
  ControlCenterClose = 1 → daemon 的 RegistryKeyChangeEvent 重读曲线并启动 5 秒定时器
```

8 个 duty 对应的温度由固件固定为 `Default_Temperature = [30,40,50,60,70,80,90,100]`（daemon 的 `FieldRVA` 解出），**不得把任意控制点原样写入**，须重采样。

交回：写 `UserFanN` / `UserFanN_SYS` → 备份原值 → `FanControlStatus=1` → `FanControlSelect=N-1` → 停自身引擎（`oem_handoff=true` 拦截自愈）→ 最后写 `ControlCenterClose=1`。
取回：恢复备份 → `FanControlStatus=0` → **再写一次 `ControlCenterClose=1`**（仅写 2 不会重读标志，风扇会继续被控）→ 清标志并重启引擎。

⚠️ 原厂 daemon 全程**只读 `0x0206` 一次**（`DetectAdapterStatus` 据 a4 判适配器），从不写。

**⑦ 系统模式：镜像 `ModeBeforeDC` 后原厂不再覆盖（11-18 真机实证）**

原厂 `Window_Loaded → PowerLineStatusCheck()` 的插电分支会读 `ModeBeforeDC` 并重放 `SetPowerMode(0x0300)`，故仅写 EC 不写注册表时，打开原厂必被旧档覆盖。`ilspycmd` 反编译 `ControlCenter.exe` 的对应证据：

| 位置 | 实证 |
|---|---|
| `GMRegisty.GetRegistry(byte)` | `case 11 → "ModeBeforeDC"`（`REG_MAIN.POWERPLAN`，DWORD，**0=狂暴 / 1=游戏 / 2=办公**），与 `0x0300` 的 APMD 数值相同 |
| `SetRegistry(POWERPLAN, x)` | `power_high/gaming/audio_click_func()` 分别写 0 / 1 / 2 |
| `Window_Loaded` → `PowerLineStatusCheck()` | **插电分支读 `ModeBeforeDC`**，按值调 `power_*_click_func()` → `Set_power_plan_immediately()` → `SetPowerMode(0x0300)`，把 EC APMD **重放为注册表值** |
| FN 性能热键（event byte 7） | 下一档从 `GetRegistry(POWERPLAN)` 起算，不读 EC |
| `Window_Loaded` 开头 | 还按当前激活的 Windows 电源计划 GUID 重放一次 `SetPowerMode` |

**实证**：切档 → 打开原厂 → 显示与 EC 一致、未覆盖；三条用户切档路径均同步该键。边界：拔电自动切办公必须 `mirrorOem:false`（该键是「插回电恢复原档」的基准）；键不存在时静默跳过且不创建；写注册表失败不影响切档判定。`Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\NL5\ControlCenter' ModeBeforeDC` 应等于 `/api/status` 的 `perf.apmd`。

## 三、显卡模式 DSDT 分支

两组同名子命令实现完全不同：

**读组 `0xFA00` / `0x0203` —— `"JJTest1 DisplaySwitch"`（只读）**

```
Store(SAPD, _T_3)
_T_3 == 0x01 → DAT2 = 0x02 ; DAT4 = 0x02
_T_3 == 0x00 → DAT2 = 0x03 ; DAT4 = 0x03 ; Store(0x00, GFXT)   ← 伴随写，原厂轮询亦如此
_T_3 == 0x04 → DAT2 = 0x01 ; DAT4 = 0x01
_T_3 == 0x03 → DAT2 = 0x01 ; DAT4 = 0x01
```

**写组 `0xFB00` / `0x0203` —— `"JJTest2 PrimarySwitch"`（真实写入）**

```
Store(GWD0, _T_8)
_T_8 == 0x03 → R0D5 = 0x00 ; R0D6 = 0xAA
_T_8 == 0x01 → R0D5 = 0x04 ; R0D6 = 0xAA
_T_8 == 0x02 → R0D5 = 0x01 ; R0D6 = 0xAA
```

### 3.1 已修正的问题

`hal.guarded_raw()` 原以 `sub in READ_ONLY_CMDS` 选组，而 `0x0203` 在只读白名单 ⇒ 写命令被发往读组，**不报错、不生效、不入日志**（界面假成功）；`_require_write()` 的 `or cmd in READ_ONLY_CMDS` 还使其绕过守卫。已修复：`_require_write()` 仅按命令组判定；双面命令由调用方显式传 `group`；`LIMITS["gfx_mode"]` 改为 `(1,3)`；`0x0203` 纳入危险命令表并需 `arm`。

### 3.2 生效时机：写入成功但须重启才作用于显示输出（10-11 实测）

1. 界面连点 4 次（模式 1/1/3/2），`op_log` 四条 `ok: true`；
2. 读组直接探 EC：`DAT2 = 2`（= 最后写入值）⇒ 写入链路正常；
3. 显示输出当场无变化；
4. 旁证：写组把值送入 `R0D5/R0D6`（`R0D6 = 0xAA` 似固件控制字），而 `SAPD` 位于 `^^PC00.LPCB.EC0_` 作用域 ⇒ 值留存于 EC，由固件在**下次 POST** 读取。

### 3.3 只读旁证：当前显示输出

独显直连时固件通常在 POST 阶段隐藏核显，混合模式下可见。实现 `sensors.Sensors.display_output()`（WMI `Win32_VideoController`，**仅认 `PNPDeviceID` 以 `PCI` 开头的物理显卡**——`ROOT\DISPLAY\…` 虚拟适配器必须排除，否则误判为「有核显」）。该值反映**上次开机时**生效的模式。首次观测（10-11，EC 码 = 2）：`physical = ["NVIDIA GeForce RTX 5070 Ti Laptop GPU"]`，`igpu = false`。

### 3.4 三档语义（10-11 用户实测标定）

| EC 写值 | 写组副作用 | 语义 | 说明 |
|---|---|---|---|
| `1` | `R0D5 = 0x04` | **混合输出** | Optimus：核显输出、独显按需渲染 |
| `2` | `R0D5 = 0x01` | **独显直连** | MUX 直连，核显被隐藏 |
| `3` | `R0D5 = 0x00` | **集显输出** | UMA：仅核显，独显不参与 |

交叉验证：标定时 EC 码为 `2`，系统中仅见 NVIDIA 独显无 Intel 核显，与「2 = 独显直连」吻合。模式 3（集显输出）尚未实机验证——重启后独显是否从系统消失、`nvidia-smi` 是否读不到数据。

## 四、背光灯效实验数据

### 4.1 软件逐帧动画：不可用

思路：强制常亮（1）后**按帧改** `LKBR/LKG/LKBB`（原实现 `recon/lightfx_experiment.py`）。

| 场景 | 单帧耗时 | 帧率 |
|---|---|---|
| 三区（一帧 3 条，gap 压至 60 ms） | 308 ms | **3.2 fps** |
| 单区（一帧 1 条） | 120 ms | **8.3 fps** |

三区为**逐条依次写**：第 1 条（addr 5）到第 3 条（addr 3）间隔 **205 ms**，期间三区停在不同帧的颜色上（撕裂），叠加低帧率阶跃，人眼直接感知为闪烁。`gap` 压到 ~20 ms 三区也只有约 8 fps ⇒ EC 写入路径的**架构上限**。（补充：三区最好情况一帧 ~140 ms ≈ 7 fps，撕裂依旧；单区最好 ~20 fps。应用级上限更低：`MIN_GAP=0.2` 限为 5 条/秒，实测三区一帧 717 ms ≈ 1.4 fps。）

### 4.2 一处易误判：测 EC 性能须绕开节流

单条 `0x0100` 的 WMI 往返约 **15 ms**，而 `presets.ec.gap` 默认 0.45 s 中约 476 ms 是 `hal._throttle()` 的人为等待。直接测 `set_backlight` 会量得 ~490 ms，据此得出「逐帧刷灯不可行」的**错误**结论；测性能须绕开节流（`set_backlight(gap=…)` / `raw(gap=…)` 可覆盖）。绕开后单条 `0x0100` 实测 **46 ms**（`0x0200` 读 30 ms、纯 WMI 往返 8.4 ms），旧结论 308 ms/帧混入了 `FX_GAP×3 = 180 ms` 的循环 floor。

### 4.3 10-14 复核：无第二条 LED 通道

- `0xFB00` 子命令全量枚举：`0x0100 / 0x0200 / 0x0202 / 0x0203 / 0x0204 / 0x0205 / 0x0206 / 0x0300 / 0x0400`——LED 仅 `0x0100`，**无批量写、无帧缓冲/调色板上传**；
- `0x0100` 分支（@340 起）对 EC 有**两次忙等** `While(ECRD(LKBE) ≠ …)`，单条耗时为固件实际开销，且每帧每分区必须一条命令；
- `root\wmi` 全表仅 `RW_GMWMI` / `GMC_WMIEvent`，无第二个 LED 类；
- 原厂 `WMISetLED4(Zone, Data32)` / `SetLEDModeChangeData` / `LED_POLICY` 落地即**同一条 0x0100 逐分区写**（抓包 `recon/oem_capture.log`：三区 = 三条命令）。

### 4.4 11-15：一次调色的三区连发（`burst_gap`）

用户对照：开「神光同步」时调一个区，其余两个**同时**变化；本程序「全部同色」逐区执行且明显更慢。根因在节流而非 EC：`SetBacklightZones` 逐条经写间隔（0.45 s / `min_gap` 0.2 s），三条命令首末相隔 **1.3 s**。

改法：一次调用内的多条写走**写批**（`GmWmi.WriteBurst`），间隔取 `ec.burst_gap`（本机 `0.02`），三条合计约 **140 ms**（3 × 46 ms）。依据：`0x0100` 分支内部有两次忙等 `While(ECRD(LKBE) ≠ …)`，**上一条未落地时下一条 `Put()` 自身阻塞**，固件已串行化；0.45 s 间隔是为「写后不得立即读」，写批只放开「写接写」，批结束后首条读仍等完整写间隔（`_lastWasWrite` 未改）。档案未写 `burst_gap` 的机型**一律逐条写**（`EcSpec.BurstGap = 0` 为「不支持连发」哨兵）——其他模具未测过 `LKBE` 忙等，不为其放开。

#### 后续修的两处浪费（用户反馈「要闪一会才变到所选颜色」）

1. **未变化的区也被重写**：联动关闭时只改一个区，其余两区颜色未变仍发三条，每条都让 EC 重刷 LED 引擎（可见「闪三下」）。现 `SetBacklightZones` 维护「EC 当前颜色」镜像（`_ecZoneColor`，**仅由成功写入更新**），只发真正变化的地址；档位/灯效变化仍全量写；恢复默认、全部同色、开关灯等需绝对对齐的动作传 `force: true`。
2. **单条命令也等 0.45 s**：写批原仅在 >1 条时启用，日常单区调色最慢。现一次调用（即使一条）均视为一个写批。

实测（`POST /api/ec/backlight`）：单区变更 **49~112 ms**（1 条），三区同色 **~195 ms**（3 条），同 zones 重发 **8 ms**（0 条）。

### 4.5 `addr = 6` 为「整条灯带」（11-15 12:16 真机标定）

`recon/led_all_probe.py`，用户目视确认：

| 写入地址 | 现象 |
|---|---|
| **6** | **三个区同时变化**（整条） |
| 7 / 8 / `0x38` / `0x07` / `0x3F` | 仅左、中两区或部分区，**非全体** |

落地：档案新增 `ec.kb_trunk_addr`（本机 `6`），未写该键则不启用；**所有分区同色**且档案标注整条地址 ⇒ 发**一条** `addr=6`，异色仍逐区写（三条连发）；落盘仍把三个分区写入 `presets.backlight.zones`；写完整条地址后须**同步三个分区的颜色镜像**，否则下次调色误判 B/C 为旧色多发一条。实测三区同色 **37~112 ms（中位 77 ms，1 条）**；偶发 ~520 ms 为后台采样 / 曲线占用 EC 时排队。

> 澄清旧传闻：「发 addr=6 不会覆盖 B/C」不成立——当年只写一条且 B/C 本是其他颜色，看似未变。反之「只发一个区会把 B/C 留旧色」成立，故整条地址仅用于「三区同色」。

### 4.6 只发一个区会把配置裁成单区

`POST /api/ec/backlight` 带 `zones` 时，后端原以请求中的 zones **整表重建** `presets.backlight.zones`——只发 `[{addr:3}]` 就只剩一个区。11-14 采集事故即此症状，11-15 以探针单区请求再次复现。现改为 `BacklightZones.Apply(existing, incoming)`：**以旧表为基础**，只改点名地址，其余原样保留（含 `name`），表内不存在的地址追加。

### 4.7 待核对

`WSAA.dsl` 的 9 份 `0x0100` 分支（3 变体）中至少一份将 `LKBM` 取自 `GWD1` 而非 `DT3D` 高半字节，疑为其他 SKU 分支。以真机实测为准，「`D3DH → LKBM`」存疑。

## 五、FN 热键细节

### 5.1 DSDT 中的热键方法（13 个，仅读 EC，从不写）

| 方法 | 作用 |
|---|---|
| `_QB5` / `_QB6` | 键盘背光：轮询 `LKCL` / `WKCL` → 镜像进 `GWMI.KBBL` |
| `_QB7` | 性能模式：`Store(Add(PERM,One), PERM)`（**在 EC 内循环档位**）→ `GWMI.PEMR=1` |
| `_QB8` | 控制中心事件：`Store(0x8, GWMI.OFST)` + `GWMI.CCHR=1` |
| `_Q20` / `_Q2A` | 电源/电池：`GWMI.OFST=0x20` + `GWMI.APID`，`Notify(BAT0/ADP1)` |
| `_Q60` | 显示切换：读 `SAPD` → `Notify(GFX0.DD1F, 0x86/0x87)` |
| `_Q80` / `_Q8A` / `_Q8C` / `_Q8D` / `_Q90` / `_Q9F` | lid、Nvidia DX、适配器检测 |

**无任何亮度/音量/静音方法** ⇒ 标准键走 Windows 原生 ACPI/HID 栈。每个 `_QBx` 最终都把结果写入 `GWMI.*` 标志位并 `Notify(GWMI, 0x80)` 抛出 `GMC_WMIEvent`；**执行动作全在 EC 固件内**。实测互印：按 Fn+2 后 `0x0200` 的 `DAT6` 由 2 变 1，而 `_QB5` 中无任何 `ECWT`。

### 5.2 `EventDetail` 可用（10-19 逐事件记录）

`EventDetail[1]` 即「哪个 `_QBx` 触发」：`FN+2` → `0x00`，`FN+3` → `0x07`，与 DSDT 完全对应。样本 `01 00 00 00 00 00 00 01 …`（背光）/ `01 07 00 00 00 00 00 01 …`（性能），其余字节恒定无信息。旧结论「无可识别信息」系只见全零样本。仍**不能**从事件读到新档位值。

### 5.3 弹窗延迟：阶梯式差分（10-13 实测 1.57 s → 4 ms）

慢的根因不在渲染，而在**串行的 EC 读取**：早期为「事件 → 唤醒采样循环 → 采集完整帧 → 再补读深字段」，每条间隔 0.45 s，累积 **1567 ms**。

现方案（`HotkeyService.LadderOnce()`，热键线程，持 `_gate`）：

| 级 | 手段 | 耗时 | 覆盖 |
|---|---|---|---|
| 0 | 读 `0x0200` 并与基线差分 | ~20 ms | 背光档位、风扇转速 |
| 1 | 读 `0x0300` 的 APMD | ~0.45 s | 性能模式 |

命中即停；两级均未命中时也把 `0x0200` 原始缓冲差分记入事件列表（FN+3 这类只改 PERM 的按键，此为唯一线索）。无论是否变化都把基线合并为当前值，否则风扇小幅波动会累积、某次越过 200 RPM 阈值而误弹。三处易错点：**锁**（`_gate` 保证「读 EC → 差分 → 弹窗」原子性，取不到锁不阻塞、记补跑标记）；**深读须在锁外读 EC**（持锁读 0.9 s 会阻塞后续事件）；`hal._raw` 内部已有锁，热键线程直接读安全。

### 5.4 连按策略：仅显示最后一个（10-13 定稿）

初版三次 `PostMessage` 各自抢动画（「点快了会被吞」）；排队版依次补播（「没来得及显示就只显示最后一个」）；**定稿：旧条目直接丢弃**。排队不可行的原因：画面会**落后于操作**——已按到第三档，屏幕还在回放第一档。实现：`show()` 先清队列再 put；`_on_show()` 取至空只渲染最后一条；`WM_APP_SHOW` **无条件处理**；`_on_timer()` 结束不再续播；`QUEUE_MAX` / `QUEUE_HOLD` 已删。

### 5.5 FN+1 飓风模式：走 CBWMI（`WMI2Event`）

**FN+1 = EC 固件「飓风模式」= 风扇 Max/Auto 切换**，**不经** `GMC_WMIEvent`，而走另一 ACPI 设备 **CBWMI/WMIA**（`_HID=PNP0C14 _UID=CBWMI`，`_WDG` 首 GUID `ABBC0F20-8EA1-11d1-00A0-C90629100000`）⇒ `root\WMI` 的 `WMI2Event`（`hotkey.CbWmiWatcher` 订阅）。

| 字节 | 含义 |
|---|---|
| `[0]` | EVID。**0x30 = 风扇模式变化（FN+1）**；0x35 = 飞行模式——须按 EVID 过滤 |
| `[0x1E]` | **FMAX = EC 风扇模式寄存器 FANM 的镜像：1 = 最大（飓风），0 = 自动** |

**实测（10-14）**：每次按键抛一事件，`FMAX` 严格 1/0 交替；转速对应 FMAX=1 → 2600→5026 RPM（约 15 s 爬满），FMAX=0 → 回落；**不改 APMD**；原厂 daemon 在跑时另弹其 OSD（双弹，已知取舍）。

**⚠️ 10-20 更正：固件 max 不敌 UFA（同一机器两次相反）**

| 时刻 | 状态 | 按 FN+1 后 | 结论 |
|---|---|---|---|
| 22:32 | 曲线在跑，UFA≈50% | 2600 → 4991 → **5002 RPM** | 固件生效 |
| 22:35 | 曲线在跑，UFA≈52% | 一直 **2600 RPM**，11 秒未动 | 固件**未**生效 |

对照（FMAX=1 且曲线让位、不写 EC 时手写 UFA）：`70%` → 3500 RPM（UFA 生效，压掉固件 max），`100%` → 5002 RPM。⇒ **UFA 至少一半情况胜出**，让位等于把飓风静默关掉。故改为**不赌固件**：`FanCurveEngine.ApplyHwMax(on)` 按键当场把目标钉到 `max_duty`（默认 100）并立即写 EC，退出时曲线未在跑才恢复旧转速；曲线循环 `YieldHw` 分支同样写 `max_duty`（幂等，仅 `LastDuty != MaxDuty` 时写一次）；用 `max_duty` 而非硬编码 100（它同时是温度保护墙上限）。FANM 读不到（WSAA 读组不支持任意地址读），故「谁在管风扇」只能靠转速反推。

### 5.6 「显示多个 / 显示不存在的项」的两个来源（10-13）

**来源一：原厂 `ControlCenterDaemon.exe` 仍在运行（主因）**。关闭界面窗口不等于退出，daemon 驻留后台，按 FN 时弹自身 OSD 与本程序叠加，其内容（原厂档位名）即「显示没有的」。判据：`/api/status` 与 `/api/osd` 的 `vendor` 字段；处理：`POST /api/vendor/stop`（仅由用户点击触发）。

**来源二：本程序弹了不该弹的**：

| 问题 | 原因 | 修法 |
|---|---|---|
| 一个事件弹两条 | 一次按键可能同时改背光、性能与风扇 | 仅弹 `specs[0]`，其余只记 `_osd_dbg` |
| 弹「RPM5 3」/「ENTP 2」 | 深字段是定位 FN+1 用的原始寄存器值 | 深字段**只记不弹** |
| 误弹「风扇 4800 RPM」 | 依据 200 RPM 跳变判定，升温/加载会误触 | 风扇提示默认关（`ui.osd_fan`）+ 性能模式后 15 s 冷却 |
| 连按堆积线程 | 每事件都起线程读深字段 | `_osd_deep_busy` 单例化 |

### 5.7 FN+3 依赖原厂运行的原因（10-13 实测）

**实测**（原厂运行时按两次，读 `/api/osd` 的 `recent`）：`src=event changed={"perf":["beast","office"]}` → `{"perf":["office","game"]}`。`src=event` 说明事件到达时第 2 级即读到新 APMD。

**因果链**：原厂在跑 → `_QB7` 只改 EC RAM 的 `PERM`，**原厂把它同步为 `APMD`** → 本程序读到新档位；原厂已退 → `PERM` 变而 `APMD` 不变 → 所有可读字段（含深字段）都不变，无法识别。

**无法自行同步**：`PERM` 在 EC RAM，可读命令仅 `0x0200 / 0x0202 / 0x0203 / 0x0207` 与 `0x0300` 读组（只 APMD），均不含 PERM；系统内无 EC 直通 I/O 驱动（项目内无 `.sys`；唯一第三方依赖 `tools/HWiNFO/` 走共享内存只读）；GWMI 标志位亦不可用（`EventDetail` 实测恒 0）。

### 5.8 C# 版 FN+3 自切档（10-19）

`EventDetail[1] = 0x07` 可识别「按的是 FN+3」，故不再依赖原厂：读当前 `APMD` → 换算档位键 → 按**界面顺序** `office→game→beast→office` 取下一档 → 走与界面按钮相同的路径（`arm 0x0300` → `SetPerfMode` → 跟随电源计划）。⚠️ 循环方向 10-23 修正：早期按 `(APMD+1) mod 3` 取下一档，实测为**反向切换**，因 APMD 数值方向本身相反（beast=0 · game=1 · office=2）；现只认 `HotkeyService.PerfOrder` 一张表。

一次按键抛 3~4 个 GMC 事件 ⇒ 800 ms 去重；**原厂 `ControlCenter.exe` 运行时让位**（其自身会把 PERM 同步为 APMD，双方同时写会连跳两档）——⚠️ 只检测 `ControlCenter.exe`，**不检测** daemon（实测 daemon 在跑时 APMD 照常不变，依它判断会永久让位）；总闸关闭 / 非管理员 → 提示，不伪装成功。

**代价**：固件 `PERM` 计数与本程序写的 `APMD` 会逐渐错位（PERM 每次 +1，无人读取）。`APMD` 是界面 / 曲线 / 电源计划用的值，目前无可见影响。原厂在 Run 键中**无自启项**；若被重新拉起，顶栏显示琥珀色警告条。

## 六、复现工具（`recon/`）

`recon/` 存放摸协议所用工具，产品代码不引用。**必须在 `recon/` 为当前目录时运行**（脚本用 `sys.path.insert(0, '.')` 与 `from gmwmi import …`，`acpi/` 走相对路径）。

| 目的 | 工具 |
|---|---|
| 连接 EC、发单条命令 | `gmwmi.py`（`BufferBytes` 读 / `Put` + 重取）、`probe.py` |
| 抓原厂真实报文 | `capture_oem.py`（4 ms 采样，过滤自身轮询） |
| 扫描原厂二进制 | `binstrings.py`、`vendor_oc.py`、`qnvoc.py` |
| 反编译 DSDT | `aml_disasm.py`（产物写 `acpi/`，不入库） |
| 数据源可行性 | `nvml_probe.py`、`probe_extra.py`、`probe_e3.py` |
| 一次性探针 | `probes/`（附独立 README） |

协议脚本需管理员（写 EC 必须提权，只读不需要）；HTTP 探针要求控制中心运行于 `127.0.0.1:8765`。本机 curl 经代理访问本地端口会 502，判活用 `curl --noproxy '*'` 或看 netstat。
