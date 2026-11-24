# Q-Control — 广达（Quanta）笔记本控制中心

<!-- 主仓 GitHub（公开），Gitee 为同步镜像；改版本号时同步 release 徽章与 ControlCenter/VERSION -->
[![Build](https://github.com/czyj1145/Q-Control/actions/workflows/ci.yml/badge.svg)](https://github.com/czyj1145/Q-Control/actions/workflows/ci.yml)
[![Release](https://img.shields.io/badge/release-v0.1.0-2ea043)](https://github.com/czyj1145/Q-Control/releases)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-0078d4)](#下载)
[![.NET](https://img.shields.io/badge/.NET-10-512bd4)](#下载)
[![Gitee](https://img.shields.io/badge/Gitee-镜像-c71d23?logo=gitee&logoColor=white)](https://gitee.com/czyj1145/Q-Control)

> **English:** An unofficial EC control center for Quanta `RW_GMWMI` laptops, replacing the vendor's `NL5\ControlCenter`. Validated on the Colorful 隐星 G16 Pro (NL5); the Thunderobot 猎刃 16 (same chassis, plus a third SYS fan and a logo light) and the Hasee 战神 T8/T10/T11/T12 Pro share the same EC layout. Read-only elsewhere. Protocol reverse-engineered from stock firmware — see [`recon/`](recon/). Use at your own risk.

面向广达 `RW_GMWMI` 机型的自研控制中心，替代原厂 `NL5\ControlCenter`。真机标定机型为七彩虹 **隐星 G16 Pro**（NL5 模具，i9-13900HX / RTX 5070 Ti）；**雷神 猎刃 16、神舟 战神 T8 Pro / T10 Pro / T11 Pro / T12 Pro 与它是同一套模具**，EC 布局与热键一致，配置通用（猎刃 16 多一路 SYS 风扇和一颗独立 Logo 灯，程序自动识别）；其余 GMWMI 机型默认仅连接、只读。界面为本地 **WPF 窗口**，直接读取后端状态快照，不经 HTTP。

> ⚠️ 非官方第三方工具，与七彩虹、广达无关；协议逆向自出厂固件。写入 EC 会真实改变硬件行为，**风险自负**。

## 机型支持

| 机型 | 状态 | 说明 |
|---|---|---|
| 七彩虹 隐星 G16 Pro（NL5） | ✅ 已验证 | 全功能：EC 读写、风扇、背光、性能 / 显卡模式 |
| 雷神 猎刃 16 | ✅ 同模具 | 与隐星 G16 Pro 同一模具，但**多一路 SYS 风扇和一颗独立 Logo 灯**（三风扇机型）；程序自动识别，风扇曲线多一条 SYS、背光多一个 Logo 分区 |
| 神舟 战神 T8 Pro / T10 Pro / T11 Pro / T12 Pro | ✅ 同模具 | 广达模具，EC 布局与热键同 NL5，按同一套配置处理 |
| 其它广达 `RW_GMWMI` | 🔌 仅连接 | 通用档案的命令表、温转速偏移与背光分区按 NL5 预置；开启「EC 控制」可读写，读数越界或读不到时由 HWiNFO 兜底。执行「一键采集」可按证据适配本机能力 |

未标定 `ec.layout` 的机型不推断偏移，温转速仅用于显示与历史，**不进入风扇控制**。

## 功能

- **风扇曲线**：自动接管或手动固定；三档各自曲线，CPU / GPU 两路按自身温度调速。默认交回 EC，需开启「自定义风扇」才接管，失败每 30 秒自愈；三风扇机型增加 SYS 一路。
- **性能模式**：办公 / 游戏 / 狂暴，联动 APMD 与电源计划；拔电自动切办公，插回恢复。
- **屏幕亮度**：仅控内建屏，经系统 WMI，无需管理员；亮度随电源计划各自保存。
- **显卡模式**：混合 / 独显直连 / 集显（GFXT，重启生效）。**显卡超频**：核心与显存偏移（NVML，仅 P0 档）。
- **监控与历史**：CPU/GPU 温度、风扇转速、功耗、PCH、显存结温、电池、内存、硬盘 SMART；150 秒滚动曲线。
- **键盘背光**：整条亮度加三分区染色，可自动熄灯；FN 热键带 OSD 提示。
- **托盘、开机自启、版本更新**：双源检查更新，可在程序内下载安装（校验 SHA256 通过才启动安装向导）。

> 设计原则：数据取不到即整行隐藏，不显示占位值；EC 写入需管理员，未提权时明确拦截。

## 下载

[GitHub Releases](https://github.com/czyj1145/Q-Control/releases) ／ [Gitee 镜像](https://gitee.com/czyj1145/Q-Control/releases)，附件含 `SHA256.txt`。

| 文件 | 说明 |
|---|---|
| `QControl-<版本>-setup-win-x64.exe` | 装到 `%ProgramFiles%\Q-Control`，创建开始菜单与桌面快捷方式；配置与日志在 `%ProgramData%\Q-Control` |
| `QControl-<版本>-portable-win-x64.zip` | 解压即用，配置与日志同目录，可整体搬迁 |

两包均为**框架依赖**（本体约 2 MB），需 **.NET 10 桌面运行时**，安装版与绿色版都会代为安装。

- 改风扇 / 性能模式 / 灯效需管理员：**首次启动弹一次 UAC**，顺带装好 HighestAvailable 自启任务，此后不再弹。请从托盘退出，用任务管理器强杀会跳过交还转速。
- 绿色版**无卸载程序**：托盘退出 → 关闭「开机自启」→ 删除目录，顺序不可颠倒。
- 自签名证书可能触发「未知发布者」或 SmartScreen，属预期；版本号未变也可能重打包，**以发布页 SHA256 为准**。

## HWiNFO（可选）

**PCH 温度、CPU/GPU 核心电压、显存结温、CPU PL1·PL2、硬盘 SMART** 仅 HWiNFO 可取；许可不允许随包分发，未配置即整行隐藏。

将便携版 `HWiNFO64.exe` 放入 `HWiNFO\`（安装目录旁或绿色版 `app\`，均已创建）、当前目录向上任一层，或由环境变量 `CC_HWI_DIR` 指定；HWiNFO 自身也需管理员运行。

## 机型适配

从「仅连接」升为「可写」无需命令行：**关于 › 机型适配 › 一键采集**——20~40 秒识别温转速偏移，十余秒验证写入，随后按置顶窗口五步操作原厂控制中心，采集后点「启用本机」生效。仅对引导确证的项开启，报告逐项列缺；`verified` 仍为 `false`，需作者复核后转为正式档案。仅需读数可点「只读采集」。流程与证据要求见 [`docs/DEVICES.md`](docs/DEVICES.md)。

## 参与

接受外部 PR。不涉及硬件写入（界面 / 文档 / 构建 / 测试）复核后即合并；涉及硬件写入（EC 命令 / 风扇曲线 / 背光 / 性能·显卡 / 机型档案）须在真机复核原厂行为。较大改动先开 Issue，一个 PR 只做一件事，`dotnet test` 必须通过。

缺陷、建议与机型采集包走 [Issues](https://github.com/czyj1145/Q-Control/issues)；**安全漏洞请勿公开报告**，渠道见 [`SECURITY.md`](SECURITY.md)。提交日志前将序列号、UUID、主机名与 MAC 替换为 `EXAMPLE-*`。

技术文档：[`docs/PROTOCOL.md`](docs/PROTOCOL.md)（EC 协议速查）、[`docs/EVIDENCE.md`](docs/EVIDENCE.md)（实证与反编译）、[`docs/DEVICES.md`](docs/DEVICES.md)（机型适配）。

## 许可

以 [MIT License](LICENSE) 发布。本项目为非官方第三方工具，与七彩虹、广达及其关联公司无隶属或合作关系；协议可能不完整或随固件而异，错误写入可能影响散热、显示输出或系统稳定性，作者不承担由此产生的损失。
