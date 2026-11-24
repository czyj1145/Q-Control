# 安全策略

Q-Control 以管理员权限运行、直接写入 EC，并在本机监听 HTTP 端口，安全边界如下。

## 支持范围

仅适用于最新发布版本，版本号以 [`ControlCenter/VERSION`](ControlCenter/VERSION) 与 Releases 为准；旧版本请先升级再反馈。

## 报告渠道

请勿通过公开 Issue 报告。GitHub 私密通道：<https://github.com/czyj1145/Q-Control/security/advisories/new>（Security → Report a vulnerability）。可附日志，须先脱敏。Gitee 镜像无此功能。

## 安全问题范围

按影响由高到低：

| 类别 | 具体表现 |
|---|---|
| **EC 写入越界** | 在未标定机型上绕过 `assert_bounds()`、总闸或 `arm()` 写入；或写入 `0x0400` 等禁用命令 |
| **提权面** | 普通权限进程借自启任务取得管理员权限；或只读实例执行写操作 |
| **本地 HTTP 面** | `127.0.0.1:8765` 端点可导致写入、任意文件读取或命令执行；存在 CSRF 风险；或监听非回环地址 |
| **隐私泄漏** | 向外发送序列号、UUID、主机名、MAC 或用户名；日志未脱敏；更新检查除版本号外携带其他信息 |
| **供应链** | 构建脚本、发布载荷或更新检查结果可被替换；签名校验可绕过 |

以下属既定设计，**不属于**安全问题：

- 首次启动请求 UAC：EC 写入必须提权，零 UAC 由 HighestAvailable 计划任务实现。
- 未标定机型读不到温转速：`ec.layout` 未标定，属能力边界。
- 任务管理器强杀后风扇停在最后写入值：跳过退出流程，应走 `stop.cmd` 或托盘退出。
- 自签名证书触发 SmartScreen：见 README「下载」。

## 处理流程

先实机复现并确认影响范围，再决定私密修复发布或转公开讨论。修复发布时致谢报告者，可要求匿名。

## 日志脱敏

提交前须脱敏：`%ProgramData%\Q-Control\logs\cc-<日期>.log`、`/api/hardware` 返回、「关于 › 机型适配」采集结果。涉及整机 / 主板 / 硬盘序列号、SMBIOS UUID、计算机名、MAC、IP、Windows 用户名与安装路径——统一替换为 `EXAMPLE-*`，定位问题不需要真实值。
