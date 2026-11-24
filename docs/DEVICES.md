# 机型适配（DEVICES）

本文说明使控制中心运行于其他广达 / 隐星笔记本所需的步骤与限制。

> 现状（截至 2026-11-14）：
> - **仅 `quanta-nl5`（七彩虹 隐星 G16 Pro）经真机标定**；其余机型走「仅连接」兜底：可识别、可连接，**写入默认关闭**，命令表与温转速偏移按 NL5 逆向结果预置（同一固件家族），开启开关后可读写，读数越界或读不到时退回 HWiNFO。EC 写错会导致风扇停转、屏参改变或功耗墙错乱，故不做推断。
> - 2026-10-18 起提供**用户总闸**（设置页「EC 控制」）：未适配机型默认关闭，用户确认风险后可自行开启。
> - 2026-11-08 起**写验证并入采集**；2026-11-13 起「观察原厂」升级为**五步引导适配**，仅将原厂写命令能证实的项补入 `caps`，未证实项显式 `false`。采集后重启并开启 EC 控制即可用，无需等作者导入。
> - 引导证明的是「原厂如何写入」，`verified` 仍为 `false`；首版边界：**仅对 NL5 负责，其他机型可试用、不承诺可用**。

---

## 一、三档行为

| 档位 | 判定 | 行为 |
|---|---|---|
| **已验证** | 命中 profile 且 `verified: true` | 全功能；EC 控制开关**默认开启** |
| **仅连接** | 命中 profile 但未验证 | 可连 EC、读身份；命令表与偏移借用 NL5，越界或读不到由 HWiNFO 兜底；开关**默认关闭**（确认后可开，按 NL5 语义读写）；顶栏显示「⚠ 写入未验证 · 只读」 |
| **不支持** | 找不到 `RW_GMWMI` | 不访问 EC，仅原生数据源（NVML / PDH / WMI），**不提供开关** |

**读数单独判定**：`ec.layout_verified`。未标定时 EC 层只返回原始字节，不返回 `cpu_temp` / `fan1_rpm`——偏移错等于把某寄存器当温度显示，宁可缺失。

**EC 读不到不等于无数据（2026-11-02）**：`AppState.Readings.cs` 在 EC 缺 `cpu_temp` / `gpu_temp` / `fan*_rpm` 时从 HWiNFO 补齐，显卡温度优先 `nvidia-smi`。三条规则：原生优先（档案带 `layout` 即回 EC 原生读）、只补缺（半标定可用）、**不参与控制**（风扇曲线取 `EcHal.CachedStatus()`，兜底值只进 `/api/status` 与历史）。判据 `/api/status` 的 `device.caps.ec_read`，`false` 即兜底值，散热页标注来源。兜底依赖 HWiNFO（按需模式：刷新时拉起，关窗 60 秒退出）⇒ 托盘常驻时历史曲线会中断，需连续记录则设 `presets.hwinfo.mode = always`；诊断口 `/api/debug/hwinfo` 的 `data.cpu_temp` / `data.gpu_temp` / `data.fans_rpm` 可单独验证。

## 一·五、EC 控制开关（2026-10-18）

```
presets.ec.enabled        Device.EcOverride             Device.WritesAllowed
  null（默认跟随档案） ──▶  null          ──┐
  true / false          ──▶  true/false    ──┴─▶ 档案有命令表 ? (override ?? 档案允许写) : 永远 False
                                             │
              ┌──────────────────────────────┼──────────────────────────┐
              ▼                              ▼                          ▼
      EcHal 的写守卫                 Device.Caps（写类能力全关）     /api/status.device
      （真正拦截的闸门）              界面据此做减法                （顶栏警示条 / 设置页开关）
```

- 真源 `presets.ec.enabled`，**三态**（`null` 跟随档案 / `true` / `false`）；压成 bool 会丢失「沿用该机默认值」语义。
- 配置变更即 `_device.WithEcOverride(...)` 生成新 Device，手改 `model.json` 亦热生效；判定入口唯一 `Device.WritesAllowed`，不存在「界面关闭而后端放行」。
- 未适配机型开启时，界面先做一次风险确认（后端不介入）。关闭总闸时运行中的曲线由 `POST /api/app/settings` 主动 `stop(release=False)`，否则下一轮 `WriteDenied` 自停并刷日志。

## 一·六、一次采集：读写同时验证（2026-11-08 改）

未适配机型获得的不是只读死路，而是**一次操作交齐证据**：

```
关于 › 机型适配
  ├─ 一键采集（完整）→ 弹一次确认 → 采样 20~40 秒 + 写验证十余秒 + 引导五步（≤150 秒，做完即收）
  │                                → 本机档案 + logs\adopt-*.json
  └─ 只读采集        → 不写任何字节，但同样弹五步引导（只 peek + 读 status）
                       ⇒ 机型 / 温度 / 转速 / 分区 / 槽位 / 三档 APMD 均可获取，仅缺写验证
→ 点「提交采集包」（打开提交页，标题摘要已填好、文件已选中）
```

> 改为一次的原因（11-08 之前）：先采集读数 → 用户自行开总闸 → 自行试写 → 再采一次。流程三次跳转，多数用户止步第二步，包内始终只有「读」的证据。现由采集自身写前读原值、写完读回、结束恢复。

**完整采集五项**：

1. **读**：多帧采样 → 判定偏移（`ec.layout`）→ 生成本机档案；
2. **写**（两条，按危险度排序）：`0x0300` 性能档——将 `presets.perf.modes[*].apmd` 在用值逐个写入并读回、恢复原档，同时实测本机三档 APMD；`0x0100` 背光——先写 0 档再写 2 档，各读一帧 `status`，以「两帧差值 = 2 且高档帧取值相符」定位档位偏移（亦识别 `(effect<<4)|level` 写法），恢复原档；
3. **引导适配**（11-13 起，此前为自由观察 60 秒）：先 `Stop(release:false)` 自身曲线，**不写任何字节**，循环 `PeekBuffer` 读取原厂刚下发的报文（原厂写帧仅存活数毫秒，故 0 间隔取样）。**独立置顶窗口**五步（挂于 MainWindow，切页仍置顶），按点击时刻切分帧段并绑定语义；原厂未运行则跳过并记 `skipped`；
4. **不写风扇**：`0x0205` 写入后 EC 持续按该值运转（**写 0 即停转**），仅重启或睡眠唤醒可交回，采集不得使机器停留该状态；
5. **追加 30 秒 CPU 负载**（11-14 起，`CpuLoadProbe`）：3 秒基线 + 30 秒满载 + 5 秒恢复（约 40 秒），每 250 ms 试读一帧 `status`（单帧往返约 0.9 秒）。哪一路转速随 CPU 功耗上升即 CPU 风扇（`FanRoleAdvisor` 重排 `fan1_rpm` / `fan2_rpm`）。仅普通用户态线程（BelowNormal，不改电压频率），期间暂停自身曲线；仅加 CPU 负载，故另一路记为 GPU。**两路同步上升则不判定**（部分固件按整机最高温驱动两路），记「未判定 + 原因」，布局保持偏移升序。
   实测（NL5，11-14）：13 秒版冷机 36.7 W→69.1 W、`status@12` +277 转、`@16` 不动，判定成功；其后两轮（风扇已拉高）仅 +203 / +58 转，无法判定。30 秒版冷机 31.4 W→70.5 W、**+509 转**、33 帧负载样本，判定成功。⇒ 延长至 30 秒把响应从 200 余转提到 500 转，代价是多 20 秒、温度 +3~4 °C；转速响应先于温度响应，故判据取转速。

   **三风扇机型**：最多识别 3 路（`fan1_rpm` / `fan2_rpm` / `fan3_rpm`）。CPU 由负载确定，其余按偏移升序填 fan2 / fan3，并**说明无法区分 GPU 与系统风扇**（缺少 GPU 计算负载通道）。`caps.fan_count ≥ 3` 时散热页多一个 SYS 圈与滑块，顶栏胶囊显示第三个数值；**曲线仍只 CPU / GPU 两条**，SYS 自动时跟随两路较高者（否则落到 `min_duty` 被钉在 30%），手动可单独写。转速取 `ec.fan3_rpm`（两风扇机型恒 null）。

**引导五步**（`关于 › 机型适配`，`Ui/GuideWindow.xaml`，默认 150 秒，完成即收）：

| 步骤 | 用户在原厂界面操作 | 时间窗内帧 | 结论 |
|---|---|---|---|
| 1 办公 / 静音 | 切至最省电档 | `0x0300` 的 D0 低字节 | 办公档 APMD |
| 2 游戏 / 性能 | 切至中间档 | 同上 | 游戏档 APMD |
| 3 狂暴 / 最强 | 切至最强档 | 同上 | 狂暴档 APMD |
| 4 键盘背光 | 换色（多区键盘每区各改一次） | `0x0100` 地址位（D0 低字节） | 分区地址个数 ⇒ `kb_zones`（1~6），名称按地址降序 A/B/C… |
| 5 风扇 | 有手动拉条则拉 40% 停两秒再回自动；仅快捷键则切 Max，起转后回 Auto | `0x0205` 中 1~100 的字节槽 + 每 250 ms 一帧 `status` | 槽位（`d0`/`a`/`b`/`c`/`d`/`d2`）+ 转速偏移与路数 |

> 「风扇」一步分两类结果：写帧给「UFA 填哪几个字节」，同窗口每 120 ms 补读的 `status` 给「转速在哪个偏移」。后者按**变化形态**判定（`FanLayoutAdvisor`）：四位数、变化 ≥200、逐帧平滑（单步最大变化不超过总变化量 55%）——专门排除显卡频率（表现为 210 ↔ 1800 单步跳变）。两路都在变时还要求同步升降（相关系数 ≥0.5），否则只取变化更大的一路。识别到两路才改 `ec.fans` / `caps.fan_count`；只识别一路则保持原值（单风扇与「另一路当时未转」无法区分，改小会隐藏真实风扇）。

> **显卡模式不设步骤**：原厂切换需重启，观察会话无法覆盖。11-14 起改为**采集页询问**（不确定 / 有 / 没有，默认不确定）：选「有」⇒ `caps.gfx_switch = true`（备注「未逐档验证，写入后需重启」）；选「没有」⇒ 显式 `false`；不确定 ⇒ 退回「观察是否出现 `0x0203`」。答复与观察冲突时以答复为准，两者均入备注。同时两次读 `0x0203`（间隔 0.6 秒）并枚举物理显卡写入 `gfx_probe`：读到 2 且无核显、读到 3 且无独显则与固件行为一致；读到 1 无法区分「混合」与「无 MUX」，**任何读数均不单独开启能力**。仅见 0 / 100 / 0xFF 等预设时注明原厂无手动拉条。

**可自动补齐的能力**（`AdoptGuide`，纯逻辑、含单测）：

* 三档 APMD **齐全**才写 `caps.perf_modes` 并写入 `presets.perf.modes.<key>.apmd`；缺档只记已确认项、**性能页保持关闭**（按钮点击即写 EC，缺档可能写到别的档）；
* 键盘按地址个数开启背光页（1~6 区），地址写入 `presets.backlight.zones` 与 `zone_addrs_confirmed`；按 `caps.kb_zones` 绘页签，超过 6 个才不开启（更像把 logo / 灯条计入）。三区沿用真机标定边界（A ≤6u / C ≥13.5u），其他数量按色数均分——原厂只下发地址不下发位置，图示为**示意**；
* 风扇仅当槽位**与通用档案一致**（`d0`/`a`/`d2`）才开手动与曲线；不一致均不开（错槽位会导致一路满转、另一路不动，难以归因）；`0xFF`（原厂「忽略该路」）不计为槽位；**从不自行试写** `0x0205`；
* **未确认项显式 `false`**：`Device.Caps()` 缺键时按命令表推导，留空等于开总闸后自动出现；
* 弹窗「抓到 N 条」**只统计当前步骤的子命令**（`CountsForStep`）。原厂自动曲线每数秒写一次 `0x0205`，一并计数会显示「抓到 1 条」，用户误判成功（11-13 真机曾发生）；当前步骤 0 条时点「完成这步」需二次确认；
* `0x0100` 中**亮度 0 且 B/G/R 为哨兵值**（0/1/255）的帧是原厂打开灯效页时的熄灯 / 初始化帧，地址 7、8 并非键盘分区（曾因此算出 5 区、背光页被关）。此类地址不计分区，写入 notes，完整地址留在 `observation.summary.kb_zone_addrs`；
* 风扇帧同时出现多路 100% 记为 **Max 预设**；出现 ≥3 个不同中间转速则注明「更似原厂自动曲线周期下发」，手动能力由槽位一致性推定，报告不表述为「已确认手动操作」；
* **layout 只认 `status` 帧**（11-16，`LayoutFrames`）：`layout` 只有「字段 → (偏移, 长度)」，**不含来源命令**，运行时 `ReadStatus` 恒读 `status`；从 `0x0202` / `0x0207` 识别的偏移会被当 status 同偏移读出。真机现场：`gpu_info@4` 被写成 `gpu_temp: [4,4]`，与 `cpu_temp: [4,4]` 撞槽，界面显卡温度显示 CPU 温度且不报错。现偏移推断、转速形态、风扇归属三处只采用 `status`（无 status 的离线夹具才退回全部命令）；
* **偏移先查标准槽，认不出才查尾部**（11-16）：先扫 DAT2..DAT6（@4~@20），一路温度都未识别才补扫 DAT7/DAT8（@24/@28）；同名字段以标准槽为准；写验证的档位定位同理（`EcWriteProbe.TailSlots`）；
* **观察到的地址不等于分区数**（11-16）：原厂改色会把 logo 灯 / 灯条一并写 `0x0100`（本机 7/8）。规则：配置中**确认过的**地址（`zone_addrs_confirmed`）本次全部出现时，多出的不计分区、只入 notes；反之（用户只点一个区）不裁剪，否则「四区键盘只点两区」会被永久锁定。未确认配置（含默认值）不得作白名单；
* **温度对齐容差「多数帧差 ≤4 °C、中位差 ≤3 °C」**（11-17 由 2 °C 放宽）：EC 报平滑值、HWiNFO 报瞬时值，常有 2~4 °C 固定偏差（实测 EC 恒 68、HWiNFO 64~67 偶跳 78/80）。逐帧相等会误判真实温度、写不出 `cpu_temp`；「与另一路参照至少差 4 °C」的互斥判据保留；
* **未开启原厂时的完整采集不清空能力表**（11-17，`GuidedCaps`）：`guide == null` 时草案照抄 generic 的 `kb_zones=0` / `perf_modes=[]` / 风扇全关，会清掉上次确认的整套能力（曾发生 `kb_zones` 3→0）。现规则：旧草案 `adopt.guide.findings` 至少一项 `value != null` 时沿用其 `caps` / `presets_overrides` / `ec.fan_slots`，layout 与写验证仍用本次实测，回执写 `profile_inherited_caps`；
* 各步**原始报文**去重写入 `observation.guide_evidence`（同时进 `profile_draft.adopt.guide.frames`）：`0x0300` 是否有其他字节随档位变化、背光位与档位的拼接方式、风扇一帧写哪几路。`AddPerfByteNotes` 发现 d0 之外字节随档位变化时点名（这类机器表现为「档位切换但功耗墙不变」）；
* 原厂实际写入的槽位记入 `profile.ec.fan_slots`（不限于 caps）：不一致时 `fan_manual` / `fan_curve` 仍关，但保留真实槽位，免得作者只知「不一致」不知具体字节；
* 风扇归属来自 CPU 负载段：仅一路明显响应则排 `fan1_rpm`，另一路记 GPU；同步上升时保持偏移升序并写明原因；
* 转速（`fan1_rpm` / `fan2_rpm`）来自「风扇」步骤的 `status` 抽样（250 ms，只读；更密会被 200 ms 读节流阻塞而漏写帧），仅风扇真正起转时可识别，无法识别时写原因（未变速 / 采样不足 / 不在 status 帧）；
* **时序与回显**（`EcTimingProbe`，11-15，纯读）：对每条 `read_only` 命令连读两次，回帧前 4 字节为 `00 FA + sub` 者记 `ec.echo_verified`（本机仅 `0x0200` 回显）；再按 0/5/10/20/50 ms 各连发 **20 次**，取首个零失败档 +10 ms 作 `ec.read_gap` 建议值。两项只进草案：偏小最坏是脏帧被回显校验拦下重试，偏大则每帧空等（0.45 s 时代整帧 515 ms 中 90% 是等待）。结论同时进包内 `timing` 段与报告；
* **写批间隔 `ec.burst_gap`**（同一自检，**仅完整采集**）：对键盘分区连发三条**同档位同色**的 `0x0100`（外观不变），从 2 ms 起找首个「三条都不抛异常 + `status@20` 读回一致」的档位，建议值 +10 ms（上限 50 ms）。**不以耗时判定**：`0x0100` 每条要等固件刷 LED 引擎（实测 27 ms → 170 ms/条），该开销与间隔无关，按耗时判会误杀所有档位；
* **整条灯带 `trunk_addr`** 必须**人工观察键盘**确认（EC 只接受写入、不回报分区颜色）：`EcProbe.exe --trunk-probe [--trunk-addrs 6,7,8,56]` 逐个点亮并确认，写入 `presets.backlight.trunk_addr`（**配置优先于档案**，见 `EcHal.TrunkAddr`），该机型立即由「三条连发」切为「一条命令」。

无法获取或对应的项一律不推断：写入 `adopt.guide`、`adopt.missing` 与报告「本机尚未确认」，由作者依原厂抓包复核（`--import-pack` **不重新推导 caps**，仅写 notes / 警告）。

`verified` 仍为 `false`：引导证明「原厂如何写」，不证明「本程序写得对」。用户采集后可点「启用本机」（等同总闸 + 重启），风险自担；作者复核后以 `--allow-write` 转正式档案。

采集包 `observation` 段：

| 键 | 含义 |
|---|---|
| `started` / `ended` / `duration_s` | 观察窗口起止与时长（跳过时全 0） |
| `skipped` | 仅跳过时出现：未观察原因（例：原厂控制中心未运行） |
| `peek_count` | 缓冲区读取次数（未抓到写命令时说明确实在监视） |
| `write_cmds[]` | 抓到的写帧：`t` / `last` / `gwf0`（`0xFB00`）/ `gwf1` / `raw` |
| `fan_paused` | 观察期间是否暂停本程序曲线 |
| `marks[]` | 用户点击标记：`step` / `at`（毫秒）/ `skipped`，**切分窗口的唯一依据** |
| `guide[]` | 逐段结论：`step` / `title` / `marked` / `skipped` / `frames` / `value` / `why` |
| `guide_evidence` | 各步原始报文（去重）：`perf.<档>.raw/nonzero`、`keyboard[]`、`fan[]`、`fan_slots` |
| `fan_samples` | 「风扇」步骤的 `status` 帧数（0 = 未执行或未变速） |
| `summary` | 可确定项（`perf_apmd_values` / `kb_zone_addrs` / `kb_effects` / `kb_levels` / `gfx_values` / `fan_slots`） |
| `conclusion` | 逐项结论（含地址与取值），供作者查阅，不下「已适配」定论 |

采集包顶层另有 `gfx_probe`（11-14，纯读，不写 EC、不重启）：

| 键 | 含义 |
|---|---|
| `ok` / `mode` / `dat4` / `raw` | `0x0203` 当前值、DAT4 与原始 32 字节（读不到时 `ok=false`、`mode=null`） |
| `stable` | 两次读数是否一致（WMI 缓冲共享，间隔 0.6 秒） |
| `reads[]` | 两次的原始返回值 / 错误 |
| `igpu` / `has_dgpu` / `dgpu` / `physical` | 可见物理显卡（`ROOT\DISPLAY\…` 虚拟适配器不计） |
| `verdict` | 一句话结论：与固件一致 / 与当前模式不符 / 读不到 |

> 仅作**佐证**：`caps.gfx_switch` 由采集页三态或「观察抓到 `0x0203`」决定；读到 1 无法区分「混合输出」与「无 MUX」。

写入窗口由 `EcHal.OpenWriteProbe(ttl, subs)` 开启：**仅绕过「机型/总闸」一道门**（白名单 + 90 秒 TTL，采集结束关闭），管理员与高危 arm 照常判定；非管理员整段跳过并说明。

`write_test` 段：

| 键 | 含义 |
|---|---|
| `mode` | `probe`（完整）/ `readonly`（未写入） |
| `perf` | `before` 原档 · `steps[]` 每值 `wrote → readback → effective` · `restored` |
| `backlight` | 写入两档 · `found_offset` · `frames` 逐帧原始 u32 · `restored` / `restored_level` |
| `fan` | 恒 `attempted:false` + 原因 |
| `gate` | 本次放行的写命令与 TTL |
| `log.items[]` | 每次写尝试：时间、命令号、参数（仅打印非零槽）、`ok`/`denied`/`error`、EC 回帧 |
| `log.kept` / `dropped` | 保留 40 条，更早滚出（超出时如实标注） |
| `verdict` | 只描述现象 |

三种「写不上」对应不同根因，**不可混读**：`denied` = 一个字节未发出（无管理员 / 开关未开 / 未 arm / 未适配）；`error` = 已发出但往返失败（WMI 异常 / 长度不符 / 超时），EC 未收到；`ok` 但**读回不符** = 命令被接受而寄存器未变（该子命令在别处并非此功能，或固件又做了一层映射），只能靠读回发现。读回一致也只证明「命令生效且未被重映射」，**不能证明语义正确**，故 `--import-pack` 不会因读回一致而放开写权限。

**只读采集的边界**（对方不接受写 EC 时）：

| 可获取 | 来源 | 结果 |
|---|---|---|
| 机型识别 | `identity` + `vendor_candidates`（注册表项目号） | 生成按项目号匹配的正式档案，同模具共用 |
| 温度偏移 | 20~40 秒采样 + 相关性（对 CPU 功耗 / 显卡温度） | `layout_verified=true` ⇒ 重启后为 EC 原生读数，不再依赖 HWiNFO |
| 风扇转速偏移 | **只读采集取不到**：来自「风扇」步骤抽样，需拉起风扇而只读采集不执行引导 | 需一次**带引导的采集**（`probe:false, observe:150`），不写任何字节 |
| 哪一路是 CPU | 负载段转速响应（只读采集**也执行**：仅普通线程，不访问 EC） | 判定后 CPU 排 `fan1_rpm`；同步上升则如实不判 |
| 背光档位偏移 | 采样期间键盘灯有变化即可识别（0~2） | 只说明读数对齐，不代表可写 |

缺的只有写权限：`verified` 恒 false、`caps` 照抄 generic ⇒ 曲线 / 性能档 / 调色 / 显卡模式均关闭。**只读包并非「证据不全、无法使用」**。

补充写权限的途径（**均不需逐台试写**）：

1. **同模具有一台愿试写** —— 写命令为模具级：同项目号命中同一档案，`--allow-write` 后对整批生效；
2. **对方改变决定** —— 再跑一次「一键采集」（写验证十余秒，结束恢复原档），包为增量；
3. **作者依原厂行为确认** —— `vendor_processes` 存在原厂进程时，依其动作（抓包 / DSDT）确定 `apmd` 映射、RGB 分区与 UFA 槽位；
4. **停留在只读** —— 合法终点：机型可识别、读数准确、写入关闭；非 NL5 机型的写功能不作承诺。

旧版采集包无 `write_test` / `probe`：`--import-pack` 给警告（「写入路径无证据」）但不阻塞导入，开写权限前应以新版重采（现为一次）。

---

## 二、识别机制

按 `devices/*.json` 的 `priority` 由高到低匹配，首个命中生效。`match.any` 是一组「候选证据块」，块内字段全须命中，字段内多条正则为「或」。可用字段（均只读、无需管理员）：

| 字段 | 来源 | 说明 |
|---|---|---|
| `system_vendor` / `system_product` / `system_family` | `HKLM\HARDWARE\DESCRIPTION\System\BIOS` | OEM 常三处同填营销名（本机均 `G16 Pro`），**单独用不可靠** |
| `board_vendor` / `board_product` | 同上 | 同样会被覆盖 |
| `bios_vendor` / `bios_version` | 同上 | 随 BIOS 版本变，仅辅助 |
| `vendor_registry` | `HKLM\SOFTWARE[\WOW6432Node]\<项目号>` | ★ 最可靠：原厂必须写该键，项目号即模具代号（本机 `NL5`） |
| `acpi_pnp0c14` | `HKLM\SYSTEM\CurrentControlSet\Enum\ACPI\PNP0C14` 子键 | 仅证明存在 ACPI-WMI 设备，所有厂商都有，弱证据 |
| `wmi_classes` | `root\WMI` 中真实存在的类名 | 需起 COM（~100–300 ms），仅轻量识别未命中时探测 |

`quanta-nl5` 的规则：

```json
"match": { "any": [
  { "vendor_registry": ["^NL5$"] },
  { "system_product": ["^G16\\s*Pro$"], "system_vendor": ["COLORFUL|七彩虹"] }
] }
```

两条任一命中即可：一条认项目号（最准），一条认机型名（原厂软件被卸载时的退路）。

## 三、新增机型（标准流程）

> 11-08 起写入验证并入采集，11-13 起末尾升级为「引导适配」：五步窗口按标记切窗并绑定 APMD / 键盘分区 / 风扇槽位 / 显卡语义，仅将**已证实**项补入 `caps`（见「一·六」）。
> 入口：「关于 › 机型适配」→ **一键采集**（`POST /api/app/machine-report {adopt:true, probe:true, observe:150}`，`observe` 为观察秒数，0 表示不观察，界面默认 150，范围 10~300，五步完成提前收尾）。用户无需命令行，也无需空载 / 负载各跑一次：
>
> - 采样 20~40 秒（16 轮，跟随每秒快照节拍；每帧 = `status` + 同刻 CPU 功耗 / 显卡温度 / EC 温度）；
> - 自动判定偏移：与 CPU 功耗或显卡温度相关性 ≥0.75 即对应温度（`Core/Devices/EcLayoutAdvisor.cs`，含单测）；
> - 验证写入：APMD 逐值写→读回→恢复；背光换两档→读回定位→恢复（`App/MachineProbe.cs` + `Core/Devices/EcWriteProbe.cs`）；
> - 引导五步：置顶窗口按标记切窗、绑定语义并补 `caps`（`Core/Devices/AdoptGuide.cs`，含单测）；确认的 APMD 与分区地址同时写 `model.json`；
> - 写 `devices/<id>.json` 至 `%ProgramData%\Q-Control\devices\`，重启生效；
> - 生成采集包 `logs/adopt-<机型>-<时间>.json|.txt`：报告 + `profile_draft` + `samples` + `write_test` + `observation`。作者 `--import-pack` 一条命令转正式档案。
>
> 三条固定规则（`DeviceProfileDraft`，调用方不得改）：`verified` / `writes_allowed` 恒 **false**；无法确认的偏移**一个都不填**；`priority` 固定 50（低于具体档案 100、高于 generic 10），**不会覆盖内置已验证档案**（10-31 曾因一次未识别温度的采集把本机标定覆盖成空布局）。
>
> 报告不含：序列号 / UUID / MAC / IP / 计算机名 / 用户名 / 路径。

### 0. 收到采集包之后（11-07 起，一条命令）

「一键采集」产出两个产物：

| 产物 | 位置 | 面向 |
|---|---|---|
| 本机档案 | `%ProgramData%\Q-Control\devices\<id>.json` | 用户本机（重启后只读生效） |
| **采集包** | `logs\adopt-<机型>-<时间>.json`（+ `.txt`） | **作者**：结论与证据 |

> **档案清理**（11-07）：「关于 › 日志 › 占用空间」分别报「日志 N 个」与「机型档案 M 个」，清理框可选 **仅清日志 / 日志与档案一并清理**（默认仅日志）。删除后需**重启**才回落到内置档案。源码仓库的 `devices\`（旁有 `QControl.slnx` / `.git`）后端**拒绝**清理（该目录为手工维护的真源，界面不提供选项）。

采集包整合原先分散三处的内容：`profile_draft`（草案）、`samples`（每帧 5 个偏移原始值 + 同刻 CPU 功耗 / 温度 / 显卡温度）、`write_test`（写入值 / 读回值 / 定位偏移 / 是否恢复）、`observation`（写验证后只读抓取的原厂写命令 + `skipped`）、`fan_rpm`（转速标定）、`fan_roles`（负载段：基线 / 负载功耗与温度、判定与未判原因）。草案是结论，采样是依据——有逐帧数据才能复算；`write_test` 说明写路径是否可用（只读采集写 `mode:"readonly"`）；`observation` 说明「原厂如何写」；`fan_rpm` 说明「转速在哪」（按形态判，故独立成段）；`fan_roles` 是唯一的**因果**证据（自造负载）。

收到包后：

```bat
QControl.exe --import-pack <采集包> [--out <目录>] [--dry-run] [--allow-write] [--force]
```

| 参数 | 作用 |
|---|---|
| `--out` | 输出目录（默认当前目录下 `devices\`） |
| `--dry-run` | 只看结论，不写文件 |
| `--allow-write` | 生成 `verified=true`，**仅在复核原厂写行为后使用** |
| `--force` | 覆盖同名档案（默认拒绝） |

**自动执行**：id 沿用、`priority` 提到 100、以包内 identity 当场跑 match 自检、按证据齐全度列警告（layout 为空 / 无项目号 / 采样过少 / 缺显卡参照 / 写验证未执行或未对上 / 观察未完成），并列出 caps 缺口。写验证结论写入 `adopt.import.write_test` 与 `adopt.import.probe`；观察结论写 notes / 警告，caps 沿用草案——两者均仅作证据，不据此开写权限。

**不自动执行**：开写权限、确认 caps（风扇路数 / MUX / RGB 分区）、确认三档 APMD **语义**。`verified` 恒 false，包内自称的 `verified` / `writes_allowed` 不采信。**风扇写入始终「未验证」**。**拒绝条件**（退出码 2，不写任何字节）：match 自检失败 / id 与内置已验证档案同名 / 缺 `profile_draft`（旧包）/ 非采集包。退出码：0 成功、2 包有问题、3 目标已存在或写盘失败。

> ⚠️ 在**源码目录**运行调试实例（`--port 8799` 且存在 `model.json`）时，档案目录为 `ControlCenter\devices\`，采集本机会覆盖 `quanta-nl5.json`；恢复用 `git checkout -- devices/`。

### 1. 获取机型身份与 EC 原始帧

**A. 用户执行（推荐）**：「关于 › 机型适配 › 一键采集」（会写两次 EC 并恢复，需确认一次），采完界面直接定位到采集包，提交即可。**一次即可**，无需补采、无需开总闸。若对方不接受写 EC，改点「只读采集」，包内只有「读」的一半。

**B. 本机探针**（默认只读）：`src\p3-probe.cmd` 输出身份指纹 → 命中档案 → 每条读命令的原始 32 字节 → `layout` 解析值。探针不生成档案，需要时用界面「一键采集」或复制 `devices/quanta-gmwmi-generic.json` 改。

**先看报告里的 `vendor_candidates`**：原厂必在 `HKLM\SOFTWARE[\WOW6432Node]\<项目号>` 写键（本机 `NL5`，子键仅 `ControlCenter`），含该子键的短键名即模具代号，写入 `match.vendor_registry`。

### 2. 标定 `ec.layout`（一键采集已代劳，手工方式如下）

偏移是推测值，**只能靠「什么在变化」判定**。无界面时在两种负载下各采一次：

```bat
src\p3-probe.cmd > idle.txt     :: 空载
:: 烤机 / 游戏 5 分钟
src\p3-probe.cmd > load.txt     :: 负载
```

（用界面时：空载导出报告，烤机 5 分钟后再导一次，对比两份 `adopt-*.json` 的 `ec.frames[].u32`。）

对比：~50 → ~90 且幅度与 CPU 温度相符 ⇒ `cpu_temp`；~45 → ~75 ⇒ `gpu_temp`；~2000 → ~4500 ⇒ `fan1_rpm` / `fan2_rpm`（两路都变，哪路 CPU 需与原厂对照；本机 fan1=CPU / fan2=GPU 为沿用假设，历史曲线两路同画，差值不符即可发现）；仅在 0/1/2 间跳 ⇒ `backlight_level`。

```json
"layout": { "cpu_temp": [4, 4], "gpu_temp": [8, 4],
            "fan1_rpm": [12, 4], "fan2_rpm": [16, 4],
            "backlight_level": [20, 4] },
"layout_verified": true
```

> 至此温转速可显示，**但仍不可写**（`verified` 仍 false）。只读收益立即可见，写权限留到最后。

### 3. 标定 `caps`

| 字段 | 确认方式 |
|---|---|
| `fan_manual` / `fan_curve` | 原厂能否手动调风扇？能则试 `0xFB00/0x0205` |
| `perf_modes` | 原厂档数；本机 `office/game/beast` ↔ `apmd 2/1/0`，**其他机型映射可能不同**，须抓包或 DSDT 确认 |
| `gfx_switch` | 有无 MUX 开关？无则不写 `0x0203` |
| `keyboard_rgb` / `kb_zones` | 分区数（1~6）与区号地址（本机 3/4/5 为**倒序**） |

同时改 `presets_overrides` 的本机默认值：

```json
"presets_overrides": {
  "perf":  { "modes": { "office": {"dat2": 2, "apmd": 2, "plan": "办公"} } },
  "backlight": { "zones": [ {"addr": 5, "rgb": [255,0,0], "name": "A"} ] }
}
```

### 4. 最后开放写权限

把 `verified` 改为 `true`——等于允许程序向本机 EC 写字节，仅前三步验完后执行。建议先在原厂软件记录各模式与灯效状态，便于写错时恢复。

### 5. 回归

```bat
dotnet test tests\QControl.Tests -c Release --filter GoldenDevice   :: 识别 + 未验证机型拦截 + 默认值注入
```

### 6. 预览未适配机型的界面（11-02 起）

改「未适配机型」界面时通常手上只有已适配机型，不预览只能靠猜。强制指定档案，**只读**：

```bat
QControl.exe --device quanta-gmwmi-generic --ui-shot preview.png --page sys
set CC_DEVICE=quanta-gmwmi-generic && QControl.exe       :: 常驻实例同样适用
```

- 仅接受 `devices/*.json` 中存在的 `id`，写错视为未设置；
- **强制档案一律按未验证处理**：`verified` / `writes_allowed` 压 false，机型名附「（强制预览）」；
- 用于验证「其他机型所见」：EC 原生温转速缺失（HWiNFO 兜底）、风扇曲线整块消失、写入口关闭，仅留「EC 已连接」。

## 四、红线

1. **不得在 `verified: false` 的档案中填推测的 `layout`**，宁可只读。
2. **不得仅凭 SMBIOS `system_product` 识别机型**，OEM 会把整批机器填成同一名称。
3. **不得为让界面「看起来可用」提前把 `verified` 置 true**，那是拿他人硬件试错。
4. **不得改 `src/QControl.Core/Model/CmdTable.cs` 的默认命令号**：仅为「无档案」时兜底，真机一律用档案 `ec.cmd`。
5. 新机型的 `dangerous` / `confirm_required` 须重新确定：其他模具上 `0x0300` 未必是性能模式。
6. **不得把未适配机型的「EC 控制」默认值改为开启**。默认值是唯一的安全表态，需试用的用户自行开启，程序不代为决定。

## 五、代码地图

| 文件 | 职责 |
|---|---|
| `Core/Devices/*.cs` | `DeviceMatcher`（纯逻辑可离线测）、`Device`（不可变，写许可唯一判定口）、`EcSpec` |
| `Core/Devices/DeviceProfile.cs` | 档案加载：嵌入 `devices/*.json` + 用户目录（同 id 覆盖、priority 下限 50） |
| `Hal/Wmi/MachineIdent.cs` | 身份采集；`ScanVendorCandidates()` 盲扫 `HKLM\SOFTWARE` 中形似项目号的短键（**仅供报告**，不参与匹配） |
| `App/MachineReport.cs` | 适配报告：只读采集（身份 / 候选项目号 / EC 只读帧 / 硬件）+ 人读文本 + 落盘 |
| `App/MachineAdopt.cs` | 一键采集：多帧采样 + 生成本机档案 + 结论与逐帧证据写入采集包 |
| `Core/Devices/PackImport.cs` | 采集包 → 正式档案（纯逻辑）：优先级、match 自检、证据齐全度、caps 缺口 |
| `App/PackImportCli.cs` | `--import-pack`：读包、打印复核清单、写 `devices\*.json` |
| `Core/Devices/EcLayoutAdvisor.cs` | 偏移推断（纯逻辑）：相关性 + 值域 + 保守阈值；标准槽认不出才扫 @24/@28 |
| `Core/Devices/LayoutFrames.cs` | 可参与推断的帧（**仅 status**），防其他读命令偏移混入（纯逻辑） |
| `Core/Devices/GuidedCaps.cs` | 本次未开原厂时沿用上次带引导草案的 caps / 预设 / 槽位（纯逻辑，含单测） |
| `Core/Devices/DeviceProfileDraft.cs` | 由采集结果生成草案（写入恒关、只填确认项） |
| `App/AppState.cs` | `Identity()`（缓存身份）、`ExportMachineReport()`（`POST /api/app/machine-report`） |
| `App/Ui/Views/AboutView.xaml(.cs)` | 「关于 › 机型适配」卡片：`导出报告` / `复制全文` |
| `EcProbe/Program.cs` | 只读探针：身份 / 档案命中 / 每条读命令原始帧 / 守卫拦截验证 |
| `devices/*.json` | 机型档案（唯一真源：命令号、偏移、能力、默认值） |
